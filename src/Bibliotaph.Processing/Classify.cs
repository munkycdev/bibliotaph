using System.Globalization;
using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>API keys for model endpoints that need one. The app keeps them in Windows Credential Manager, never in catalog.db.</summary>
public interface IApiKeyStore
{
    string? Find(string endpoint);

    bool Remember(string endpoint, string key);

    void Forget(string endpoint);
}

public sealed class NoApiKeys : IApiKeyStore
{
    public string? Find(string endpoint) => null;

    public bool Remember(string endpoint, string key) => false;

    public void Forget(string endpoint)
    {
    }
}

/// <summary>
/// How AI is set up: the endpoint and model, whether it is switched on, whether the user agreed to an endpoint off
/// this computer, and the library folders never sent. <see cref="IsReady"/> says whether the Classify lane may send
/// anything. With <see cref="BuiltIn"/>, Bibliotaph runs <see cref="BuiltInModel"/> itself, and the model server's
/// endpoint and model are kept for a switch back. <see cref="EmbeddingModel"/> and
/// <see cref="BuiltInEmbeddingModel"/> are what Ask finds pages by meaning with, on the server or built in; without
/// one, it finds them by their words. <see cref="LibraryVectors"/> says whether the whole library's pages are read for
/// meaning in the background, for questions to the library.
/// </summary>
public sealed record AiSetup(string? Endpoint, string? Model, string Provider, bool Enabled, bool RemoteAllowed, IReadOnlySet<long> SkippedRoots,
    bool BuiltIn = false, string? BuiltInModel = null, string? EmbeddingModel = null, string? BuiltInEmbeddingModel = null, bool LibraryVectors = true)
{
    /// <summary>The provider runs of the built-in AI are recorded under.</summary>
    public const string BuiltInProvider = "built-in";

    public static AiSetup Off { get; } = new(null, null, "", false, false, new HashSet<long>());

    public bool IsLocal => BuiltIn || (Endpoint is not null && LocalModelClient.IsLocal(Endpoint));

    /// <summary>
    /// Set up, switched on, and either built in, on this computer or network, or allowed off it. Never a fallback to
    /// anything else.
    /// </summary>
    public bool IsReady => Enabled && (BuiltIn
        ? BuiltInModel is not null
        : Model is not null && Endpoint is not null && (IsLocal || RemoteAllowed));

    /// <summary>The name runs are recorded under: the model server's name for it, or the built-in model's with " (built in)".</summary>
    public string? ModelName => BuiltIn ? BuiltInModel is null ? null : BuiltInModels.NameFor(BuiltInModel) : Model;

    /// <summary>The provider runs are recorded under.</summary>
    public string RunProvider => BuiltIn ? BuiltInProvider : Provider;

    /// <summary>
    /// The embedding model page vectors are made with and stored under, or null when there is none and Ask finds pages
    /// by their words alone. A built-in one is named as a built-in chat model is, so it never passes for a server's.
    /// </summary>
    public string? EmbeddingName => BuiltIn
        ? BuiltInEmbeddingModel is null ? null : BuiltInModels.NameFor(BuiltInEmbeddingModel)
        : EmbeddingModel;
}

/// <summary>
/// One AI setup and everything made from it. <see cref="AiSettings"/> swaps the whole of it at once, so work that takes
/// <see cref="AiSettings.Current"/> once never pairs one setup's client with another's model name.
/// </summary>
/// <param name="Setup">The setup, as saved.</param>
/// <param name="Classifier">The classifier, or null while AI isn't ready.</param>
/// <param name="Interpreter">Ask's reader of requests in plain English, or null while AI isn't ready.</param>
/// <param name="Chat">The client Ask this book's answers come from, or null while AI isn't ready.</param>
/// <param name="Background">The same client for reading whole books, whose requests don't count as the user's.</param>
/// <param name="Embedder">What makes page vectors, or null while AI isn't ready or there is no embedding model.</param>
/// <param name="BackgroundEmbedder">The same embedder for vectors made in the background, whose requests don't count as the user's.</param>
public sealed record AiState(AiSetup Setup, ModelClassifier? Classifier, ModelPlainSearchInterpreter? Interpreter, IModelClient? Chat,
    IModelClient? Background, IEmbeddingClient? Embedder, IEmbeddingClient? BackgroundEmbedder)
{
    public static AiState Off { get; } = new(AiSetup.Off, null, null, null, null, null, null);
}

/// <summary>
/// AI's settings in catalog.db, loaded once and kept, and the classifier, Ask's interpreter and the embedding client they
/// make. A change raises <see cref="Changed"/>, which wakes or stops the Classify lane and shows or hides Ask this book.
/// </summary>
/// <param name="settings">Where the setup is kept.</param>
/// <param name="keys">The model servers' keys, read when a request is made.</param>
/// <param name="clients">The client for a model server's endpoint; tests and the smoke test pass stand-ins.</param>
/// <param name="builtIn">The built-in AI's chat engine, or null where the app has none (tests).</param>
/// <param name="builtInEmbedding">The built-in AI's second engine, in embedding mode, for finding pages by meaning, or null
/// where the app has none (tests).</param>
/// <param name="activity">Tracks every request a person waits on, so reading whole books gives way to them. Made here when
/// not given, since everything that uses it reaches it through <see cref="Activity"/>; tests give one a shorter quiet period.</param>
public sealed class AiSettings(SettingsStore settings, IApiKeyStore keys, Func<string, IModelClient>? clients = null, BuiltInEngine? builtIn = null,
    BuiltInEngine? builtInEmbedding = null, ModelActivity? activity = null)
{
    /// <summary>A local model can take minutes to load and read a long excerpt on a modest GPU.</summary>
    static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    readonly Func<string, IModelClient> _clients = clients ?? (endpoint => ClientFor(endpoint, keys));
    volatile AiState _state = AiState.Off;
    volatile bool _scanWholeBooks = true;

    /// <summary>The usual client for a model server: the shared HttpClient, with the endpoint's key read when a request is made.</summary>
    public static IModelClient ClientFor(string endpoint, IApiKeyStore keys) => new LocalModelClient(Http.Value, endpoint, () => keys.Find(endpoint));

    public event EventHandler? Changed;

    /// <summary>Raised when "Scan whole books" is switched on or off.</summary>
    public event EventHandler? ScanWholeBooksChanged;

    /// <summary>The setup and what it made, together. Work that uses more than one of them takes this once.</summary>
    public AiState Current => _state;

    public AiSetup Setup => _state.Setup;

    /// <summary>When the AI is busy with something a person is waiting on. Every client made here counts its requests.</summary>
    public ModelActivity Activity { get; } = activity ?? new ModelActivity();

    /// <summary>
    /// Whether books are read whole, chapter by chapter, once they are catalogued: on unless the user switched it off,
    /// and only ever while AI itself is on.
    /// </summary>
    public bool ScanWholeBooks => _scanWholeBooks;

    /// <summary>
    /// The client reading whole books asks: the same model and server as the classifier, but its requests don't count
    /// as the user's, since they are the ones that give way. Null while AI isn't ready.
    /// </summary>
    public IModelClient? Background => _state.Background;

    /// <summary>The classifier for the current setup, or null while AI isn't ready.</summary>
    public ModelClassifier? Classifier => _state.Classifier;

    /// <summary>
    /// What makes Ask this book's page vectors for the current setup: the model server, when an embedding model is
    /// set for it, or the built-in embedding engine. Null while AI isn't ready or there is no embedding model.
    /// </summary>
    public IEmbeddingClient? Embedder => _state.Embedder;

    /// <summary>
    /// The same embedder for vectors made in the background, whose requests don't count as the user's: they give way
    /// on their own, and counting them would hold a whole-book scan off for as long as they run.
    /// </summary>
    public IEmbeddingClient? BackgroundEmbedder => _state.BackgroundEmbedder;

    /// <summary>
    /// The client Ask this book's answers come from: the model server, or the built-in AI, as the classifier uses.
    /// Null while AI isn't ready.
    /// </summary>
    public IModelClient? Chat => _state.Chat;

    /// <summary>Ask's reader of requests in plain English, over the same model, or null while AI isn't ready.</summary>
    public ModelPlainSearchInterpreter? Interpreter => _state.Interpreter;

    public IModelClient ClientFor(string endpoint) => Activity.Track(_clients(endpoint));

    /// <summary>The built-in AI's engine, or null where the app has none (tests).</summary>
    public BuiltInEngine? BuiltIn => builtIn;

    /// <summary>The client for the built-in AI on <paramref name="model"/>: a model's id from <see cref="BuiltInModels"/>, or a file's path.</summary>
    public IModelClient BuiltInClientFor(string model) => Activity.Track(UntrackedBuiltInClientFor(model));

    BuiltInModelClient UntrackedBuiltInClientFor(string model)
    {
        var engine = builtIn ?? throw new InvalidOperationException("This app has no built-in AI engine.");
        return new BuiltInModelClient(engine, Http.Value, BuiltInModels.PathFor(model, engine.Options.ModelsFolder));
    }

    /// <summary>Switches reading whole books on or off, and keeps the choice.</summary>
    public async Task SetScanWholeBooksAsync(bool on, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.AiScanWholeBooks, on.ToString(), ct);
        if (_scanWholeBooks == on) return;
        _scanWholeBooks = on;
        ScanWholeBooksChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var skipped = await settings.GetAsync(SettingKeys.AiSkippedRoots, ct) is { } json ? JsonSerializer.Deserialize<long[]>(json) ?? [] : [];
        Apply(new AiSetup(
            await settings.GetAsync(SettingKeys.AiEndpoint, ct),
            await settings.GetAsync(SettingKeys.AiModel, ct),
            await settings.GetAsync(SettingKeys.AiProvider, ct) ?? "",
            await settings.GetAsync(SettingKeys.AiEnabled, ct) == bool.TrueString,
            await settings.GetAsync(SettingKeys.AiRemoteAllowed, ct) == bool.TrueString,
            skipped.ToHashSet(),
            await settings.GetAsync(SettingKeys.AiBuiltIn, ct) == bool.TrueString,
            await settings.GetAsync(SettingKeys.AiBuiltInModel, ct),
            await settings.GetAsync(SettingKeys.AiEmbeddingModel, ct),
            await settings.GetAsync(SettingKeys.AiBuiltInEmbeddingModel, ct),
            await settings.GetAsync(SettingKeys.AiLibraryVectors, ct) != bool.FalseString));
        _scanWholeBooks = await settings.GetAsync(SettingKeys.AiScanWholeBooks, ct) != bool.FalseString;
    }

    public async Task SaveAsync(AiSetup setup, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.AiEndpoint, setup.Endpoint ?? "", ct);
        await settings.SetAsync(SettingKeys.AiModel, setup.Model ?? "", ct);
        await settings.SetAsync(SettingKeys.AiProvider, setup.Provider, ct);
        await settings.SetAsync(SettingKeys.AiEnabled, setup.Enabled.ToString(), ct);
        await settings.SetAsync(SettingKeys.AiRemoteAllowed, setup.RemoteAllowed.ToString(), ct);
        await settings.SetAsync(SettingKeys.AiSkippedRoots, JsonSerializer.Serialize(setup.SkippedRoots.Order()), ct);
        await settings.SetAsync(SettingKeys.AiBuiltIn, setup.BuiltIn.ToString(), ct);
        await settings.SetAsync(SettingKeys.AiBuiltInModel, setup.BuiltInModel ?? "", ct);
        await settings.SetAsync(SettingKeys.AiEmbeddingModel, setup.EmbeddingModel ?? "", ct);
        await settings.SetAsync(SettingKeys.AiBuiltInEmbeddingModel, setup.BuiltInEmbeddingModel ?? "", ct);
        await settings.SetAsync(SettingKeys.AiLibraryVectors, setup.LibraryVectors.ToString(), ct);
        Apply(setup);
    }

    /// <summary>Makes the classifier again for the same setup, and wakes the Classify lane: after a built-in model downloads, say.</summary>
    public void Refresh() => Apply(_state.Setup);

    void Apply(AiSetup setup)
    {
        setup = setup with
        {
            Endpoint = Blank(setup.Endpoint),
            Model = Blank(setup.Model),
            BuiltInModel = Blank(setup.BuiltInModel),
            EmbeddingModel = Blank(setup.EmbeddingModel),
            BuiltInEmbeddingModel = Blank(setup.BuiltInEmbeddingModel),
        };
        var untracked = !setup.IsReady ? null
            : setup.BuiltIn ? builtIn is null ? null : UntrackedBuiltInClientFor(setup.BuiltInModel!)
            : _clients(setup.Endpoint!);
        var client = untracked is null ? null : Activity.Track(untracked);
        // The same endpoint as the chat model, never another: pages go only where Settings says.
        // A built-in one is used once its file is here; the finished download refreshes this.
        var embedder = !setup.IsReady ? null
            : setup.BuiltIn
                ? builtInEmbedding is null || setup.BuiltInEmbeddingModel is null
                    || BuiltInModels.PathFor(setup.BuiltInEmbeddingModel, builtInEmbedding.Options.ModelsFolder) is not { } path || !File.Exists(path) ? null
                    : new BuiltInEmbeddingClient(builtInEmbedding, Http.Value, path)
            : setup.EmbeddingModel is null ? null : _clients(setup.Endpoint!) as IEmbeddingClient;
        _state = new AiState(setup,
            client is null ? null : new ModelClassifier(client, setup.RunProvider, setup.ModelName!),
            client is null ? null : new ModelPlainSearchInterpreter(client, setup.ModelName!),
            client, untracked, embedder is null ? null : Activity.Track(embedder), embedder);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>
/// Finds the worked examples for a book: the user's books with values they confirmed, the most alike first
/// (<see cref="WorkedExamples"/>), never one with a file in a library folder kept from AI.
/// </summary>
public sealed class ExampleFinder(MetadataStore metadata, LibraryStore library)
{
    /// <summary>Up to <see cref="ClassifierPrompt.MaxExamples"/> examples for <paramref name="target"/>; none when nothing alike is confirmed.</summary>
    public async Task<IReadOnlyList<WorkedExample>> FindAsync(EntryId target, IReadOnlySet<long> keptFromAi, Vocabulary vocabulary, CancellationToken ct = default)
    {
        var decided = (await metadata.GetDecidedEntryIdsAsync(ct)).Where(e => e != target).ToList();
        if (decided.Count == 0) return [];
        var all = await metadata.GetManyAsync([.. decided, target], ct);
        var folders = (await library.GetEntryFoldersAsync([.. decided, target], ct)).ToLookup(f => f.EntryId);
        var mine = all.TryGetValue(target, out var own) ? own.Compute() : EffectiveMetadata.Empty;
        var wanted = new ExampleTarget(target, [.. folders[target].Where(f => f.Present).Select(f => f.Folder).Distinct()],
            mine[MetadataFields.System].First?.Value, mine[MetadataFields.Publisher].First?.Value);
        var books = decided.Where(all.ContainsKey).Select(e => new ExampleBook(e,
            [.. folders[e].Where(f => f.Present).Select(f => f.Folder).Distinct()],
            folders[e].Any(f => keptFromAi.Contains(f.Folder.RootId)),
            all[e].Compute()));
        return WorkedExamples.Choose(wanted, books, vocabulary);
    }
}

/// <summary>
/// Builds what the classifier reads for a document from index.db, or says why there is nothing to read, and the
/// worked examples sent beside it.
/// </summary>
public sealed class ClassifierInputs(LibraryStore library, IndexQueries queries, VocabularyStore vocabularies, EntryStore entries, ExampleFinder examples)
{
    /// <param name="documentId">The book to read.</param>
    /// <param name="keptFromAi">The library folders kept from AI, whose books are never sent as examples.</param>
    /// <param name="withExamples">False for the model pilot, whose runs must read the same thing whatever the user has confirmed since.</param>
    /// <param name="pages">The pages of an adventure inside it, read alone: an excerpt of those pages, chosen as a
    /// book's would be from its own opening, and only their text for the evidence check. Page markers stay the file's,
    /// so evidence points at the file's pages.</param>
    /// <param name="ct">Cancels the reading.</param>
    public async Task<(ClassifierInput? Input, string? Why)> BuildAsync(long documentId, IReadOnlySet<long>? keptFromAi = null, bool withExamples = true,
        PartPages? pages = null, CancellationToken ct = default)
    {
        var path = await library.GetRelativePathAsync(documentId, ct);
        var texts = await queries.GetPageTextsAsync(documentId, ct);
        if (pages is { } part) texts = [.. texts.Where(p => part.Contains(p.PdfPage))];
        if (texts.Count == 0) return (null, "There is no text to read.");
        var outline = await queries.GetOutlineAsync(documentId, ct);
        var count = await queries.GetPageCountAsync(documentId, ct) ?? texts[^1].PdfPage + 1;
        // An adventure's pages are read as if they were a book of their own, from its first page.
        var offset = pages?.First ?? 0;
        var excerpt = ExcerptBuilder.Build([.. texts.Select(p => new BookPage(p.PdfPage - offset, p.Text))],
            [.. outline.Where(o => pages is not { } range || range.Contains(o.PdfPage)).Select(o => new BookHeading(o.Title, o.PdfPage - offset, o.Depth))],
            pages?.Count ?? count);
        if (excerpt.IsEmpty) return (null, "There is too little text to read.");
        if (pages is not null)
            excerpt = new Excerpt([.. excerpt.Pages.Select(p => p with { PdfPage = p.PdfPage + offset })],
                [.. excerpt.Headings.Select(h => h with { PdfPage = h.PdfPage + offset })], count);
        var byPage = texts.ToDictionary(p => p.PdfPage, p => p.Text);
        var fileName = path is null ? "" : Path.GetFileName(path);
        var vocabulary = await vocabularies.GetAsync(ct);
        IReadOnlyList<WorkedExample> worked = withExamples && await entries.GetEntryAsync(documentId, ct) is { } entry
            ? await examples.FindAsync(entry.EntryId, keptFromAi ?? new HashSet<long>(), vocabulary, ct)
            : [];
        return (new ClassifierInput(excerpt, fileName, page => byPage.GetValueOrDefault(page), vocabulary, worked), null);
    }

    /// <summary>
    /// What a model reads to find the adventures in a document, or null when its bookmarks don't offer two candidates
    /// with text.
    /// </summary>
    public async Task<PartFinderInput?> BuildPartFinderAsync(long documentId, CancellationToken ct = default)
    {
        var outline = await queries.GetOutlineAsync(documentId, ct);
        if (outline.Count < PartRules.MinimumParts) return null;
        var texts = await queries.GetPageTextsAsync(documentId, ct);
        if (texts.Count == 0) return null;
        var count = await queries.GetPageCountAsync(documentId, ct) ?? texts[^1].PdfPage + 1;
        var path = await library.GetRelativePathAsync(documentId, ct);
        return PartFinderInput.Build(path is null ? "" : Path.GetFileName(path), [.. texts.Select(p => new BookPage(p.PdfPage, p.Text))],
            [.. outline.Select(o => new BookHeading(o.Title, o.PdfPage, o.Depth))], count);
    }
}

/// <summary>
/// Turns a classifier's checked claims into stored suggestions: a new term becomes a pending term (and its values
/// wait for the user's decision on it), a term the user rejected is dropped, and the run is recorded with its pages.
/// </summary>
public sealed class ClassificationResults(ClassificationStore runs, EntryStore entries, MetadataStore metadata, VocabularyStore vocabularies,
    MetadataProjector projector)
{
    /// <summary>Stores a run over one copy of a book, and its suggestions on the book's entry. Returns the run's id.</summary>
    public async Task<string> StoreAsync(long documentId, string contentHash, ClassifierInput input, ClassifierResult result, DateTime startedUtc, CancellationToken ct = default) =>
        await StoreOnAsync(await EntryOfAsync(documentId, ct), contentHash, input, result, startedUtc, ct);

    /// <summary>
    /// Stores a run over an adventure's own pages in one copy of its file, and its suggestions on the adventure's card.
    /// Returns the run's id.
    /// </summary>
    public async Task<string> StoreOnAsync(EntryId entry, string contentHash, ClassifierInput input, ClassifierResult result, DateTime startedUtc, CancellationToken ct = default)
    {
        var proposals = new List<MetadataProposal>();
        foreach (var claim in result.Accepted)
        {
            var value = claim.Value;
            if (claim.IsNewTerm)
            {
                var (term, state) = await vocabularies.ProposeTermAsync(claim.Field.Vocabulary!, claim.Value, ct);
                if (state == TermState.Rejected) continue;
                value = term.Key;
            }
            proposals.Add(new MetadataProposal(claim.Field, value, AssertionOrigin.Ai, claim.Quote, [claim.PdfPage], claim.FromSampling));
        }
        var runId = await runs.RecordAsync(new RunRecord(entry, contentHash, result.Provider, result.Model, ClassifierPrompt.Version,
            ClassifierPrompt.SchemaVersion, input.Excerpt.PdfPages, startedUtc, ClassificationStore.Complete), ct);
        await metadata.ApplyRunAsync(entry, contentHash, runId, AssertionOrigin.Ai, proposals, ct);
        await projector.ProjectAsync([entry], ct);
        return runId;
    }

    /// <summary>Records a run whose answer couldn't be used, and why.</summary>
    public async Task RecordFailedAsync(long documentId, string contentHash, string provider, string model, ClassifierInput input, DateTime startedUtc,
        string problem, CancellationToken ct = default) =>
        await RecordFailedOnAsync(await EntryOfAsync(documentId, ct), contentHash, provider, model, input, startedUtc, problem, ct);

    /// <summary>Records a run over an adventure's own pages whose answer couldn't be used, and why.</summary>
    public async Task RecordFailedOnAsync(EntryId entry, string contentHash, string provider, string model, ClassifierInput input, DateTime startedUtc,
        string problem, CancellationToken ct = default) =>
        await runs.RecordAsync(new RunRecord(entry, contentHash, provider, model, ClassifierPrompt.Version,
            ClassifierPrompt.SchemaVersion, input.Excerpt.PdfPages, startedUtc, problem), ct);

    async Task<EntryId> EntryOfAsync(long documentId, CancellationToken ct) =>
        (await entries.GetEntryAsync(documentId, ct))?.EntryId ?? throw new InvalidOperationException($"Document {documentId} has no entry.");
}

/// <summary>
/// Classify: sends a bounded excerpt of the book to the configured model and stores the values whose evidence checks
/// out as AI suggestions. Runs in its own lane, one book at a time, only while AI is ready; waits for the book's OCR;
/// skips books in a folder the user keeps from AI, and books this model and prompt have already read. When the
/// endpoint can't be used, the lane waits with a notice instead of failing books. A book the server fails on while
/// it still answers others fails like an unusable answer, so it can't hold up the books behind it.
/// </summary>
/// <remarks>
/// After a book's own values, the model is asked which of its bookmarks are self-contained adventures, when the rules
/// proposed nothing; what passes the evidence check is a proposal for Needs review. Each adventure marked in the file
/// is then classified on its own pages, onto its own card, once per model and prompt.
/// </remarks>
public sealed class ClassifyStage(
    AiSettings ai, LibraryStore library, IndexQueries queries, ClassifierInputs inputs, ClassificationStore runs, ClassificationResults results,
    AiReadingFeed feed, PartsService parts, PartProposalService proposals, TimeProvider? clock = null, ILogger<ClassifyStage>? log = null) : IGatedStage
{
    /// <summary>
    /// How long a book whose scanned pages are still being read waits before Classify looks again. It looks sooner when
    /// OCR ends, since the job board wakes a book's deferred jobs when another of its stages finishes.
    /// </summary>
    public static readonly TimeSpan OcrWait = TimeSpan.FromMinutes(5);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<ClassifyStage>.Instance;

    public Stage Stage => Stage.Classify;

    public bool IsReady => ai.Classifier is not null;

    public event EventHandler? ReadyChanged
    {
        add => ai.Changed += value;
        remove => ai.Changed -= value;
    }

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var state = ai.Current;
        if (state.Classifier is not { } classifier) return new StageOutcome.Unavailable("AI is switched off.");
        var kept = state.Setup.SkippedRoots;
        if (kept.Count > 0 && (await library.GetRootIdsAsync(job.DocumentId, ct)).Any(kept.Contains))
            return new StageOutcome.Done(StageStatus.Skipped, [], "Its library folder isn't sent to AI.");
        var read = await runs.HasRunAsync(job.ContentHash, classifier.Model, ClassifierPrompt.Version, ct);
        var unread = await UnreadPartsAsync(job.DocumentId, classifier.Model, ct);
        if (read && unread.Count == 0) return StageOutcome.Complete();
        if (await queries.GetStageStatusAsync(job.DocumentId, Stage.Ocr, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(OcrWait, "Waiting for its scanned pages to be read.");
        // The rules' proposal of its adventures comes with the hints, and a model is asked only when they found none.
        if (!read && await queries.GetStageStatusAsync(job.DocumentId, Stage.RuleHints, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(MatchStage.HintsWait, "Waiting for its hints from names.");

        if (!read)
        {
            var (input, why) = await inputs.BuildAsync(job.DocumentId, kept, ct: ct);
            if (input is null) return new StageOutcome.Done(StageStatus.Skipped, [], why);
            var title = await queries.GetTitleAsync(job.DocumentId, ct) ?? input.FileName;
            if (await ReadAsync(job, classifier, input, title, null, ct) is { } ended) return ended;
            await FindPartsAsync(job.DocumentId, classifier, ct);
        }

        // Each adventure is read on its own pages; one the model fails on is tried again, after the others.
        StageOutcome? failed = null;
        foreach (var part in unread)
        {
            var (input, _) = await inputs.BuildAsync(job.DocumentId, kept, pages: part.Pages, ct: ct);
            if (input is null) continue;
            switch (await ReadAsync(job, classifier, input, part.Title, part.EntryId, ct))
            {
                case StageOutcome.Failed problem:
                    failed ??= problem;
                    break;
                case { } ended:
                    return ended;
            }
        }
        return failed ?? StageOutcome.Complete();
    }

    /// <summary>
    /// Sends one excerpt, the book's or an adventure's (<paramref name="part"/>), and stores what passes the evidence
    /// check on its card. Returns null once stored, or what the job ends with.
    /// </summary>
    async Task<StageOutcome?> ReadAsync(JobRecord job, ModelClassifier classifier, ClassifierInput input, string title, EntryId? part, CancellationToken ct)
    {
        var started = _clock.GetUtcNow().UtcDateTime;
        // The AI reading page follows the answer as it streams.
        var progress = feed.Start(job.DocumentId, title, classifier.Model);
        ClassifierResult result;
        try
        {
            result = await classifier.ClassifyAsync(input, progress, ct);
        }
        catch (ModelEndpointException ex)
        {
            feed.Abandon(job.DocumentId);
            return new StageOutcome.Unavailable(ex.Message);
        }
        catch (ClassifierAnswerException ex)
        {
            feed.Fail(job.DocumentId, ex.Message);
            if (part is { } failedPart)
                await results.RecordFailedOnAsync(failedPart, job.ContentHash, classifier.Provider, classifier.Model, input, started, ex.Message, CancellationToken.None);
            else
                await results.RecordFailedAsync(job.DocumentId, job.ContentHash, classifier.Provider, classifier.Model, input, started, ex.Message, CancellationToken.None);
            return new StageOutcome.Failed(ex.Message, Retry: true);
        }
        catch (OperationCanceledException)
        {
            feed.Abandon(job.DocumentId);
            throw;
        }
        feed.Finish(job.DocumentId, result);

        // Quotes are book text, so only counts and reasons go to the log.
        _log.LogInformation("Classified document {DocumentId}{Part} with {Model} in {Seconds:0.0} s, {Examples} worked examples: {Accepted} values kept, {Dropped} dropped ({Reasons})",
            job.DocumentId, part is { } p ? $" (adventure {p.Value.ToString(CultureInfo.InvariantCulture)})" : "", classifier.Model, (_clock.GetUtcNow().UtcDateTime - started).TotalSeconds,
            input.Examples?.Count ?? 0, result.Accepted.Count, result.Dropped.Count,
            string.Join("; ", result.Dropped.GroupBy(d => d.Reason).Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} × {g.Key}")));
        if (part is { } partId) await results.StoreOnAsync(partId, job.ContentHash, input, result, started, CancellationToken.None);
        else await results.StoreAsync(job.DocumentId, job.ContentHash, input, result, started, CancellationToken.None);
        return null;
    }

    /// <summary>The adventures with pages in this document that this model and prompt haven't read on their own pages, from any copy.</summary>
    async Task<IReadOnlyList<(EntryId EntryId, string Title, PartPages Pages)>> UnreadPartsAsync(long documentId, string model, CancellationToken ct)
    {
        var unread = new List<(EntryId, string, PartPages)>();
        foreach (var part in await parts.GetInDocumentAsync(documentId, ct))
            if (part.Pages is { } pages && !await runs.HasRunOnAsync(part.EntryId, model, ClassifierPrompt.Version, ct))
                unread.Add((part.EntryId, part.Title, pages));
        return unread;
    }

    /// <summary>
    /// Asks the model which of the book's bookmarks are self-contained adventures, when nothing has been proposed for
    /// it; those that pass the evidence check wait in Needs review. A failure here costs only the proposal: the book's
    /// own values are stored, and it is asked again when it is reclassified.
    /// </summary>
    async Task FindPartsAsync(long documentId, ModelClassifier classifier, CancellationToken ct)
    {
        if (!await proposals.WantsModelAsync(documentId, ct)) return;
        if (await inputs.BuildPartFinderAsync(documentId, ct) is not { } input) return;
        try
        {
            var found = await classifier.FindPartsAsync(input, ct);
            _log.LogInformation("Asked {Model} for the adventures in document {DocumentId}: {Accepted} kept, {Dropped} dropped ({Reasons})",
                classifier.Model, documentId, found.Accepted.Count, found.Dropped.Count,
                string.Join("; ", found.Dropped.GroupBy(d => d.Reason).Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} × {g.Key}")));
            await proposals.ProposeFromModelAsync(documentId, found.Accepted, CancellationToken.None);
        }
        catch (Exception ex) when (ex is ModelEndpointException or ClassifierAnswerException)
        {
            _log.LogWarning("Couldn't ask {Model} for the adventures in document {DocumentId}: {Problem}", classifier.Model, documentId, ex.Message);
        }
    }
}

/// <summary>What Settings > AI's Test button shows: the book it read, what it kept with its evidence, and what it dropped.</summary>
public sealed record AiTestResult(string Title, TimeSpan Took, ClassifierResult? Result, string? Problem);

/// <summary>How far a test has got, so the test dialog can say what it is waiting for.</summary>
public enum AiTestStep
{
    /// <summary>Picking a book with text from the library.</summary>
    Choosing,

    /// <summary>Reading the book's pages into an excerpt.</summary>
    Reading,

    /// <summary>Waiting for the model's answer, which takes longest while the server loads the model.</summary>
    Asking,
}

/// <summary>A test's step, with the book once it is chosen.</summary>
public sealed record AiTestProgress(AiTestStep Step, string? Title);

/// <summary>
/// Settings > AI's work: connecting to an endpoint and listing its models, saving the setup, the Test button, and
/// "Reclassify N documents". Nothing here reclassifies by itself.
/// </summary>
public sealed class AiService(
    AiSettings settings, IApiKeyStore keys, IndexingService indexing, ClassificationStore runs, ClassifierInputs inputs, IndexQueries queries,
    LibraryStore library, VocabularyStore vocabularies, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public AiSetup Setup => settings.Setup;

    public IApiKeyStore Keys => keys;

    /// <summary>The vocabulary, for labelling a test's term values.</summary>
    public Task<Vocabulary> VocabularyAsync(CancellationToken ct = default) => vocabularies.GetAsync(ct);

    /// <summary>Finds out what is at <paramref name="endpoint"/> and lists its models.</summary>
    /// <exception cref="ModelEndpointException">Nothing usable answered.</exception>
    public Task<EndpointInfo> ConnectAsync(string endpoint, CancellationToken ct = default) => settings.ClientFor(endpoint).ConnectAsync(ct);

    /// <summary>
    /// Saves the setup. Books in a folder that was skipped and isn't any more are given to the Classify lane again,
    /// which passes over those already done.
    /// </summary>
    public async Task SaveAsync(AiSetup setup, CancellationToken ct = default)
    {
        var before = settings.Setup;
        await settings.SaveAsync(setup, ct);
        if (before.SkippedRoots.Except(setup.SkippedRoots).Any()) await indexing.RerunAsync(Stage.Classify, ct);
    }

    /// <summary>How many books the current model and prompt have classified, and how many only an earlier one did.</summary>
    public Task<ClassificationSummary> SummarizeAsync(CancellationToken ct = default) =>
        settings.Setup.ModelName is { } model ? runs.SummarizeAsync(model, ClassifierPrompt.Version, ct) : Task.FromResult(new ClassificationSummary(0, 0));

    /// <summary>
    /// "Reclassify N documents": every book goes back to the Classify lane, which reads again those the current model
    /// and prompt haven't, and those that failed. Values the user decided stay as they are.
    /// </summary>
    public Task<int> ReclassifyAsync(CancellationToken ct = default) => indexing.RerunAsync(Stage.Classify, ct);

    /// <summary>Classifies one book from the library with the setup on screen, and stores nothing.</summary>
    public Task<AiTestResult> TestAsync(string endpoint, string model, IProgress<AiTestProgress>? progress = null, CancellationToken ct = default) =>
        TestAsync(() => settings.ClientFor(endpoint), model, progress, ct);

    /// <summary>
    /// Classifies one book with the built-in AI on <paramref name="model"/> (an id from <see cref="BuiltInModels"/>),
    /// starting the engine if it isn't running, and stores nothing.
    /// </summary>
    public Task<AiTestResult> TestBuiltInAsync(string model, IProgress<AiTestProgress>? progress = null, CancellationToken ct = default) =>
        TestAsync(() => settings.BuiltInClientFor(model), BuiltInModels.NameFor(model), progress, ct);

    async Task<AiTestResult> TestAsync(Func<IModelClient> clientFor, string model, IProgress<AiTestProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new AiTestProgress(AiTestStep.Choosing, null));
        var kept = settings.Setup.SkippedRoots;
        var skipped = await library.GetDocumentIdsInRootsAsync([.. kept], ct);
        if (await queries.FindSampleAsync(skipped, ct: ct) is not { } documentId)
            return new AiTestResult("", TimeSpan.Zero, null, "There's no book with text to try yet. Add a folder, or wait for its books to be read.");
        var title = await queries.GetTitleAsync(documentId, ct) ?? "a book";
        progress?.Report(new AiTestProgress(AiTestStep.Reading, title));
        var (input, why) = await inputs.BuildAsync(documentId, kept, ct: ct);
        if (input is null) return new AiTestResult(title, TimeSpan.Zero, null, why);

        var client = clientFor();
        var started = _clock.GetTimestamp();
        progress?.Report(new AiTestProgress(AiTestStep.Asking, title));
        try
        {
            var info = await client.ConnectAsync(ct);
            var result = await new ModelClassifier(client, info.Provider, model).ClassifyAsync(input, ct: ct);
            return new AiTestResult(title, _clock.GetElapsedTime(started), result, null);
        }
        catch (Exception ex) when (ex is ModelEndpointException or ClassifierAnswerException)
        {
            return new AiTestResult(title, _clock.GetElapsedTime(started), null, ex.Message);
        }
    }
}
