using System.Diagnostics;
using System.Globalization;
using System.Text;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// What a search with <c>about:</c> found: the Inside documents tab's pages and the Documents tab's books, best first,
/// how much of what it searched has text and vectors, whether meaning took part, and why not this time, if it couldn't.
/// </summary>
public sealed record MeaningResults(PageResults Pages, IReadOnlyList<LibraryEntry> Documents, LibraryCoverage Coverage, AskMeaning Meaning, string? Problem,
    bool ReadingLibrary)
{
    /// <summary>The line the results show about how they were found, or null when meaning covered everything.</summary>
    public string? Note => MeaningSearchText.Note(this);
}

/// <summary>
/// The search box's <c>about:</c>: Answer from my books' ranking without the answer. The other fields and the Library's
/// filters choose the pages; within them, pages are ranked by the about words through the search index and by the
/// closeness of their vectors to the about text, fused by reciprocal rank with lists pushed down. Books are ranked by
/// their best pages and their own vectors (<see cref="BookVectors"/>) together. Plain words still have to be on a page; about: then orders those pages by meaning. Books
/// without vectors yet take part by their words, and with no embedding model it is a search for any of its words.
/// Nothing of a book is sent anywhere: only the about text goes to the embedding model.
/// </summary>
public sealed class MeaningSearchService(AiSettings ai, LibraryAskQueries ask, LibraryQueries library, BookVectorQueries books, AiUse use,
    ILogger<MeaningSearchService>? log = null)
{
    /// <summary>How deep each signal ranks: deeper than a question's, since a search shows up to 100 books.</summary>
    public static AskRetrievalOptions Options { get; } = new() { RankDepth = 300 };

    /// <summary>With plain words to find, this many of their pages are ranked by meaning, best by words first.</summary>
    const int WordPool = 2_000;

    /// <summary>About as long as a snippet the search index cuts.</summary>
    const int SnippetLength = 200;

    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly Lock _lock = new();

    /// <summary>The last about text's vector, so changing tabs, order or filters doesn't ask the model again.</summary>
    (string Model, string Text, float[] Vector)? _last;

    /// <summary>Runs <paramref name="plan"/>, which has <see cref="SearchPlan.About"/>, within <paramref name="filter"/>.</summary>
    public async Task<MeaningResults> SearchAsync(SearchQuery query, SearchPlan plan, LibraryFilter filter, CancellationToken ct = default)
    {
        var about = plan.About ?? throw new ArgumentException("The search has no about: to rank by.", nameof(plan));
        var started = Stopwatch.GetTimestamp();
        var state = ai.Current;
        var embedder = state.Embedder;
        var model = state.Setup.EmbeddingName;
        var scope = PageScope.Of(await library.GetPageSpansAsync(filter, plan with { TextMatch = null, TextExclude = null }, ct));
        var aboutWords = AskRetrieval.Words(about);
        var coverage = await ask.GetCoverageAsync(model, EmbeddingText.Version, scope, ct);

        // Plain words narrow the pages as they always do, and about: orders them; alone, about's own words find pages too.
        var required = plan.TextMatch;
        var wordHits = required is not null ? await ask.FindWordsAsync(required, scope, WordPool, ct)
            : AskRetrieval.WordsMatch(aboutWords) is { } match ? await ask.FindWordsAsync(match, scope, Options.RankDepth, ct)
            : [];

        IReadOnlyList<(long PageId, double Cosine)> byMeaning = [];
        string? problem = null;
        float[]? vector = null;
        var bookVectors = model is null ? 0 : await books.CountAsync(model, EmbeddingText.Version, ct);
        if (embedder is not null && model is not null && (coverage.PagesWithVectors > 0 || bookVectors > 0))
        {
            try
            {
                vector = await VectorAsync(embedder, model, about, ct);
            }
            catch (ModelEndpointException ex)
            {
                // Words still find pages; the results say why meaning didn't help this time.
                problem = ex.Message;
                _log.LogWarning("Searching by meaning couldn't turn the about text into a vector with {Model}: {Reason}", model, ex.Message);
            }
        }
        if (vector is not null && model is not null && coverage.PagesWithVectors > 0 && (required is null || wordHits.Count > 0))
        {
            var ranking = new MeaningRanking(vector, Options.RankDepth);
            if (required is null)
                await foreach (var page in ask.ReadVectorsAsync(model, EmbeddingText.Version, scope, ct)) ranking.Add(page.PageId, page.Vector);
            else
                foreach (var page in await ask.GetVectorsAsync(model, EmbeddingText.Version, [.. wordHits.Select(h => h.PageId)], ct)) ranking.Add(page.PageId, page.Vector);
            byMeaning = ranking.Best();
        }

        var candidates = wordHits.Take(Options.RankDepth).Select(h => h.PageId).Concat(byMeaning.Select(m => m.PageId)).ToHashSet();
        var rows = (await ask.GetPagesAsync(candidates, ct)).ToDictionary(r => r.PageId);
        var lists = rows.Values.Where(r => AskRetrieval.IsListLike(r.Text)).Select(r => r.PageId).ToHashSet();
        var ranked = AskRetrieval.Fuse([.. wordHits.Select(h => h.PageId)], [.. byMeaning.Select(m => m.PageId)], new HashSet<long>(), Options, lists);

        var marks = AskRetrieval.Words(string.Join(' ', [.. query.HighlightTerms(), about]));
        var hits = ranked.Where(r => rows.ContainsKey(r.PageId)).Select(r => new RankedPageHit(r.PageId, Snippet(rows[r.PageId].Text, marks))).ToList();
        var pages = await library.RankedPagesAsync(hits, plan, filter, ct: ct);
        var documents = await DocumentsAsync(pages, plan, filter, model, bookVectors > 0 ? vector : null, ct);

        var meaning = model is null || embedder is null ? AskMeaning.Off
            : coverage.PagesWithVectors > 0 && coverage.PagesWithVectors >= coverage.PagesWithText ? AskMeaning.On
            : AskMeaning.Partial;
        // Counts only: what someone searches for stays off the log, as book text does.
        _log.LogInformation(
            "Searched by meaning: {Pages} pages in scope, {Words} by words, {Meaning} by meaning of {Vectors} with vectors, {Books} books shown in {Ms:0} ms",
            coverage.PagesWithText, wordHits.Count, byMeaning.Count, coverage.PagesWithVectors, pages.Entries.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new MeaningResults(pages, documents, coverage, meaning, problem, state.Setup.LibraryVectors);
    }

    async Task<float[]> VectorAsync(IEmbeddingClient embedder, string model, string about, CancellationToken ct)
    {
        lock (_lock)
            if (_last is { } last && last.Model == model && last.Text == about) return last.Vector;
        // The background reader gives way while this is asked, as it does for a question.
        using var busy = use.Begin();
        var vector = (await embedder.EmbedAsync(model, [EmbeddingText.Query(model, about)], ct))[0];
        lock (_lock) _last = (model, about, vector);
        return vector;
    }

    /// <summary>
    /// The Documents tab: the books the fields and filters leave (and the words, when there are some), ranked by fusing
    /// three orders by reciprocal rank: their best pages', how close each card's own vector is to the about text, and
    /// their titles' and fields' words. A book only one of them found takes part too. In the Library's order when it
    /// isn't Best match.
    /// </summary>
    async Task<IReadOnlyList<LibraryEntry>> DocumentsAsync(PageResults pages, SearchPlan plan, LibraryFilter filter, string? model, float[]? vector,
        CancellationToken ct)
    {
        var byPages = pages.Entries.Select(e => e.Entry).ToList();
        IReadOnlyList<LibraryEntry> byWords = plan.DocumentMatch is null ? [] : await library.SearchDocumentsAsync(plan, filter, ct: ct);
        List<LibraryEntry> byCard = [];
        if (vector is not null && model is not null)
        {
            // The cards a card's vector may bring in: those the words found, or the whole library the fields and filters leave.
            var allowed = plan.DocumentMatch is null ? await library.ListAsync(filter, plan, ct: ct) : byWords;
            var vectors = (await books.GetVectorsAsync(model, EmbeddingText.Version, [.. allowed.Select(e => e.EntryId.Value).Distinct()], ct))
                .ToDictionary(v => v.EntryId, v => v.Vector);
            var close = allowed.Where(e => vectors.ContainsKey(e.EntryId.Value))
                .Select(e => (Entry: e, Cosine: AskRetrieval.Cosine(vector, vectors[e.EntryId.Value])))
                .OrderByDescending(e => e.Cosine).ThenBy(e => e.Entry.CardId.Value).ToList();
            // Every card has some closeness to anything, so only those standing out from the rest count: closer than
            // halfway from the middle card to the best.
            if (close.Count > 0)
            {
                var (best, middle) = (close[0].Cosine, close[close.Count / 2].Cosine);
                var cut = middle + (best - middle) / 2;
                byCard = [.. close.TakeWhile(e => e.Cosine > 0 && e.Cosine >= cut).Take(Options.RankDepth).Select(e => e.Entry)];
            }
        }

        var cards = new Dictionary<EntryId, LibraryEntry>();
        var scores = new Dictionary<EntryId, double>();
        foreach (var ranking in new[] { byPages, byCard, byWords.Take(Options.RankDepth).ToList() })
            for (var i = 0; i < ranking.Count; i++)
            {
                var card = ranking[i].CardId;
                // The page tab's card carries how its pages were found; the others are the same card.
                cards.TryAdd(card, ranking[i]);
                scores[card] = scores.GetValueOrDefault(card) + 1.0 / (Options.K + i + 1);
            }
        var found = cards.Values.OrderByDescending(e => scores[e.CardId]).ThenBy(e => e.CardId.Value).ToList();
        return filter.Sort switch
        {
            LibrarySort.Title => [.. found.OrderBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)],
            LibrarySort.Publisher => [.. found.OrderBy(e => e.Publisher is null).ThenBy(e => e.Publisher, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)],
            LibrarySort.RecentlyOpened => [.. found.OrderBy(e => e.OpenedUtc is null).ThenByDescending(e => e.OpenedUtc).ThenByDescending(e => e.AddedUtc)],
            LibrarySort.RecentlyAdded => [.. found.OrderByDescending(e => e.AddedUtc)],
            _ => found,
        };
    }

    /// <summary>A few lines of the page around the first word searched for, each such word marked as the search index marks its hits.</summary>
    internal static string Snippet(string text, IReadOnlyList<string> words)
    {
        var excerpt = AskRetrieval.Excerpt(text, words, SnippetLength).Text;
        if (words.Count == 0) return excerpt;
        var marked = new StringBuilder(excerpt.Length + 16);
        var i = 0;
        while (i < excerpt.Length)
        {
            if (!char.IsLetterOrDigit(excerpt[i]))
            {
                marked.Append(excerpt[i++]);
                continue;
            }
            var end = i;
            while (end < excerpt.Length && char.IsLetterOrDigit(excerpt[end])) end++;
            var token = excerpt[i..end];
            var lower = token.ToLower(CultureInfo.InvariantCulture);
            if (words.Any(w => w.Length >= 3 ? lower.StartsWith(w, StringComparison.Ordinal) : lower == w))
                marked.Append(LibraryQueries.HitStart).Append(token).Append(LibraryQueries.HitEnd);
            else marked.Append(token);
            i = end;
        }
        return marked.ToString();
    }
}

/// <summary>What a search with <c>about:</c> says about how it found its results, in the same words on Windows and the web.</summary>
public static class MeaningSearchText
{
    /// <summary>The line under the results, or null when every page searched was found by meaning as well as words.</summary>
    public static string? Note(MeaningResults results) => results switch
    {
        { Coverage.PagesWithText: 0 } => null,
        { Meaning: AskMeaning.Off } => "about: found pages by its words. Choose an embedding model in Settings > AI to find them by meaning too.",
        { Problem: { } problem } => $"about: found pages by its words alone this time: {problem}",
        { Meaning: AskMeaning.Partial, Coverage: { PagesWithVectors: 0 } } => "about: found pages by its words, since none has been read for meaning yet. " + Rest(results, "They are", "them"),
        { Meaning: AskMeaning.Partial, Coverage: var coverage } =>
            $"about: found pages by meaning in {Count(coverage.PagesWithVectors)} of {Count(coverage.PagesWithText)} pages, and the rest by its words. " + Rest(results, "The others are", "the others"),
        _ => null,
    };

    /// <summary>What happens to the pages not yet read for meaning.</summary>
    static string Rest(MeaningResults results, string subject, string pages) => results.ReadingLibrary
        ? $"{subject} read for meaning in the background."
        : $"Tick Read the library for meaning in the background in Settings > AI to find {pages} by meaning.";

    static string Count(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}
