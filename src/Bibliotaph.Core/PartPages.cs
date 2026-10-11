using System.Globalization;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Core;

/// <summary>
/// The pages of an adventure inside a file (slice 5 plan, choice 3): one continuous run of zero-based PDF pages,
/// inclusive. The file never changes; a part only borrows its pages.
/// </summary>
public readonly record struct PartPages(int First, int Last)
{
    public int Count => Last - First + 1;

    public bool Contains(int page) => page >= First && page <= Last;

    public bool Overlaps(PartPages other) => First <= other.Last && other.First <= Last;

    /// <summary>"pp. 34–52" or "p. 34", as the file prints its page numbers.</summary>
    public string Describe(IReadOnlyList<string?> labels) => Describe(PageNumbers.Display(First, labels), PageNumbers.Display(Last, labels), First == Last);

    /// <summary>"pp. 34–52" or "p. 34", from the printed labels of the first and last pages, or PDF pages without them.</summary>
    public string Describe(string? firstLabel, string? lastLabel) =>
        Describe(firstLabel ?? (First + 1).ToString(CultureInfo.CurrentCulture), lastLabel ?? (Last + 1).ToString(CultureInfo.CurrentCulture), First == Last);

    static string Describe(string first, string last, bool one) => one ? $"p. {first}" : $"pp. {first}–{last}";
}

/// <summary>
/// The rules for the pages of the adventures in one file (slice 5 plan, choice 3): one continuous range each, inside
/// the file, never sharing a page with another adventure in it; gaps between them are fine, and nothing nests.
/// </summary>
public static class PartRanges
{
    /// <summary>
    /// Why <paramref name="pages"/> can't be an adventure in a file of <paramref name="pageCount"/> pages beside its
    /// <paramref name="others"/>, or null when they can.
    /// </summary>
    public static string? Check(PartPages pages, int pageCount, IEnumerable<(string Title, PartPages Pages)> others)
    {
        if (pages.Last < pages.First) return "The last page comes before the first.";
        if (pages.First < 0 || pages.Last >= pageCount) return "Those pages aren't all in this file.";
        foreach (var (title, other) in others)
            if (other.Overlaps(pages)) return $"Those pages overlap “{title}”. Adventures in one file can't share pages.";
        return null;
    }

    /// <summary>
    /// The pages a bookmark covers when it is indexed as its own adventure (choice 4): from its page to the page before
    /// the next bookmark at the same depth or above that starts on a later page, or to the end of the file.
    /// </summary>
    public static PartPages FromBookmark(IReadOnlyList<(int Page, int Depth)> outline, int index, int pageCount)
    {
        var (first, depth) = outline[index];
        var last = pageCount - 1;
        for (var i = index + 1; i < outline.Count; i++)
        {
            if (outline[i].Depth > depth || outline[i].Page <= first) continue;
            last = outline[i].Page - 1;
            break;
        }
        return new PartPages(first, Math.Clamp(last, first, Math.Max(first, pageCount - 1)));
    }
}
