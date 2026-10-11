using System.Collections.Concurrent;
using System.Diagnostics;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>A book can't be asked: AI is off, or its library folder is kept from AI. The message says which, for the panel.</summary>
public sealed class AskUnavailableException : Exception
{
    public AskUnavailableException(string message) : base(message) { }

    public AskUnavailableException() { }

    public AskUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// A page shown under a question: its PDF page, the page as the reader numbers it, the bookmark it is under, a few lines
/// of it, the word in them that matched (for Find to mark when it opens), and which signals found it.
/// </summary>
public sealed record AskPassage(int PdfPage, string Page, string? Heading, string Text, string? Phrase, bool ByWords, bool ByMeaning);

/// <summary>A page the answer is read from (step 2): numbered as the reader sees it, under its bookmark, cut to its share.</summary>
public sealed record AskEvidence(int PdfPage, string Page, string? Heading, string Text);

/// <summary>How a book's pages were found: by words alone, by words and the vectors made so far, or by words and every page's vector.</summary>
public enum AskMeaning
{
    /// <summary>No embedding model is set up, so pages are found by their words.</summary>
    Off,

    /// <summary>Some pages have vectors; the rest are being made, or couldn't be.</summary>
    Partial,

    /// <summary>Every page with text has a vector.</summary>
    On,
}

/// <summary>
/// What retrieval found for a question (Ask this book, steps 2, 3 and 6): the passages to show, the evidence a model
/// would read, whether both signals were too weak to ask a model at all and the pages looked at then, how much of the
/// book has text and vectors, and why meaning couldn't be used this time, if it couldn't.
/// </summary>
public sealed record AskFindings(string Question, IReadOnlyList<AskPassage> Passages, IReadOnlyList<AskEvidence> Evidence, bool Weak,
    IReadOnlyList<string> LookedAt, PageCoverage Coverage, AskMeaning Meaning, string? MeaningProblem);

/// <summary>
/// An answer to a question (Ask plan, steps 4 and 5): as the model wrote it, as checked against the pages sent, the
/// model that wrote it, and the pages it was sent with the numbers they were cited by.
/// </summary>
public sealed record AskAnswer(string Question, string Text, CheckedAnswer Checked, string Model, IReadOnlyList<AskPromptPage> Pages);

/// <summary>A book's vectors being made: how many of its pages with text have one, whether it is still going, and why it stopped if it failed.</summary>
public sealed record AskFill(int Done, int Total, bool Running, string? Problem);

/// <summary>
/// Ask this book (Ask plan, Q1): finds the pages of one book that answer a question, by its words through the book's
/// search index and by its meaning against each page's vector, fused by reciprocal rank, with no model asked to write
/// anything. The first time a book is asked, its pages' vectors are made in the background by the embedding model in
/// Settings > AI and kept in index.db (page_vec); until they are all there, pages are found by words and the vectors so
/// far. Pages go only to the endpoint in Settings, only while AI is ready (so a remote one needs its tick), and never
/// from a folder kept from AI. The log gets counts, never text.
/// </summary>
public sealed class AskService(
    AiSettings ai, LibraryStore library, AskQueries ask, IndexQueries queries, IndexStore index, ILogger<AskService>? log = null,
    AskRetrievalOptions? options = null) : IDisposable
{
    /// <summary>Pages sent to the embedding model at a time: few enough that a slow server reports progress often.</summary>
    public const int FillBatch = 8;

    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly AskRetrievalOptions _options = options ?? new AskRetrievalOptions();
    readonly ConcurrentDictionary<long, AskFill> _fills = new();
    readonly SemaphoreSlim _fillGate = new(1, 1);
    readonly CancellationTokenSource _stopping = new();

    /// <summary>Whether a book can be asked at all: AI is set up and on. With AI off, the panel is hidden (choice 12).</summary>
    public bool IsOn => ai.Setup.IsReady;

    /// <summary>Whether pages can be found by meaning: an embedding model is set up for the current AI.</summary>
    public bool HasEmbeddingModel => ai.Embedder is not null && ai.Setup.EmbeddingName is not null;

    /// <summary>Whether a question can be answered, as well as its pages found: AI is ready with a chat model.</summary>
    public bool CanAnswer => ai.Chat is not null && ai.Setup.ModelName is not null;

    /// <summary>Raised, on any thread, when AI's setup changes, which can show or hide Ask.</summary>
    public event EventHandler? Changed
    {
        add => ai.Changed += value;
        remove => ai.Changed -= value;
    }

    /// <summary>Raised, on any thread, with the document whose vectors moved on, finished or stopped.</summary>
    public event EventHandler<long>? FillChanged;

    /// <summary>How a book's vectors are coming along, or null when none have been made this sitting.</summary>
    public AskFill? FillOf(long documentId) => _fills.GetValueOrDefault(documentId);

    /// <summary>Why this book can't be asked, or null when it can.</summary>
    public async Task<string?> WhyNotAsync(long documentId, CancellationToken ct = default)
    {
        if (!ai.Setup.IsReady) return "Ask this book uses AI, which is off. Set it up in Settings > AI.";
        if (ai.Setup.SkippedRoots.Count > 0 && (await library.GetRootIdsAsync(documentId, ct)).Any(ai.Setup.SkippedRoots.Contains))
            return "This book's library folder is kept from AI, so it can't be asked. Settings > AI says which folders are sent.";
        return null;
    }

    /// <summary>
    /// Finds the pages that answer <paramref name="question"/> in the document's pages, and starts making the book's
    /// vectors if they aren't all made yet.
    /// </summary>
    /// <exception cref="AskUnavailableException">AI is off, or the book's folder is kept from AI.</exception>
    public async Task<AskFindings> FindAsync(long documentId, string question, CancellationToken ct = default)
    {
        if (await WhyNotAsync(documentId, ct) is { } why) throw new AskUnavailableException(why);
        var started = Stopwatch.GetTimestamp();
        var setup = ai.Setup;
        var embedder = ai.Embedder;
        var model = setup.EmbeddingName;

        var words = AskRetrieval.Words(question);
        var wordHits = AskRetrieval.WordsMatch(words) is { } match ? await ask.FindWordsAsync(documentId, match, _options.RankDepth, ct) : [];
        var pdfPageOf = wordHits.ToDictionary(h => h.PageId, h => h.PdfPage);

        IReadOnlyList<(long PageId, double Cosine)>? meaning = null;
        string? problem = null;
        if (embedder is not null && model is not null)
        {
            StartFill(documentId);
            var vectors = await ask.GetVectorsAsync(documentId, model, EmbeddingText.Version, ct);
            if (vectors.Count > 0)
            {
                try
                {
                    var asked = await embedder.EmbedAsync(model, [EmbeddingText.Query(model, question)], ct);
                    meaning = AskRetrieval.RankByMeaning(asked[0], vectors.Select(v => (v.PageId, v.Vector)));
                    foreach (var vector in vectors) pdfPageOf[vector.PageId] = vector.PdfPage;
                }
                catch (ModelEndpointException ex)
                {
                    // Words still find pages; the panel says why meaning didn't help this time.
                    problem = ex.Message;
                    _log.LogWarning("Ask couldn't turn a question into a vector with {Model}: {Reason}", model, ex.Message);
                }
            }
        }

        var outline = await queries.GetOutlineAsync(documentId, ct);
        var headings = AskRetrieval.Headings([.. outline.Select(o => new BookHeading(o.Title, o.PdfPage, o.Depth))], pdfPageOf.Values.Distinct());
        var underMatching = AskRetrieval.UnderMatchingHeadings(words, headings);
        var boosted = pdfPageOf.Where(p => underMatching.Contains(p.Value)).Select(p => p.Key).ToHashSet();
        var ranked = AskRetrieval.Fuse([.. wordHits.Select(h => h.PageId)], meaning is null ? [] : [.. meaning.Select(m => m.PageId)], boosted, _options);

        // The texts of the pages that could be evidence, and of the best few by words for the weak-signal guard.
        var wanted = ranked.Take(_options.RankDepth).Select(r => r.PageId).Concat(wordHits.Take(5).Select(h => h.PageId)).ToHashSet();
        var rows = (await ask.GetPagesAsync(documentId, wanted, ct)).ToDictionary(r => r.PageId);
        var bestCoverage = wordHits.Take(5).Select(h => rows.TryGetValue(h.PageId, out var row) ? AskRetrieval.Coverage(words, row.Text) : 0).DefaultIfEmpty(0).Max();
        var weak = AskRetrieval.IsWeak(words.Count, bestCoverage, meaning?.Select(m => m.Cosine).ToList(), _options);

        var labels = await ask.GetLabelsAsync(documentId, ct);
        var chosen = weak ? [] : AskRetrieval.Choose(ranked, rows.ToDictionary(r => r.Key, r => new AskPage(r.Key, r.Value.PdfPage, r.Value.Text)), _options);
        var byRank = ranked.ToDictionary(r => r.PageId);
        var evidence = chosen.Select(p => new AskEvidence(p.PdfPage, PageNumbers.Display(p.PdfPage, labels), headings.GetValueOrDefault(p.PdfPage)?.Title, p.Text)).ToList();
        var passages = chosen.Select(p =>
        {
            var passage = AskRetrieval.Excerpt(rows[p.PageId].Text, words);
            var rank = byRank[p.PageId];
            return new AskPassage(p.PdfPage, PageNumbers.Display(p.PdfPage, labels), headings.GetValueOrDefault(p.PdfPage)?.Title, passage.Text, passage.Phrase,
                rank.WordRank is not null, rank.MeaningRank is not null);
        }).ToList();
        var lookedAt = weak ? ranked.Take(_options.MaxPages).Select(r => PageNumbers.Display(pdfPageOf[r.PageId], labels)).ToList() : [];

        var coverage = await ask.GetCoverageAsync(documentId, model, EmbeddingText.Version, ct);
        var meaningState = model is null || embedder is null ? AskMeaning.Off
            : coverage.WithVectors > 0 && coverage.WithVectors >= coverage.WithText ? AskMeaning.On
            : AskMeaning.Partial;
        _log.LogInformation("Asked document {DocumentId}: {Words} pages by words, {Meaning} by meaning, {Evidence} chosen{Weak} in {Ms:0} ms",
            documentId, wordHits.Count, meaning?.Count ?? 0, evidence.Count, weak ? ", signals too weak to answer" : "",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new AskFindings(question, passages, evidence, weak, lookedAt, coverage, meaningState, problem);
    }

    /// <summary>The evidence as it is sent: each page with the number it is cited by.</summary>
    public static IReadOnlyList<AskPromptPage> PromptPages(AskFindings findings)
    {
        var markers = AskPrompt.Markers([.. findings.Evidence.Select(e => (e.PdfPage, e.Page))]);
        return [.. findings.Evidence.Select((e, i) => new AskPromptPage(markers[i], e.PdfPage, e.Heading, e.Text))];
    }

    /// <summary>
    /// Asks the model to answer the question from the pages <see cref="FindAsync"/> chose (Ask plan, step 4), streaming
    /// its answer to <paramref name="progress"/>, and checks its citations and quotes against those pages (step 5).
    /// </summary>
    /// <exception cref="AskUnavailableException">AI is off, or the book's folder is kept from AI.</exception>
    /// <exception cref="ModelEndpointException">The model server couldn't be reached, refused, or failed while answering.</exception>
    public async Task<AskAnswer> AnswerAsync(long documentId, AskFindings findings, IProgress<ModelProgress>? progress = null, CancellationToken ct = default)
    {
        if (await WhyNotAsync(documentId, ct) is { } why) throw new AskUnavailableException(why);
        var client = ai.Chat;
        var model = ai.Setup.ModelName;
        if (client is null || model is null) throw new AskUnavailableException("Ask this book uses AI, which is off. Set it up in Settings > AI.");
        if (findings.Weak || findings.Evidence.Count == 0) throw new InvalidOperationException("There are no pages to answer from.");
        var pages = PromptPages(findings);
        var user = AskPrompt.UserMessage(findings.Question, pages);
        var started = Stopwatch.GetTimestamp();
        var text = await client.CompleteAsync(
            new ModelRequest(model, AskPrompt.SystemMessage, user, null, AskPrompt.ContextTokens, AskPrompt.AnswerTokens, progress), ct);
        var answer = AskAnswerCheck.Check(text, pages);
        _log.LogInformation(
            "Answered a question on document {DocumentId} with {Model}: {Pages} pages sent, about {PromptTokens} tokens; about {AnswerTokens} tokens back{NotAnswered}, {Checks}, in {Seconds:0.0} s",
            documentId, model, pages.Count, (AskPrompt.SystemMessage.Length + user.Length) / 4, text.Length / 4,
            answer.NotAnswered ? ", saying the pages don't answer it" : "", AskAnswerCheck.Counts(answer), Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new AskAnswer(findings.Question, text, answer, model, pages);
    }

    /// <summary>
    /// Starts making the book's page vectors in the background, unless they are being made already or there is no
    /// embedding model. One book at a time; another waits its turn.
    /// </summary>
    public void StartFill(long documentId)
    {
        if (!HasEmbeddingModel || _stopping.IsCancellationRequested) return;
        if (_fills.TryGetValue(documentId, out var fill) && fill.Running) return;
        _fills[documentId] = new AskFill(fill?.Done ?? 0, fill?.Total ?? 0, true, null);
        FillChanged?.Invoke(this, documentId);
        _ = Task.Run(() => FillAsync(documentId, _stopping.Token));
    }

    /// <summary>
    /// Makes vectors for the book's pages that have text and none from the current model, a few pages at a time, and
    /// first drops those another model made. Stops at the first failure, saying why; asking again carries on.
    /// </summary>
    async Task FillAsync(long documentId, CancellationToken ct)
    {
        var made = 0;
        var started = Stopwatch.GetTimestamp();
        string? problem = null;
        string? model = null;
        try
        {
            await _fillGate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            IReadOnlyDictionary<int, HeadingPath>? headings = null;
            while (true)
            {
                var embedder = ai.Embedder;
                var name = ai.Setup.EmbeddingName;
                if (embedder is null || name is null || await WhyNotAsync(documentId, ct) is not null) break;
                if (name != model)
                {
                    model = name;
                    await index.DeleteStaleVectorsAsync(documentId, model, EmbeddingText.Version, ct);
                }
                var coverage = await ask.GetCoverageAsync(documentId, model, EmbeddingText.Version, ct);
                Report(documentId, new AskFill(coverage.WithVectors, coverage.WithText, true, null));
                var batch = await ask.GetPagesWithoutVectorsAsync(documentId, model, EmbeddingText.Version, FillBatch, ct);
                if (batch.Count == 0) break;
                if (headings is null)
                {
                    var outline = await queries.GetOutlineAsync(documentId, ct);
                    headings = AskRetrieval.Headings([.. outline.Select(o => new BookHeading(o.Title, o.PdfPage, o.Depth))],
                        Enumerable.Range(0, (await ask.GetLabelsAsync(documentId, ct)).Count));
                }
                var texts = batch.Select(p => EmbeddingText.Document(model, headings.GetValueOrDefault(p.PdfPage)?.Title, p.Text)).ToList();
                var vectors = await embedder.EmbedAsync(model, texts, ct);
                await index.SaveVectorsAsync(model, EmbeddingText.Version, [.. batch.Zip(vectors, (p, v) => (p.PageId, v))], ct);
                made += batch.Count;
            }
        }
        catch (ModelEndpointException ex)
        {
            problem = ex.Message;
            _log.LogWarning("Making page vectors for document {DocumentId} with {Model} stopped after {Pages} pages: {Reason}", documentId, model, made, ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problem = "Finding pages by meaning stopped. The log has the details.";
            _log.LogError(ex, "Making page vectors for document {DocumentId} failed", documentId);
        }
        finally
        {
            _fillGate.Release();
        }
        if (made > 0)
            _log.LogInformation("Made {Pages} page vectors for document {DocumentId} with {Model} in {Seconds:0.0} s",
                made, documentId, model, Stopwatch.GetElapsedTime(started).TotalSeconds);
        var final = model is null ? null : await ask.GetCoverageAsync(documentId, model, EmbeddingText.Version, CancellationToken.None);
        Report(documentId, new AskFill(final?.WithVectors ?? 0, final?.WithText ?? 0, false, problem));
    }

    void Report(long documentId, AskFill fill)
    {
        _fills[documentId] = fill;
        FillChanged?.Invoke(this, documentId);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
