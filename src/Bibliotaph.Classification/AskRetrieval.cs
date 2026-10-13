using System.Globalization;
using System.Text;

namespace Bibliotaph.Classification;

/// <summary>A page of the book being asked, as retrieval reads it: its row in index.db, its PDF page and its indexed text.</summary>
public sealed record AskPage(long PageId, int PdfPage, string Text);

/// <summary>
/// A page as the two signals ranked it (1 is first; null when that signal didn't rank it), whether a heading above it
/// matched the question, and its score after fusing them.
/// </summary>
public sealed record RankedPage(long PageId, int? WordRank, int? MeaningRank, bool Boosted, double Score);

/// <summary>A bookmark with the bookmarks above it, innermost last: "Part Two", "The Mill".</summary>
public sealed record HeadingPath(IReadOnlyList<string> Titles)
{
    /// <summary>The innermost bookmark, which is what a page is shown under.</summary>
    public string Title => Titles[^1];
}

/// <summary>The few lines of a page shown under a question, and the word in them that matched it, for Find to mark.</summary>
public sealed record Passage(string Text, string? Phrase);

/// <summary>How retrieval weighs and cuts.</summary>
public sealed record AskRetrievalOptions
{
    /// <summary>How many pages each signal ranks before they are fused.</summary>
    public int RankDepth { get; init; } = 30;

    /// <summary>Reciprocal rank fusion's constant: a page's share from a signal is 1 / (K + rank).</summary>
    public int K { get; init; } = 60;

    /// <summary>A page under a bookmark that matches the question gains this share of a first place.</summary>
    public double HeadingBoost { get; init; } = 0.5;

    /// <summary>The meaning signal counts as strong only when the best page is at least this close to the question...</summary>
    public double MinCosine { get; init; } = 0.45;

    /// <summary>...and stands this far above the book's typical page, so a book where every page is alike doesn't count.</summary>
    public double MinCosineLead { get; init; } = 0.05;

    /// <summary>At most this many pages are the evidence.</summary>
    public int MaxPages { get; init; } = 8;

    /// <summary>About 6,000 tokens of pages, at roughly four characters a token.</summary>
    public int Budget { get; init; } = 24_000;

    /// <summary>A page gives at most this much, so one long page can't take the whole budget.</summary>
    public int PageCharacters { get; init; } = 4_000;
}

/// <summary>
/// Finding the pages of one book that answer a question, without a model: the question's words through the book's
/// search index, its meaning against each page's vector, the two fused by reciprocal rank, and pages under a bookmark
/// that matches the question lifted a little. Pure, so it is tested without an index or a server.
/// </summary>
public static class AskRetrieval
{
    /// <summary>A word this long or longer also finds words that start with it, as search does (SearchPlan.MinWordStart).</summary>
    const int MinWordStart = 3;

    /// <summary>Words that say how a question is asked rather than what about, and are on every page anyway.</summary>
    static readonly HashSet<string> StopWords =
    [
        with(StringComparer.Ordinal),
        "a", "about", "after", "all", "also", "an", "and", "any", "are", "as", "at", "be", "been", "before", "being", "between", "both",
        "but", "by", "can", "could", "did", "do", "does", "doing", "done", "during", "each", "for", "from", "get", "gets", "give", "go",
        "had", "has", "have", "having", "he", "her", "here", "him", "his", "how", "i", "if", "in", "into", "is", "it", "its", "just",
        "know", "let", "like", "many", "may", "me", "might", "more", "most", "much", "must", "my", "no", "not", "of", "on", "one", "or",
        "other", "our", "out", "over", "say", "says", "she", "should", "so", "some", "such", "tell", "than", "that", "the", "their",
        "them", "then", "there", "these", "they", "this", "those", "through", "to", "too", "under", "up", "us", "use", "used", "very",
        "was", "way", "we", "were", "what", "when", "where", "which", "while", "who", "whom", "whose", "why", "will", "with", "would",
        "you", "your", "book", "page", "pages",
    ];

    /// <summary>
    /// The words of a question worth looking for, in order, each once: lower case, letters and digits only, without the
    /// words every question and every page has.
    /// </summary>
    public static IReadOnlyList<string> Words(string question)
    {
        var words = new List<string>();
        foreach (var token in Tokens(question))
            if (token.Length >= 2 && !StopWords.Contains(token) && !words.Contains(token)) words.Add(token);
        return words;
    }

    /// <summary>
    /// The question's words as an FTS5 expression that finds pages with any of them, so bm25 ranks those with more and
    /// rarer ones first. Each word is quoted, and is letters and digits only, so nothing typed can become FTS5 syntax.
    /// Null when the question has no words worth looking for.
    /// </summary>
    public static string? WordsMatch(IReadOnlyList<string> words) =>
        words.Count == 0 ? null : string.Join(" OR ", words.Select(w => w.Length >= MinWordStart ? $"\"{w}\"*" : $"\"{w}\""));

    /// <summary>How many of the question's words a text has, as search would find them (a longer word by its start).</summary>
    public static int Coverage(IReadOnlyList<string> words, string text)
    {
        if (words.Count == 0) return 0;
        var tokens = Tokens(text).ToHashSet(StringComparer.Ordinal);
        return words.Count(w => Matches(w, tokens));
    }

    static bool Matches(string word, HashSet<string> tokens) =>
        word.Length >= MinWordStart ? tokens.Any(t => t.StartsWith(word, StringComparison.Ordinal)) : tokens.Contains(word);

    /// <summary>The cosine of the angle between two vectors: 1 for the same direction, 0 for unrelated. 0 when either is empty or zero.</summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            aa += a[i] * (double)a[i];
            bb += b[i] * (double)b[i];
        }
        return aa == 0 || bb == 0 ? 0 : dot / (Math.Sqrt(aa) * Math.Sqrt(bb));
    }

    /// <summary>
    /// Every page's closeness to the question, closest first (in process, over the one book's vectors).
    /// </summary>
    public static IReadOnlyList<(long PageId, double Cosine)> RankByMeaning(float[] question, IEnumerable<(long PageId, float[] Vector)> pages) =>
        [.. pages.Select(p => (p.PageId, Cosine: Cosine(question, p.Vector))).OrderByDescending(p => p.Cosine).ThenBy(p => p.PageId)];

    /// <summary>
    /// Reciprocal rank fusion: each signal gives a page 1 / (K + its rank), and a page under a matching bookmark gains
    /// part of a first place. Only pages a signal ranked take part, so a matching chapter can lift its pages but not
    /// bring in pages that say nothing of the question. Best first.
    /// </summary>
    public static IReadOnlyList<RankedPage> Fuse(IReadOnlyList<long> byWords, IReadOnlyList<long> byMeaning, IReadOnlySet<long> boosted,
        AskRetrievalOptions? options = null)
    {
        options ??= new AskRetrievalOptions();
        var words = byWords.Take(options.RankDepth).Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i + 1);
        var meaning = byMeaning.Take(options.RankDepth).Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i + 1);
        var first = 1.0 / (options.K + 1);
        return [.. words.Keys.Union(meaning.Keys)
            .Select(id =>
            {
                int? wordRank = words.TryGetValue(id, out var w) ? w : null;
                int? meaningRank = meaning.TryGetValue(id, out var m) ? m : null;
                var boost = boosted.Contains(id);
                var score = (wordRank is { } wr ? 1.0 / (options.K + wr) : 0) + (meaningRank is { } mr ? 1.0 / (options.K + mr) : 0)
                    + (boost ? options.HeadingBoost * first : 0);
                return new RankedPage(id, wordRank, meaningRank, boost, score);
            })
            .OrderByDescending(p => p.Score).ThenBy(p => p.PageId)];
    }

    /// <summary>
    /// The weak-signal guard (before any model is asked): the best page by words has fewer than half the question's
    /// words (at least one), and the best page by meaning is neither close to the question nor stands out from the
    /// book's typical page. Without vectors, words alone decide.
    /// </summary>
    /// <param name="wordCount">How many words the question has worth looking for.</param>
    /// <param name="bestCoverage">How many of them the best page by words has.</param>
    /// <param name="cosines">Every page's closeness to the question, or null when the book has no vectors.</param>
    /// <param name="options">The thresholds; the defaults when null.</param>
    public static bool IsWeak(int wordCount, int bestCoverage, IReadOnlyList<double>? cosines, AskRetrievalOptions? options = null)
    {
        options ??= new AskRetrievalOptions();
        var wordsStrong = wordCount > 0 && bestCoverage >= Math.Max(1, (wordCount + 1) / 2);
        if (wordsStrong) return false;
        if (cosines is not { Count: > 0 }) return true;
        var best = cosines.Max();
        var typical = Median(cosines);
        return !(best >= options.MinCosine && best - typical >= options.MinCosineLead);
    }

    static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>
    /// Each PDF page's bookmark: the last one before or on it in reading order, with the bookmarks above that one.
    /// Bookmarks that point outside the file are left out.
    /// </summary>
    public static IReadOnlyDictionary<int, HeadingPath> Headings(IReadOnlyList<BookHeading> outline, IEnumerable<int> pdfPages)
    {
        var paths = new List<(int PdfPage, HeadingPath Path)>();
        var stack = new List<(int Depth, string Title)>();
        foreach (var heading in outline)
        {
            var title = ExcerptBuilder.Tidy(heading.Title);
            if (heading.PdfPage < 0 || title.Length == 0) continue;
            while (stack.Count > 0 && stack[^1].Depth >= heading.Depth) stack.RemoveAt(stack.Count - 1);
            stack.Add((heading.Depth, title));
            paths.Add((heading.PdfPage, new HeadingPath([.. stack.Select(s => s.Title)])));
        }
        var result = new Dictionary<int, HeadingPath>();
        if (paths.Count == 0) return result;
        foreach (var page in pdfPages)
        {
            // The last in reading order that starts on or before the page: a sub-heading on the same page as its chapter wins.
            HeadingPath? found = null;
            foreach (var (start, path) in paths)
                if (start <= page) found = path;
            if (found is not null) result[page] = found;
        }
        return result;
    }

    /// <summary>Pages under a bookmark, at any level, that has one of the question's words.</summary>
    public static IReadOnlySet<int> UnderMatchingHeadings(IReadOnlyList<string> words, IReadOnlyDictionary<int, HeadingPath> headings)
    {
        var pages = new HashSet<int>();
        if (words.Count == 0) return pages;
        foreach (var (page, path) in headings)
            if (path.Titles.Any(t => Coverage(words, t) > 0)) pages.Add(page);
        return pages;
    }

    /// <summary>
    /// The evidence: the best pages in order until <see cref="AskRetrievalOptions.MaxPages"/> or the budget runs out,
    /// each tidied and cut to its share. Pages with no text are passed over.
    /// </summary>
    public static IReadOnlyList<AskPage> Choose(IReadOnlyList<RankedPage> ranked, IReadOnlyDictionary<long, AskPage> pages, AskRetrievalOptions? options = null)
    {
        options ??= new AskRetrievalOptions();
        var chosen = new List<AskPage>();
        var budget = options.Budget;
        foreach (var rank in ranked)
        {
            if (chosen.Count >= options.MaxPages || budget <= 0) break;
            if (!pages.TryGetValue(rank.PageId, out var page)) continue;
            var text = ExcerptBuilder.Tidy(page.Text);
            if (text.Length == 0) continue;
            var cut = ExcerptBuilder.Cut(text, Math.Min(options.PageCharacters, budget));
            chosen.Add(page with { Text = cut });
            budget -= cut.Length;
        }
        return chosen;
    }

    /// <summary>
    /// A few lines of a page around the first of the question's words on it (or its opening, when none is), ending at
    /// words, and the word as the page spells it, for Find to mark when the page is opened.
    /// </summary>
    public static Passage Excerpt(string text, IReadOnlyList<string> words, int length = 300)
    {
        var tidy = ExcerptBuilder.Tidy(text);
        if (tidy.Length == 0) return new Passage("", null);
        var (at, phrase) = FirstWord(tidy, words);
        var start = Math.Max(0, at - length / 3);
        if (start > 0)
        {
            var space = tidy.IndexOf(' ', start);
            start = space >= 0 && space < at ? space + 1 : start;
        }
        var body = ExcerptBuilder.Cut(tidy[start..], length);
        return new Passage(start > 0 ? "…" + body : body, phrase);
    }

    /// <summary>Where the first of the words is in the text, and the whole word there; the start and null when none is.</summary>
    static (int At, string? Phrase) FirstWord(string text, IReadOnlyList<string> words)
    {
        var best = (At: 0, Phrase: (string?)null);
        if (words.Count == 0) return best;
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }
            var end = i;
            while (end < text.Length && char.IsLetterOrDigit(text[end])) end++;
            var token = Normal(text[i..end]);
            if (words.Any(w => w.Length >= MinWordStart ? token.StartsWith(w, StringComparison.Ordinal) : token == w)) return (i, text[i..end]);
            i = end;
        }
        return best;
    }

    /// <summary>The words of a text as search compares them: lower case, without accents, letters and digits only.</summary>
    static IEnumerable<string> Tokens(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            else if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }
        if (builder.Length > 0) yield return builder.ToString();
    }

    static string Normal(string word) => Tokens(word).FirstOrDefault() ?? "";
}

/// <summary>
/// The text a page or a question is turned into a vector from. Some embedding models are trained with a prefix that
/// says which is which (nomic-embed-text's "search_document: " and "search_query: "), and get it; others get the text
/// as it is. Changing what goes in bumps <see cref="Version"/>, which makes every stored vector stale.
/// </summary>
public static class EmbeddingText
{
    /// <summary>Stored with each vector in index.db's page_vec; a vector of another version is made again.</summary>
    public const int Version = 1;

    /// <summary>About 750 to 1,000 tokens: well inside an embedding model's 2,048-token window, with the heading in front.</summary>
    public const int MaxCharacters = 3_000;

    /// <summary>A page: its bookmark, then its text, cut to fit.</summary>
    public static string Document(string model, string? heading, string text)
    {
        var body = ExcerptBuilder.Cut(ExcerptBuilder.Tidy(text), MaxCharacters);
        return (Prefixed(model) ? "search_document: " : "") + (heading is { Length: > 0 } ? $"{heading}. {body}" : body);
    }

    /// <summary>A question.</summary>
    public static string Query(string model, string question) =>
        (Prefixed(model) ? "search_query: " : "") + ExcerptBuilder.Cut(ExcerptBuilder.Tidy(question), MaxCharacters);

    static bool Prefixed(string model) => model.Contains("nomic", StringComparison.OrdinalIgnoreCase);
}
