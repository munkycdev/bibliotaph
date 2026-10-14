using System.Diagnostics;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// One condition on which pages a question to the library reads. Each becomes a <see cref="PageScope"/>, and the
/// question reads the pages every condition allows. Today's conditions are on books; one on chapters (a chapter's
/// setting or creatures) would become the page ranges of the chapters that have it.
/// </summary>
public abstract record AskFilter;

/// <summary>
/// The books the Library shows for its filters and the search box's fields (system:, type:, setting:, theme:,
/// environment:, level: and the rest): every page of each PDF among them, and an adventure's own pages in its file.
/// The search box's plain words don't narrow it: they are what the user searched for, and the question has its own.
/// </summary>
public sealed record BookFilter(LibraryFilter Filter, SearchPlan Plan) : AskFilter;

/// <summary>A page shown under a question to the library: its book, its page as the reader numbers it, a few of its lines, and how it was found.</summary>
public sealed record LibraryAskPassage(long DocumentId, string Book, int PdfPage, string Page, string? Heading, string Text, string? Phrase, bool ByWords,
    bool ByMeaning);

/// <summary>A page an answer from the library is read from: its book, its page as the reader numbers it, its bookmark, and its text cut to its share.</summary>
public sealed record LibraryAskEvidence(long DocumentId, string Book, int PdfPage, string Page, string? Heading, string Text);

/// <summary>
/// What retrieval found across the library: the passages to show, the evidence a model would read, whether both signals
/// were too weak to ask a model at all and the pages that came closest then, how many books and pages were searched
/// and how many of those have vectors, and why meaning couldn't be used this time, if it couldn't.
/// </summary>
public sealed record LibraryAskFindings(string Question, IReadOnlyList<LibraryAskPassage> Passages, IReadOnlyList<LibraryAskEvidence> Evidence, bool Weak,
    IReadOnlyList<string> LookedAt, LibraryCoverage Coverage, AskMeaning Meaning, string? MeaningProblem);

/// <summary>
/// An answer from the library: as the model wrote it, as checked (each citation naming its book and page, and opening
/// that book there), the model that wrote it, the pages it was sent, and the pages it cites in the order it first does.
/// </summary>
public sealed record LibraryAskAnswer(string Question, string Text, CheckedAnswer Checked, string Model, IReadOnlyList<AskPromptPage> Pages,
    IReadOnlyList<LibraryAskEvidence> Cited);

/// <summary>
/// Ask across the whole library: finds the pages of every book in scope that answer a question, with Ask this book's
/// ranking at library scale (words through the search index, meaning through the page vectors made so far, fused by
/// reciprocal rank, lists pushed down, a weak-signal guard weighing each word by how rare it is in the books asked),
/// with at most a few pages of any one book, and has the model answer from them, citing each page by its book. Books
/// without vectors take part by their words. Nothing from a library folder kept from AI is read or sent.
/// </summary>
public sealed class LibraryAskService(
    AiSettings ai, LibraryAskQueries ask, LibraryQueries library, IndexQueries queries, LibraryStore store, ILogger<LibraryAskService>? log = null,
    AskRetrievalOptions? options = null, AiUse? use = null)
{
    /// <summary>
    /// How a library is searched: more pages ranked than in one book, as many pages of evidence as one book's budget
    /// holds at a shorter share each, and no more than three of them from one book.
    /// </summary>
    public static AskRetrievalOptions Defaults { get; } = new() { RankDepth = 100, MaxPages = 10, PageCharacters = 3_000, MaxPagesPerBook = 3 };

    /// <summary>A book's title in a citation is cut to this, so a long one doesn't swamp the sentence it follows.</summary>
    const int CitationTitle = 40;

    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly AskRetrievalOptions _options = options ?? Defaults;

    /// <summary>Whether the library can be asked at all: AI is set up and on.</summary>
    public bool IsOn => ai.Setup.IsReady;

    /// <summary>Whether a question can be answered, as well as its pages found: AI is ready with a chat model.</summary>
    public bool CanAnswer => ai.Current is { Chat: not null, Setup.ModelName: not null };

    /// <summary>The pages a question reads: those every filter allows, without the books in folders kept from AI.</summary>
    public async Task<PageScope> ScopeAsync(IReadOnlyList<AskFilter> filters, CancellationToken ct = default)
    {
        var scope = PageScope.Everything;
        foreach (var filter in filters)
        {
            var narrowed = filter switch
            {
                BookFilter books => PageScope.Of(await library.GetPageSpansAsync(books.Filter, books.Plan with { TextMatch = null, TextExclude = null }, ct)),
                _ => throw new ArgumentException($"{filter.GetType().Name} isn't a filter Ask knows.", nameof(filters)),
            };
            scope = scope.Intersect(narrowed);
        }
        var kept = ai.Setup.SkippedRoots;
        return kept.Count == 0 ? scope : scope.Without(await store.GetDocumentIdsInRootsAsync([.. kept], ct));
    }

    /// <summary>
    /// Which of these books have had a file in a folder kept from AI, as Ask this book decides it, gone files included:
    /// the scope leaves out what is in those folders now, and this catches a book whose file there has since gone.
    /// </summary>
    async Task<IReadOnlyList<long>> KeptFromAiAsync(IReadOnlyList<long> documentIds, CancellationToken ct)
    {
        var kept = ai.Setup.SkippedRoots;
        if (kept.Count == 0) return [];
        var found = new List<long>();
        foreach (var documentId in documentIds)
            if ((await store.GetRootIdsAsync(documentId, ct)).Any(kept.Contains)) found.Add(documentId);
        return found;
    }

    /// <summary>Finds the pages across the library, within <paramref name="filters"/>, that answer <paramref name="question"/>.</summary>
    /// <exception cref="AskUnavailableException">AI is off.</exception>
    public async Task<LibraryAskFindings> FindAsync(string question, IReadOnlyList<AskFilter> filters, CancellationToken ct = default)
    {
        var state = ai.Current;
        if (!state.Setup.IsReady) throw new AskUnavailableException("Asking your library uses AI, which is off. Set it up in Settings > AI.");
        using var busy = use?.Begin();
        var started = Stopwatch.GetTimestamp();
        var embedder = state.Embedder;
        var model = state.Setup.EmbeddingName;
        var scope = await ScopeAsync(filters, ct);

        var words = AskRetrieval.Words(question);
        var wordHits = AskRetrieval.WordsMatch(words) is { } match ? await ask.FindWordsAsync(match, scope, _options.RankDepth, ct) : [];
        var coverage = await ask.GetCoverageAsync(model, EmbeddingText.Version, scope, ct);

        MeaningRanking? meaning = null;
        string? problem = null;
        if (embedder is not null && model is not null && coverage.PagesWithVectors > 0)
        {
            try
            {
                var asked = await embedder.EmbedAsync(model, [EmbeddingText.Query(model, question)], ct);
                meaning = new MeaningRanking(asked[0], _options.RankDepth);
                await foreach (var vector in ask.ReadVectorsAsync(model, EmbeddingText.Version, scope, ct)) meaning.Add(vector.PageId, vector.Vector);
            }
            catch (ModelEndpointException ex)
            {
                // Words still find pages; the panel says why meaning didn't help this time.
                meaning = null;
                problem = ex.Message;
                _log.LogWarning("Asking the library couldn't turn a question into a vector with {Model}: {Reason}", model, ex.Message);
            }
        }
        var byMeaning = meaning?.Best() ?? [];

        // Every page either signal ranked, from books that may be sent: the evidence comes from them, and lists are told apart by their text.
        var candidates = wordHits.Select(h => h.PageId).Concat(byMeaning.Select(m => m.PageId)).ToHashSet();
        var rows = (await ask.GetPagesAsync(candidates, ct)).ToDictionary(r => r.PageId);
        foreach (var kept in await KeptFromAiAsync([.. rows.Values.Select(r => r.DocumentId).Distinct()], ct))
            foreach (var row in rows.Values.Where(r => r.DocumentId == kept).ToList()) rows.Remove(row.PageId);
        wordHits = [.. wordHits.Where(h => rows.ContainsKey(h.PageId))];
        byMeaning = [.. byMeaning.Where(m => rows.ContainsKey(m.PageId))];
        var bookOf = rows.ToDictionary(r => r.Key, r => r.Value.DocumentId);
        var lists = rows.Values.Where(r => AskRetrieval.IsListLike(r.Text)).Select(r => r.PageId).ToHashSet();

        // Each book's bookmarks lift its pages under a heading that matches the question, and name the pages sent.
        var headings = new Dictionary<long, IReadOnlyDictionary<int, HeadingPath>>();
        var boosted = new HashSet<long>();
        foreach (var book in rows.Values.GroupBy(r => r.DocumentId))
        {
            var outline = await queries.GetOutlineAsync(book.Key, ct);
            var paths = AskRetrieval.Headings([.. outline.Select(o => new BookHeading(o.Title, o.PdfPage, o.Depth))], book.Select(r => r.PdfPage).Distinct());
            headings[book.Key] = paths;
            var under = AskRetrieval.UnderMatchingHeadings(words, paths);
            boosted.UnionWith(book.Where(r => under.Contains(r.PdfPage)).Select(r => r.PageId));
        }
        string? HeadingOf(LibraryPageRow row) => headings.TryGetValue(row.DocumentId, out var paths) ? paths.GetValueOrDefault(row.PdfPage)?.Title : null;

        var ranked = AskRetrieval.Fuse([.. wordHits.Select(h => h.PageId)], [.. byMeaning.Select(m => m.PageId)], boosted, _options, lists);
        var capped = AskRetrieval.CapPerBook(ranked, bookOf, _options.MaxPagesPerBook);

        // The weak-signal guard weighs each word by how rare it is in the books asked, and reads the best few pages by
        // words that aren't lists.
        var pagesWith = words.Count == 0 ? [] : await ask.CountPagesAsync([.. words.Select(AskRetrieval.WordMatch)], scope, ct);
        var best = wordHits.Where(h => !lists.Contains(h.PageId)).Take(_options.GuardPages).Select(h => rows.TryGetValue(h.PageId, out var row) ? row.Text : "");
        var wordShare = AskRetrieval.WordShare(words, pagesWith, best);
        var weak = AskRetrieval.IsWeak(wordShare, meaning?.Cosines, _options);

        var titles = await ask.GetTitlesAsync([.. rows.Values.Select(r => r.DocumentId).Distinct()], ct);
        string Title(long documentId) => titles.GetValueOrDefault(documentId) ?? "Untitled";
        var chosen = weak ? [] : AskRetrieval.Choose(capped, rows.ToDictionary(r => r.Key, r => new AskPage(r.Key, r.Value.PdfPage, r.Value.Text)), _options);
        var byRank = ranked.ToDictionary(r => r.PageId);
        var evidence = chosen.Select(p =>
        {
            var row = rows[p.PageId];
            return new LibraryAskEvidence(row.DocumentId, Title(row.DocumentId), row.PdfPage, PageNumbers.Display(row.PdfPage, row.Label), HeadingOf(row), p.Text);
        }).ToList();
        var passages = chosen.Select(p =>
        {
            var row = rows[p.PageId];
            var passage = AskRetrieval.Excerpt(row.Text, words);
            var rank = byRank[p.PageId];
            return new LibraryAskPassage(row.DocumentId, Title(row.DocumentId), row.PdfPage, PageNumbers.Display(row.PdfPage, row.Label), HeadingOf(row),
                passage.Text, passage.Phrase, rank.WordRank is not null, rank.MeaningRank is not null);
        }).ToList();
        var lookedAt = weak
            ? capped.Take(_options.MaxPages).Where(r => rows.ContainsKey(r.PageId)).Select(r => rows[r.PageId])
                .Select(r => $"{Title(r.DocumentId)}, p. {PageNumbers.Display(r.PdfPage, r.Label)}").ToList()
            : [];

        var meaningState = model is null || embedder is null ? AskMeaning.Off
            : coverage.PagesWithVectors > 0 && coverage.PagesWithVectors >= coverage.PagesWithText ? AskMeaning.On
            : AskMeaning.Partial;
        _log.LogInformation(
            "Asked the library: {Books} books in scope, {Words} pages by words, {Meaning} of {Vectors} by meaning, {Lists} read as lists, {Evidence} chosen from {Chosen} books{Weak} in {Ms:0} ms",
            coverage.Books, wordHits.Count, byMeaning.Count, meaning?.Count ?? 0, lists.Count, evidence.Count, evidence.Select(e => e.DocumentId).Distinct().Count(),
            weak ? ", signals too weak to answer" : "", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new LibraryAskFindings(question, passages, evidence, weak, lookedAt, coverage, meaningState, problem);
    }

    /// <summary>
    /// The evidence as it is sent: each page with the book and page it is cited by ("2:112"), and, standing in for its
    /// PDF page while the answer is checked, its place among the pages sent, since two books can share page numbers.
    /// </summary>
    public static IReadOnlyList<AskPromptPage> PromptPages(LibraryAskFindings findings)
    {
        var markers = AskPrompt.LibraryMarkers([.. findings.Evidence.Select(e => (e.DocumentId, e.PdfPage, e.Page))]);
        return [.. findings.Evidence.Select((e, i) => new AskPromptPage(markers[i], i, e.Heading, e.Text, e.Book))];
    }

    /// <summary>
    /// Checks an answer, finished or still being written, against the pages sent (<see cref="AskAnswerCheck"/>), then
    /// turns each citation into its book and page: "[The Sunken Keep, p. 112]", opening that book at that page.
    /// </summary>
    public static CheckedAnswer Check(string answer, IReadOnlyList<AskPromptPage> pages, IReadOnlyList<LibraryAskEvidence> evidence)
    {
        var checkedAnswer = AskAnswerCheck.Check(answer, pages);
        return checkedAnswer with
        {
            Parts = [.. checkedAnswer.Parts.Select(part => part.Kind == AnswerPartKind.Citation && part.PdfPage is { } at && at >= 0 && at < evidence.Count
                ? part with { Text = $"[{Short(evidence[at].Book)}, p. {evidence[at].Page}]", PdfPage = evidence[at].PdfPage, DocumentId = evidence[at].DocumentId }
                : part)],
        };
    }

    static string Short(string title) => title.Length <= CitationTitle ? title : title[..(CitationTitle - 1)].TrimEnd() + "…";

    /// <summary>
    /// Asks the model to answer the question from the pages <see cref="FindAsync"/> chose, streaming its answer to
    /// <paramref name="progress"/> as written (check it with <see cref="Check"/>), and checks its citations and quotes.
    /// </summary>
    /// <exception cref="AskUnavailableException">AI is off.</exception>
    /// <exception cref="ModelEndpointException">The model server couldn't be reached, refused, or failed while answering.</exception>
    public async Task<LibraryAskAnswer> AnswerAsync(LibraryAskFindings findings, IProgress<ModelProgress>? progress = null, CancellationToken ct = default)
    {
        var state = ai.Current;
        var client = state.Chat;
        var model = state.Setup.ModelName;
        if (!state.Setup.IsReady || client is null || model is null)
            throw new AskUnavailableException("Asking your library uses AI, which is off. Set it up in Settings > AI.");
        if (findings.Weak || findings.Evidence.Count == 0) throw new InvalidOperationException("There are no pages to answer from.");
        using var busy = use?.Begin();
        var pages = PromptPages(findings);
        var user = AskPrompt.UserMessage(findings.Question, pages);
        var started = Stopwatch.GetTimestamp();
        var text = await client.CompleteAsync(
            new ModelRequest(model, AskPrompt.LibrarySystemMessage, user, null, AskPrompt.ContextTokens, AskPrompt.AnswerTokens, progress), ct);
        var answer = Check(text, pages, findings.Evidence);
        var cited = answer.CitedPages.Where(i => i >= 0 && i < findings.Evidence.Count).Select(i => findings.Evidence[i]).ToList();
        _log.LogInformation(
            "Answered a question to the library with {Model}: {Pages} pages of {Books} books sent, about {PromptTokens} tokens; about {AnswerTokens} tokens back{NotAnswered}, {Checks}, in {Seconds:0.0} s",
            model, pages.Count, findings.Evidence.Select(e => e.DocumentId).Distinct().Count(), (AskPrompt.LibrarySystemMessage.Length + user.Length) / 4,
            text.Length / 4, answer.NotAnswered ? ", saying the pages don't answer it" : "", AskAnswerCheck.Counts(answer),
            Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new LibraryAskAnswer(findings.Question, text, answer, model, pages, cited);
    }
}
