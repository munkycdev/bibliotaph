using System.Globalization;
using System.Text.RegularExpressions;

namespace Bibliotaph.Core.Reading;

/// <summary>Where a book's printed page numbers came from, if anywhere.</summary>
public enum PrintedPagesSource
{
    /// <summary>Nothing says how the book numbers its pages, so no printed number is looked up.</summary>
    None,

    /// <summary>The PDF's own page labels.</summary>
    Labels,

    /// <summary>The numbers printed at the top or foot of the book's pages (its folios), read from the indexed text.</summary>
    Folios,
}

/// <summary>
/// Which PDF page a printed page number is on, so a number read off an index or contents page can go to its page.
/// The book's page labels decide when they number its pages. A book without them is read from the numbers printed
/// at the top or foot of its pages, when enough pages agree on how far the printed numbers are from the PDF's.
/// Otherwise nothing is known, and a PDF page is never taken to be the printed page of the same number: most books
/// start with a cover and front matter, so that guess would jump to the wrong page.
/// </summary>
public sealed partial class PrintedPages
{
    /// <summary>Folios count only when at least this many pages agree on the offset...</summary>
    public const int MinimumFolioPages = 3;

    /// <summary>...and at least this share of the pages that show a number at all.</summary>
    public const double MinimumFolioShare = 0.5;

    /// <summary>Lines at each end of a page that are looked at for its printed number.</summary>
    const int FolioLines = 2;

    /// <summary>
    /// A page showing a number this close to where the usual offset puts it is taken as that number's page, so a book
    /// with a few unnumbered plates bound in still finds the pages after them.
    /// </summary>
    const int FolioDrift = 10;

    readonly Dictionary<int, int> _labels;
    readonly Dictionary<int, List<int>> _folios;
    readonly int _offset;
    readonly int _pageCount;

    PrintedPages(PrintedPagesSource source, Dictionary<int, int> labels, Dictionary<int, List<int>> folios, int offset, int pageCount)
    {
        Source = source;
        _labels = labels;
        _folios = folios;
        _offset = offset;
        _pageCount = pageCount;
    }

    /// <summary>Nothing known: every lookup is null.</summary>
    public static PrintedPages None { get; } = new(PrintedPagesSource.None, [], [], 0, 0);

    public PrintedPagesSource Source { get; }

    public bool IsKnown => Source != PrintedPagesSource.None;

    /// <summary>
    /// From the PDF's page labels (null where a page has none). Only labels that are plain numbers count; a number two
    /// pages share is ambiguous and finds nothing. <see cref="None"/> when no label is a number.
    /// </summary>
    public static PrintedPages FromLabels(IReadOnlyList<string?> labels, int pageCount)
    {
        var byNumber = new Dictionary<int, int>();
        for (var i = 0; i < Math.Min(labels.Count, pageCount); i++)
        {
            if (Number(labels[i]?.Trim()) is not { } number) continue;
            byNumber[number] = byNumber.ContainsKey(number) ? -1 : i;
        }
        return byNumber.Count == 0 ? None : new PrintedPages(PrintedPagesSource.Labels, byNumber, [], 0, pageCount);
    }

    /// <summary>
    /// From the indexed text of a book's pages: a line at the top or foot of a page that is just a number ("42",
    /// "Page 42", or "42 | Chapter title") is its printed number. The offset most pages agree on maps the rest, when
    /// at least <see cref="MinimumFolioPages"/> pages and <see cref="MinimumFolioShare"/> of the numbered ones agree;
    /// otherwise <see cref="None"/>.
    /// </summary>
    public static PrintedPages FromFolios(IEnumerable<(int PdfPage, string Text)> pages, int pageCount)
    {
        var folios = new Dictionary<int, List<int>>();
        var votes = new Dictionary<int, int>();
        var numbered = 0;
        foreach (var (pdfPage, text) in pages)
        {
            if (pdfPage < 0 || pdfPage >= pageCount) continue;
            var found = FoliosOf(text);
            if (found.Count == 0) continue;
            numbered++;
            foreach (var number in found)
            {
                if (!folios.TryGetValue(number, out var list)) folios[number] = list = [];
                list.Add(pdfPage);
            }
            foreach (var offset in found.Select(n => pdfPage - n).Distinct()) votes[offset] = votes.GetValueOrDefault(offset) + 1;
        }
        if (votes.Count == 0) return None;
        // The offset most pages agree on; between equals, the smaller one, so the result doesn't depend on order.
        var (best, count) = votes.OrderByDescending(v => v.Value).ThenBy(v => Math.Abs(v.Key)).ThenBy(v => v.Key).Select(v => (v.Key, v.Value)).First();
        if (count < MinimumFolioPages || count < numbered * MinimumFolioShare) return None;
        return new PrintedPages(PrintedPagesSource.Folios, [], folios, best, pageCount);
    }

    /// <summary>The PDF page index of a printed page number, or null when the book has no such page or it isn't known.</summary>
    public int? IndexOf(int printed)
    {
        switch (Source)
        {
            case PrintedPagesSource.Labels:
                return _labels.TryGetValue(printed, out var index) && index >= 0 ? index : null;
            case PrintedPagesSource.Folios:
                var expected = printed + _offset;
                // A page that shows the number itself wins, nearest to where the offset puts it.
                if (_folios.TryGetValue(printed, out var showing)
                    && showing.Where(p => Math.Abs(p - expected) <= FolioDrift).OrderBy(p => Math.Abs(p - expected)).Select(p => (int?)p).FirstOrDefault() is { } shown)
                    return shown;
                return expected >= 0 && expected < _pageCount ? expected : null;
            default:
                return null;
        }
    }

    /// <summary>The numbers in the first and last lines of a page's text that read as its printed page number.</summary>
    static List<int> FoliosOf(string text)
    {
        var lines = text.Split(['\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ends = lines.Take(FolioLines).Concat(lines.Skip(Math.Max(FolioLines, lines.Length - FolioLines)));
        var found = new List<int>();
        foreach (var line in ends)
            if (FolioLine().Match(line) is { Success: true } match && Number(match.Groups["n"].Value) is { } number && !found.Contains(number))
                found.Add(number);
        return found;
    }

    /// <summary>A whole number with no leading zero, of at most four digits: how a page is printed.</summary>
    internal static int? Number(string? text) =>
        text is { Length: >= 1 and <= 4 } && text[0] is >= '1' and <= '9' && text.All(char.IsAsciiDigit)
            ? int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"^(?:(?:page|p\.)\s*(?<n>\d+)|(?<n>\d+)(?:\s*[|•·]\s*\S.*)?|.*\S\s*[|•·]\s*(?<n>\d+))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolioLine();
}
