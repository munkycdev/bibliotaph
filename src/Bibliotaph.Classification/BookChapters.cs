using Bibliotaph.Core;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Classification;

/// <summary>
/// Splits a book into the chapters a whole-book scan reads, and those chapters into batches the model reads one at a
/// time. Chapters come from the PDF's bookmarks, else from the entries of its printed contents page, else from even
/// runs of pages. A run of short sections becomes one chapter, so a book with a bookmark for every room isn't read a
/// page at a time, and a long chapter is read in parts, so no answer has to cover more than about 30 pages.
/// </summary>
public static class BookChapters
{
    /// <summary>A chapter shorter than this joins the one after it.</summary>
    public const int MinPages = 4;

    /// <summary>A chapter longer than this is read in parts of about <see cref="WindowPages"/>.</summary>
    public const int MaxPages = 30;

    /// <summary>How many pages a part of a long chapter, or of a book with no chapters, covers.</summary>
    public const int WindowPages = 25;

    /// <summary>A contents page is looked for among this many pages from the start.</summary>
    const int ContentsSearchPages = 15;

    /// <summary>Bookmarks deeper than this are sections inside a chapter, never chapters.</summary>
    const int MaxDepth = 2;

    const int MaxTitleLength = 80;

    /// <summary>The chapters of a book of <paramref name="pageCount"/> pages, in page order, covering every page once.</summary>
    /// <param name="pageCount">The book's pages.</param>
    /// <param name="outline">Its bookmarks, in the file's order.</param>
    /// <param name="pages">Its pages' text as indexed, with line breaks, for finding a contents page.</param>
    /// <param name="printed">How the book numbers its pages, for following a contents page's numbers.</param>
    public static IReadOnlyList<ScannedChapter> Split(int pageCount, IReadOnlyList<BookHeading> outline, IReadOnlyList<BookPage> pages, PrintedPages printed)
    {
        if (pageCount <= 0) return [];
        var (starts, source) = FromOutline(outline, pageCount) is { Count: >= 2 } bookmarks
            ? (bookmarks, ChapterSource.Outline)
            : FromContents(outline, pages, printed, pageCount) is { Count: >= 2 } listed
                ? (listed, ChapterSource.Contents)
                : ([], ChapterSource.Pages);
        if (starts.Count == 0) return Windows(0, pageCount - 1, null, ChapterSource.Pages);

        var chapters = new List<(string? Title, int First, int Last)>();
        // The cover, credits and contents before the first chapter are read too: a book's opening says a lot.
        if (starts[0].PdfPage > 0) chapters.Add((null, 0, starts[0].PdfPage - 1));
        for (var i = 0; i < starts.Count; i++)
            chapters.Add((starts[i].Title, starts[i].PdfPage, i + 1 < starts.Count ? starts[i + 1].PdfPage - 1 : pageCount - 1));

        var merged = Merge(chapters);
        return [.. merged.SelectMany(c => c.Last - c.First + 1 > MaxPages
            ? Windows(c.First, c.Last, c.Title, source)
            : [new ScannedChapter(c.Title ?? OpeningTitle, c.First, c.Last, source, ChapterReading.Waiting, [])])];
    }

    /// <summary>What the pages before the first chapter are called.</summary>
    public const string OpeningTitle = "Opening pages";

    /// <summary>
    /// The shallowest level of bookmarks that gives at least three chapter starts, one per page, inside the book.
    /// A single bookmark over the whole book, with the chapters under it, gives the level under it.
    /// </summary>
    static List<(string Title, int PdfPage)> FromOutline(IReadOnlyList<BookHeading> outline, int pageCount)
    {
        List<(string Title, int PdfPage)> best = [];
        for (var depth = 0; depth <= MaxDepth; depth++)
        {
            var starts = Starts(outline.Where(h => h.Depth <= depth).Select(h => (h.Title, h.PdfPage)), pageCount);
            if (starts.Count >= 3) return starts;
            if (starts.Count > best.Count) best = starts;
        }
        return best;
    }

    /// <summary>
    /// The entries of a printed contents page near the front of the book: each line's words before its page number,
    /// at the PDF page the number stands for.
    /// </summary>
    static List<(string Title, int PdfPage)> FromContents(IReadOnlyList<BookHeading> outline, IReadOnlyList<BookPage> pages, PrintedPages printed, int pageCount)
    {
        if (!printed.IsKnown) return [];
        var listed = PageReferences.ListedPages([.. outline.Select(h => (h.Title, h.PdfPage, h.Depth))], pageCount);
        foreach (var page in pages.Where(p => p.PdfPage < ContentsSearchPages).OrderBy(p => p.PdfPage))
        {
            var references = PageReferences.Find(page.Text, printed, listed.Contains(page.PdfPage));
            if (references.Count < 3) continue;
            var entries = new List<(string Title, int PdfPage)>();
            var lastLine = -1;
            foreach (var reference in references)
            {
                var lineStart = page.Text.LastIndexOfAny(['\n', '\r', '\f'], Math.Max(0, reference.Range.Start - 1)) + 1;
                // An index line lists several pages; a contents line names its first.
                if (lineStart == lastLine) continue;
                lastLine = lineStart;
                var title = EntryTitle(page.Text[lineStart..reference.Range.Start]);
                if (title.Length > 0 && reference.PageIndex > page.PdfPage) entries.Add((title, reference.PageIndex));
            }
            // A contents page lists chapters in the order they come; an index lists names alphabetically, and its pages jump about.
            var ordered = entries.Zip(entries.Skip(1)).Count(p => p.Second.PdfPage >= p.First.PdfPage);
            if (entries.Count >= 3 && ordered >= (entries.Count - 1) * 0.8) return Starts(entries, pageCount);
        }
        return [];
    }

    /// <summary>An entry's words, without its dot leaders and the separators before its page number.</summary>
    static string EntryTitle(string text)
    {
        var title = ExcerptBuilder.Tidy(text.Replace('…', ' ')).TrimEnd(' ', '.', '·', ',', ';', ':', '-', '–', '—');
        return title.Length > MaxTitleLength ? ExcerptBuilder.Cut(title, MaxTitleLength) : title;
    }

    /// <summary>Starts in page order inside the book, the first title kept where two share a page.</summary>
    static List<(string Title, int PdfPage)> Starts(IEnumerable<(string Title, int PdfPage)> starts, int pageCount) =>
        [.. starts.Where(s => s.PdfPage >= 0 && s.PdfPage < pageCount)
            .Select(s => (Title: ExcerptBuilder.Cut(ExcerptBuilder.Tidy(s.Title), MaxTitleLength), s.PdfPage))
            .Where(s => s.Title.Length > 0)
            .DistinctBy(s => s.PdfPage)
            .OrderBy(s => s.PdfPage)];

    /// <summary>
    /// Joins each chapter shorter than <see cref="MinPages"/> to the one after it, keeping the first title: a chapter's
    /// heading page and its sections read as one. The opening pages take the next chapter's title when they join it,
    /// and a short last chapter joins the one before.
    /// </summary>
    static List<(string? Title, int First, int Last)> Merge(List<(string? Title, int First, int Last)> chapters)
    {
        var merged = new List<(string? Title, int First, int Last)>();
        (string? Title, int First, int Last)? pending = null;
        foreach (var chapter in chapters)
        {
            var current = pending is { } waiting ? (waiting.Title ?? chapter.Title, waiting.First, chapter.Last) : chapter;
            if (current.Last - current.First + 1 < MinPages)
            {
                pending = current;
                continue;
            }
            pending = null;
            merged.Add(current);
        }
        if (pending is { } rest)
        {
            if (merged.Count > 0) merged[^1] = (merged[^1].Title, merged[^1].First, rest.Last);
            else merged.Add(rest);
        }
        return merged;
    }

    /// <summary>Even runs of about <see cref="WindowPages"/> pages from <paramref name="first"/> to <paramref name="last"/>.</summary>
    static List<ScannedChapter> Windows(int first, int last, string? title, ChapterSource source)
    {
        var count = last - first + 1;
        var parts = Math.Max(1, (int)Math.Ceiling(count / (double)WindowPages));
        var windows = new List<ScannedChapter>(parts);
        for (var i = 0; i < parts; i++)
        {
            var from = first + (int)((long)count * i / parts);
            var to = first + (int)((long)count * (i + 1) / parts) - 1;
            var name = title is null
                ? source == ChapterSource.Pages ? $"Pages {from + 1}–{to + 1}" : OpeningTitle
                : parts == 1 ? title : $"{title} ({i + 1} of {parts})";
            windows.Add(new ScannedChapter(name, from, to, source, ChapterReading.Waiting, []));
        }
        return windows;
    }

    /// <summary>
    /// Groups the chapters still to read (by index) into batches, each one model call: consecutive chapters up to
    /// <paramref name="maxPages"/> pages and <paramref name="maxCharacters"/> of text together. A chapter that is too
    /// long alone is a batch of its own, and its pages are cut to fit when the batch is built.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Batches(IReadOnlyList<ScannedChapter> chapters, Func<int, int> characters, int maxPages = MaxPages,
        int maxCharacters = ChapterPrompt.Budget)
    {
        var batches = new List<IReadOnlyList<int>>();
        var current = new List<int>();
        int pages = 0, size = 0;
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Reading != ChapterReading.Waiting)
            {
                Close();
                continue;
            }
            var chapter = chapters[i];
            var chapterPages = chapter.LastPage - chapter.FirstPage + 1;
            var chapterSize = Enumerable.Range(chapter.FirstPage, chapterPages).Sum(characters);
            if (current.Count > 0 && (pages + chapterPages > maxPages || size + chapterSize > maxCharacters)) Close();
            current.Add(i);
            pages += chapterPages;
            size += chapterSize;
        }
        Close();
        return batches;

        void Close()
        {
            if (current.Count > 0) batches.Add(current);
            current = [];
            pages = size = 0;
        }
    }

    /// <summary>
    /// The text of the pages a batch reads, tidied, with each page cut to an equal share of what is left of
    /// <paramref name="budget"/> once the shorter pages have their whole text: so a long chapter is read thinly from
    /// end to end rather than its first pages alone.
    /// </summary>
    public static IReadOnlyList<BookPage> Fit(IReadOnlyList<BookPage> pages, int budget)
    {
        var tidy = pages.Select(p => p with { Text = ExcerptBuilder.Tidy(p.Text) }).Where(p => p.Text.Length > 0).ToList();
        var total = tidy.Sum(p => p.Text.Length);
        if (total <= budget) return tidy;
        var cap = ShareOf([.. tidy.Select(p => p.Text.Length)], budget);
        return [.. tidy.Select(p => p with { Text = ExcerptBuilder.Cut(p.Text, cap) })];
    }

    /// <summary>The largest per-page length that keeps the pages, each cut to it, within <paramref name="budget"/>.</summary>
    static int ShareOf(List<int> lengths, int budget)
    {
        lengths.Sort();
        var left = budget;
        for (var i = 0; i < lengths.Count; i++)
        {
            var share = left / (lengths.Count - i);
            if (lengths[i] > share) return Math.Max(1, share);
            left -= lengths[i];
        }
        return Math.Max(1, lengths[^1]);
    }

    /// <summary>"pp. 21–45" for a chapter's PDF pages, counted from 1 as the reader's page box counts them.</summary>
    public static string DescribePages(ScannedChapter chapter) => chapter.Pages.Describe(null, null);
}
