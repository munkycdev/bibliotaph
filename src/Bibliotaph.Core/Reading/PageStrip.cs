using System.Globalization;

namespace Bibliotaph.Core.Reading;

/// <summary>
/// What the reader's thumbnail strip marks on each page (the page in view, the pages of a binder's item, the pages with
/// notes) and which thumbnails it draws. Worked out here, apart from WPF, so it can be tested anywhere.
/// </summary>
public static class PageStrip
{
    /// <summary>Thumbnails drawn ahead either side of those in view, so a short scroll finds them ready.</summary>
    public const int DrawAround = 4;

    /// <summary>
    /// How many page notes are on each page, from each note's first and last PDF page. A note running past either end
    /// of the book counts on the pages it has here, and one wholly outside it counts nowhere.
    /// </summary>
    public static int[] NoteCounts(int pageCount, IEnumerable<(int First, int Last)> notes)
    {
        var counts = new int[Math.Max(0, pageCount)];
        foreach (var (first, last) in notes)
        {
            var from = Math.Max(0, Math.Min(first, last));
            var to = Math.Min(counts.Length - 1, Math.Max(first, last));
            for (var page = from; page <= to; page++) counts[page]++;
        }
        return counts;
    }

    /// <summary>
    /// A binder item's pages as the strip marks them, held to the book; null for a whole book, or for pages that aren't
    /// in this book at all.
    /// </summary>
    public static (int First, int Last)? ItemPages(int? first, int? last, int pageCount)
    {
        if (first is not { } from || last is not { } to || pageCount <= 0) return null;
        if (to < from) (from, to) = (to, from);
        if (to < 0 || from >= pageCount) return null;
        return (Math.Max(0, from), Math.Min(pageCount - 1, to));
    }

    /// <summary>
    /// The thumbnails to draw, in the order to draw them: those in view, from the first, then the ones either side,
    /// nearest first, turn about. Only these are drawn, so a 600-page book draws a dozen, not 600.
    /// </summary>
    public static IReadOnlyList<int> PagesToDraw(int firstInView, int lastInView, int pageCount, int around = DrawAround)
    {
        if (pageCount <= 0) return [];
        var first = Math.Clamp(Math.Min(firstInView, lastInView), 0, pageCount - 1);
        var last = Math.Clamp(Math.Max(firstInView, lastInView), 0, pageCount - 1);
        var pages = new List<int>(last - first + 1 + (2 * around));
        for (var page = first; page <= last; page++) pages.Add(page);
        for (var step = 1; step <= around; step++)
        {
            if (last + step < pageCount) pages.Add(last + step);
            if (first - step >= 0) pages.Add(first - step);
        }
        return pages;
    }

    /// <summary>
    /// What a screen reader says for a thumbnail, and its tooltip: "Page 12, 2 notes, in this item, the page in view".
    /// <paramref name="page"/> is the page as the book numbers it.
    /// </summary>
    public static string Describe(string page, int notes, bool inItem, bool current)
    {
        var parts = new List<string> { $"Page {page}" };
        if (notes > 0) parts.Add(notes == 1 ? "1 note" : $"{notes.ToString("N0", CultureInfo.CurrentCulture)} notes");
        if (inItem) parts.Add("in this item");
        if (current) parts.Add("the page in view");
        return string.Join(", ", parts);
    }
}

/// <summary>A binder item as reading ahead sees it: the book it opens in, whether it can open, and whether it opens as an image.</summary>
public sealed record ReadAheadItem(long? DocumentId, bool CanOpen, bool IsImage);

/// <summary>
/// Which binder item to open ahead of time, so moving to it is as quick as turning a page. The book already open draws
/// the next pages in it by itself; this covers the next item when it's in another book.
/// </summary>
public static class ReadAhead
{
    /// <summary>
    /// The index of the item after <paramref name="current"/> when it opens a PDF other than
    /// <paramref name="openDocument"/>. Null when there is no next item, it can't open, it's an image (quick to open
    /// anyway), or it's in the book already open.
    /// </summary>
    public static int? Next(IReadOnlyList<ReadAheadItem> items, int current, long? openDocument)
    {
        var next = current + 1;
        if (current < 0 || next >= items.Count) return null;
        var item = items[next];
        if (!item.CanOpen || item.IsImage || item.DocumentId is not { } document || document == openDocument) return null;
        return next;
    }
}
