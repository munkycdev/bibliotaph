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
/// How AI is set up (choice 10): the endpoint and model, whether it is switched on, whether the user agreed to an
/// endpoint off this computer, and the library folders never sent. <see cref="IsReady"/> says whether the Classify lane
/// may send anything. With <see cref="BuiltIn"/>, Bibliotaph runs <see cref="BuiltInModel"/> itself (Built-in AI plan,
/// choice 7), and the model server's endpoint and model are kept for a switch back. <see cref="EmbeddingModel"/> and
/// <see cref="BuiltInEmbeddingModel"/> are what Ask this book finds pages by meaning with (choice 3), on the server or
/// built in; without one, it finds them by their words.
/// </summary>
public sealed record AiSetup(string? Endpoint, string? Model, string Provider, bool Enabled, bool RemoteAllowed, IReadOnlySet<long> SkippedRoots,
    bool BuiltIn = false, string? BuiltInModel = null, string? EmbeddingModel = null, string? BuiltInEmbeddingModel = null)
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
/// AI's settings in catalog.db, loaded once and kept, and the classifier and embedding client they make. A change raises
/// <see cref="Changed"/>, which wakes or stops the Classify lane and shows or hides Ask this book.
/// </summary>
/// <param name="settings">Where the setup is kept.</param>
/// <param name="keys">The model servers' keys, read when a request is made.</param>
/// <param name="clients">The client for a model server's endpoint; tests and the smoke test pass stand-ins.</param>
/// <param name="builtIn">The built-in AI's chat engine, or null where the app has none (tests).</param>
/// <param name="builtInEmbedding">The built-in AI's second engine, in embedding mode, for finding pages by meaning.</param>
public sealed class AiSettings(SettingsStore settings, IApiKeyStore keys, Func<string, IModelClient>? clients = null, BuiltInEngine? builtIn = null,
    BuiltInEngine? builtInEmbedding = null)
{
    /// <summary>A local model can take minutes to load and read a long excerpt on a modest GPU.</summary>
    static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

    readonly Func<string, IModelClient> _clients = clients ?? (endpoint => ClientFor(endpoint, keys));
    volatile AiSetup _setup = AiSetup.Off;
    volatile IClassifier? _classifier;
    volatile IEmbeddingClient? _embedder;
    volatile IModelClient? _chat;

    /// <summary>The usual client for a model server: the shared HttpClient, with the endpoint's key read when a request is made.</summary>
    public static IModelClient ClientFor(string endpoint, IApiKeyStore keys) => new LocalModelClient(Http.Value, endpoint, () => keys.Find(endpoint));

    public event EventHandler? Changed;

    public AiSetup Setup => _setup;

    /// <summary>The classifier for the current setup, or null while AI isn't ready.</summary>
    public IClassifier? Classifier => _classifier;

    /// <summary>
    /// What makes Ask this book's page vectors for the current setup (choice 3): the model server, when an embedding
    /// model is set for it, or the built-in embedding engine. Null while AI isn't ready or there is no embedding model.
    /// </summary>
    public IEmbeddingClient? Embedder => _embedder;

    /// <summary>
    /// The client Ask this book's answers come from (Ask plan, step 4): the model server, or the built-in AI, as the
    /// classifier uses. Null while AI isn't ready.
    /// </summary>
    public IModelClient? Chat => _chat;

    public IModelClient ClientFor(string endpoint) => _clients(endpoint);

    /// <summary>The built-in AI's engine, or null where the app has none (tests).</summary>
    public BuiltInEngine? BuiltIn => builtIn;

    /// <summary>The client for the built-in AI on <paramref name="model"/>: a model's id from <see cref="BuiltInModels"/>, or a file's path.</summary>
    public IModelClient BuiltInClientFor(string model)
    {
        var engine = builtIn ?? throw new InvalidOperationException("This app has no built-in AI engine.");
        return new BuiltInModelClient(engine, Http.Value, BuiltInModels.PathFor(model, engine.Options.ModelsFolder));
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
            await settings.GetAsync(SettingKeys.AiBuiltInEmbeddingModel, ct)));
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
        Apply(setup);
    }

    /// <summary>Makes the classifier again for the same setup, and wakes the Classify lane: after a built-in model downloads, say.</summary>
    public void Refresh() => Apply(_setup);

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
        _setup = setup;
        _classifier = !setup.IsReady ? null
            : setup.BuiltIn ? builtIn is null ? null : new ModelClassifier(BuiltInClientFor(setup.BuiltInModel!), setup.RunProvider, setup.ModelName!)
            : new ModelClassifier(_clients(setup.Endpoint!), setup.Provider, setup.Model!);
        _chat = !setup.IsReady ? null
            : setup.BuiltIn ? builtIn is null ? null : BuiltInClientFor(setup.BuiltInModel!)
            : _clients(setup.Endpoint!);
        // The same endpoint as the chat model, never another: pages go only where Settings says (choice 12).
        // A built-in one is used once its file is here; the finished download refreshes this.
        _embedder = !setup.IsReady ? null
            : setup.BuiltIn
                ? builtInEmbedding is null || setup.BuiltInEmbeddingModel is null
                    || BuiltInModels.PathFor(setup.BuiltInEmbeddingModel, builtInEmbedding.Options.ModelsFolder) is not { } path || !File.Exists(path) ? null
                    : new BuiltInEmbeddingClient(builtInEmbedding, Http.Value, path)
            : setup.EmbeddingModel is null ? null : _clients(setup.Endpoint!) as IEmbeddingClient;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Builds what the classifier reads for a document from index.db, or says why there is nothing to read.</summary>
public sealed class ClassifierInputs(LibraryStore library, IndexQueries queries, VocabularyStore vocabularies)
{
    public async Task<(ClassifierInput? Input, string? Why)> BuildAsync(long documentId, CancellationToken ct = default)
    {
        var path = await library.GetRelativePathAsync(documentId, ct);
        var pages = await queries.GetPageTextsAsync(documentId, ct);
        if (pages.Count == 0) return (null, "There is no text to read.");
        var outline = await queries.GetOutlineAsync(documentId, ct);
        var count = await queries.GetPageCountAsync(documentId, ct) ?? pages[^1].PdfPage + 1;
        var excerpt = ExcerptBuilder.Build([.. pages.Select(p => new BookPage(p.PdfPage, p.Text))],
            [.. outline.Select(o => new BookHeading(o.Title, o.PdfPage, o.Depth))], count);
        if (excerpt.IsEmpty) return (null, "There is too little text to read.");
        var byPage = pages.ToDictionary(p => p.PdfPage, p => p.Text);
        var fileName = path is null ? "" : Path.GetFileName(path);
        return (new ClassifierInput(excerpt, fileName, page => byPage.GetValueOrDefault(page), await vocabularies.GetAsync(ct)), null);
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
    public async Task<string> StoreAsync(long documentId, string contentHash, ClassifierInput input, ClassifierResult result, DateTime startedUtc, CancellationToken ct = default)
    {
        var entry = await EntryOfAsync(documentId, ct);
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
        await runs.RecordAsync(new RunRecord(await EntryOfAsync(documentId, ct), contentHash, provider, model, ClassifierPrompt.Version,
            ClassifierPrompt.SchemaVersion, input.Excerpt.PdfPages, startedUtc, problem), ct);

    async Task<EntryId> EntryOfAsync(long documentId, CancellationToken ct) =>
        (await entries.GetEntryAsync(documentId, ct))?.EntryId ?? throw new InvalidOperationException($"Document {documentId} has no entry.");
}

/// <summary>
/// Classify: sends a bounded excerpt of the book to the configured model and stores the values whose evidence checks
/// out as AI suggestions. Runs in its own lane, one book at a time, only while AI is ready; waits for the book's OCR;
/// skips books in a folder the user keeps from AI, and books this model and prompt have already read. When the
/// endpoint can't be used, the lane waits with a notice instead of failing books (A14). A book the server fails on while
/// it still answers others fails like an unusable answer, so it can't hold up the books behind it.
/// </summary>
public sealed class ClassifyStage(
    AiSettings ai, LibraryStore library, IndexQueries queries, ClassifierInputs inputs, ClassificationStore runs, ClassificationResults results,
    TimeProvider? clock = null, ILogger<ClassifyStage>? log = null, AiReadingFeed? feed = null) : IGatedStage
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
        if (ai.Classifier is not { } classifier) return new StageOutcome.Unavailable("AI is switched off.");
        if (ai.Setup.SkippedRoots.Count > 0 && (await library.GetRootIdsAsync(job.DocumentId, ct)).Any(ai.Setup.SkippedRoots.Contains))
            return new StageOutcome.Done(StageStatus.Skipped, [], "Its library folder isn't sent to AI.");
        if (await runs.HasRunAsync(job.ContentHash, classifier.Model, ClassifierPrompt.Version, ct))
            return StageOutcome.Complete();
        if (await queries.GetStageStatusAsync(job.DocumentId, Stage.Ocr, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(OcrWait, "Waiting for its scanned pages to be read.");

        var (input, why) = await inputs.BuildAsync(job.DocumentId, ct);
        if (input is null) return new StageOutcome.Done(StageStatus.Skipped, [], why);

        var started = _clock.GetUtcNow().UtcDateTime;
        // The AI reading page follows the answer as it streams (Built-in AI plan, choice 12).
        var progress = feed?.Start(job.DocumentId, await queries.GetTitleAsync(job.DocumentId, ct) ?? input.FileName, classifier.Model);
        ClassifierResult result;
        try
        {
            result = await classifier.ClassifyAsync(input, progress, ct);
        }
        catch (ModelEndpointException ex)
        {
            feed?.Abandon(job.DocumentId);
            return new StageOutcome.Unavailable(ex.Message);
        }
        catch (ClassifierAnswerException ex)
        {
            feed?.Fail(job.DocumentId, ex.Message);
            await results.RecordFailedAsync(job.DocumentId, job.ContentHash, classifier.Provider, classifier.Model, input, started, ex.Message, CancellationToken.None);
            return new StageOutcome.Failed(ex.Message, Retry: true);
        }
        catch (OperationCanceledException)
        {
            feed?.Abandon(job.DocumentId);
            throw;
        }
        feed?.Finish(job.DocumentId, result);

        // Quotes are book text, so only counts and reasons go to the log.
        _log.LogInformation("Classified document {DocumentId} with {Model} in {Seconds:0.0} s: {Accepted} values kept, {Dropped} dropped ({Reasons})",
            job.DocumentId, classifier.Model, (_clock.GetUtcNow().UtcDateTime - started).TotalSeconds, result.Accepted.Count, result.Dropped.Count,
            string.Join("; ", result.Dropped.GroupBy(d => d.Reason).Select(g => $"{g.Count().ToString(CultureInfo.InvariantCulture)} × {g.Key}")));
        await results.StoreAsync(job.DocumentId, job.ContentHash, input, result, started, CancellationToken.None);
        return StageOutcome.Complete();
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
/// "Reclassify N documents". Nothing here reclassifies by itself (choice 10).
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
        var skipped = await library.GetDocumentIdsInRootsAsync([.. settings.Setup.SkippedRoots], ct);
        if (await queries.FindSampleAsync(skipped, ct: ct) is not { } documentId)
            return new AiTestResult("", TimeSpan.Zero, null, "There's no book with text to try yet. Add a folder, or wait for its books to be read.");
        var title = await queries.GetTitleAsync(documentId, ct) ?? "a book";
        progress?.Report(new AiTestProgress(AiTestStep.Reading, title));
        var (input, why) = await inputs.BuildAsync(documentId, ct);
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
