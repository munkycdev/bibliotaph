using System.Text;
using System.Text.RegularExpressions;

namespace Bibliotaph.Classification;

/// <summary>A page's indexed text: what the PDF gave, or what OCR read.</summary>
public sealed record BookPage(int PdfPage, string Text);

/// <summary>An entry in the PDF's outline (bookmarks).</summary>
public sealed record BookHeading(string Title, int PdfPage, int Depth);

/// <summary>Why a page is in the excerpt. Values read from <see cref="Sampled"/> pages are marked as from sampling.</summary>
public enum ExcerptPart
{
    Opening,
    Contents,
    Introduction,
    Sampled,
}

/// <summary>One page as the model reads it, cut to its share of the budget.</summary>
public sealed record ExcerptPage(int PdfPage, string Text, ExcerptPart Part);

/// <summary>
/// The bounded part of a book the classifier reads (slice 2 plan, choice 9): its opening pages, its contents, its
/// introduction, its headings, then pages sampled evenly across the rest, each tagged with its PDF page number.
/// </summary>
public sealed record Excerpt(IReadOnlyList<ExcerptPage> Pages, IReadOnlyList<BookHeading> Headings, int PageCount)
{
    public bool IsEmpty => Pages.Count == 0;

    /// <summary>The PDF page indexes the model reads, in order.</summary>
    public IReadOnlyList<int> PdfPages => [.. Pages.Select(p => p.PdfPage)];

    public ExcerptPage? Find(int pdfPage) => Pages.FirstOrDefault(p => p.PdfPage == pdfPage);

    public int Characters => Pages.Sum(p => p.Text.Length) + Headings.Sum(h => h.Title.Length + 12);
}

public sealed record ExcerptOptions
{
    /// <summary>About 6,000 tokens of book text (choice 9), at roughly four characters a token.</summary>
    public int Budget { get; init; } = 24_000;

    public int OpeningPages { get; init; } = 3;

    /// <summary>A page that opens the book, or holds its contents or introduction, gives at most this much.</summary>
    public int KeyPageCharacters { get; init; } = 2_400;

    /// <summary>A sampled page gives at most this much, so the samples spread across the book.</summary>
    public int SampledPageCharacters { get; init; } = 1_200;

    public int MaxHeadings { get; init; } = 60;

    /// <summary>A page with less text than this (after tidying) is a picture, a map or a blank, and isn't read.</summary>
    public int MinPageCharacters { get; init; } = 40;
}

/// <summary>Chooses the pages the classifier reads, within a budget.</summary>
public static partial class ExcerptBuilder
{
    /// <summary>The opening pages are looked for among this many, so a book that starts with blank covers still has some.</summary>
    const int OpeningSearch = 8;

    /// <summary>Contents and introductions come early; later pages that look like them are usually indexes or chapters.</summary>
    const int FrontMatter = 24;

    public static Excerpt Build(IReadOnlyList<BookPage> pages, IReadOnlyList<BookHeading> outline, int pageCount, ExcerptOptions? options = null)
    {
        options ??= new ExcerptOptions();
        var texts = pages
            .Select(p => (p.PdfPage, Text: Tidy(p.Text)))
            .Where(p => p.Text.Length >= options.MinPageCharacters)
            .ToDictionary(p => p.PdfPage, p => p.Text);
        var headings = outline.Where(h => h.Depth <= 1 && Tidy(h.Title).Length > 0).Take(options.MaxHeadings)
            .Select(h => h with { Title = Cut(Tidy(h.Title), 80) }).ToList();
        var chosen = new Dictionary<int, ExcerptPage>();
        var budget = options.Budget - headings.Sum(h => h.Title.Length + 12);

        bool Take(int pdfPage, ExcerptPart part, int limit)
        {
            if (chosen.ContainsKey(pdfPage) || !texts.TryGetValue(pdfPage, out var text) || budget <= 0) return false;
            var cut = Cut(text, Math.Min(limit, budget));
            chosen[pdfPage] = new ExcerptPage(pdfPage, cut, part);
            budget -= cut.Length;
            return true;
        }

        foreach (var page in texts.Keys.Where(p => p < OpeningSearch).Order().Take(options.OpeningPages))
            Take(page, ExcerptPart.Opening, options.KeyPageCharacters);

        foreach (var page in texts.Where(t => t.Key < FrontMatter && LooksLikeContents(t.Value)).Select(t => t.Key).Order().Take(2))
            Take(page, ExcerptPart.Contents, options.KeyPageCharacters);

        var introduction = outline.Where(h => IntroductionHeading().IsMatch(Tidy(h.Title))).Select(h => (int?)h.PdfPage).FirstOrDefault()
            ?? texts.Where(t => t.Key < FrontMatter * 2 && !chosen.ContainsKey(t.Key) && IntroductionHeading().IsMatch(t.Value[..Math.Min(200, t.Value.Length)]))
                .Select(t => (int?)t.Key).Min();
        if (introduction is { } intro)
        {
            Take(intro, ExcerptPart.Introduction, options.KeyPageCharacters);
            // An introduction usually runs onto the next page, which says what the book is for.
            Take(intro + 1, ExcerptPart.Introduction, options.KeyPageCharacters / 2);
        }

        // The rest of the budget goes to pages spread evenly across the book.
        var rest = texts.Keys.Where(p => !chosen.ContainsKey(p)).Order().ToList();
        var room = budget / Math.Max(1, options.SampledPageCharacters);
        if (rest.Count > 0 && room > 0)
        {
            var count = Math.Min(rest.Count, room);
            for (var i = 0; i < count; i++)
                Take(rest[(int)((i + 0.5) * rest.Count / count)], ExcerptPart.Sampled, options.SampledPageCharacters);
        }

        return new Excerpt([.. chosen.Values.OrderBy(p => p.PdfPage)], headings, pageCount);
    }

    [GeneratedRegex(@"^(introduction|foreword|preface|overview|welcome|about this (book|adventure|module|supplement)|adventure (background|overview|summary)|background|running this adventure|using this book|how to use this book)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntroductionHeading();

    [GeneratedRegex(@"\b(table of )?contents\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContentsWord();

    [GeneratedRegex(@"(\.{3,}|…|\s)\s*\d{1,3}\b")]
    private static partial Regex LeaderNumber();

    /// <summary>A page that says Contents near its top, or reads like a list of headings with page numbers.</summary>
    internal static bool LooksLikeContents(string text)
    {
        if (ContentsWord().IsMatch(text[..Math.Min(160, text.Length)])) return true;
        var numbers = LeaderNumber().Count(text);
        var words = text.Count(c => c == ' ') + 1;
        return numbers >= 8 && numbers * 8 >= words;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>One space between words: line breaks and runs of spaces cost tokens and say nothing.</summary>
    internal static string Tidy(string text) => Whitespace().Replace(text, " ").Trim();

    /// <summary>At most <paramref name="limit"/> characters, ending at a word where it can.</summary>
    internal static string Cut(string text, int limit)
    {
        if (text.Length <= limit) return text;
        var space = text.LastIndexOf(' ', Math.Max(0, limit - 1));
        var end = space > limit * 3 / 4 ? space : limit;
        var builder = new StringBuilder(end + 1);
        builder.Append(text, 0, end).Append('…');
        return builder.ToString();
    }
}
