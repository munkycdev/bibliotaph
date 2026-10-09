using System.Security.Cryptography;
using System.Text;

namespace Bibliotaph.Core;

/// <summary>
/// Page-text fingerprints, which tell two files of the same book apart from two different books (foundation slice,
/// F2). A page's fingerprint hashes its text with case, punctuation and spacing ignored, after dropping the lines that
/// repeat on most of the file's pages. Those are running headers and watermarks ("Prepared exclusively for ...", an
/// order number): a watermark repeats on every page of one copy and differs between copies, so dropping repeated lines
/// makes two watermarked copies of a book fingerprint alike without knowing any store's wording.
/// </summary>
public static class PageFingerprints
{
    /// <summary>A page with fewer letters and digits than this, once repeated lines are dropped, has no fingerprint.</summary>
    public const int MinimumCharacters = 40;

    /// <summary>Repeated lines are only looked for in files with at least this many pages of text.</summary>
    public const int RepeatedLinePages = 4;

    /// <summary>Two files are copies only when at least this many pages have fingerprints...</summary>
    public const int MinimumMatchingPages = 3;

    /// <summary>...and at least this share of their pages.</summary>
    public const double MinimumMatchingShare = 0.5;

    /// <summary>The fingerprint of each page, in page order; null for a page with too little text.</summary>
    public static IReadOnlyList<string?> Compute(IReadOnlyList<string?> pageTexts)
    {
        var pages = pageTexts.Select(Lines).ToList();
        var repeated = RepeatedLines(pages);
        return [.. pages.Select(lines => Hash([.. lines.Where(l => !repeated.Contains(Shape(l)))]))];
    }

    /// <summary>
    /// Whether two files hold the same book page for page: the same number of pages, every page's fingerprint equal
    /// (pages without one in the same places), and enough of them fingerprinted that the match means something.
    /// </summary>
    public static bool IsSameBook(IReadOnlyList<string?> a, IReadOnlyList<string?> b)
    {
        if (a.Count != b.Count || a.Count == 0) return false;
        var fingerprinted = 0;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            if (a[i] is not null) fingerprinted++;
        }
        return fingerprinted >= MinimumMatchingPages && fingerprinted >= a.Count * MinimumMatchingShare;
    }

    /// <summary>A page's lines in comparison form: lowercase, letters and digits, single spaces; empty lines dropped.</summary>
    static List<string> Lines(string? text)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) return lines;
        var line = new StringBuilder();
        var space = false;
        foreach (var c in text)
        {
            if (c is '\n' or '\r' or '\f' or '\v' or '\u2028' or '\u2029')
            {
                Flush();
                continue;
            }
            if (char.IsLetterOrDigit(c))
            {
                if (space && line.Length > 0) line.Append(' ');
                line.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else space = true;
        }
        Flush();
        return lines;

        void Flush()
        {
            if (line.Length > 0) lines.Add(line.ToString());
            line.Clear();
            space = false;
        }
    }

    /// <summary>
    /// The shapes of lines that appear on half or more of the pages with text. A line's shape has its digits masked,
    /// so a watermark that carries the page number ("Order 123 · page 7") still counts as one line.
    /// </summary>
    static HashSet<string> RepeatedLines(List<List<string>> pages)
    {
        var withText = pages.Count(p => p.Count > 0);
        if (withText < RepeatedLinePages) return [];
        var pagesWith = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var page in pages)
            foreach (var shape in page.Select(Shape).Distinct(StringComparer.Ordinal))
                pagesWith[shape] = pagesWith.GetValueOrDefault(shape) + 1;
        return [.. pagesWith.Where(p => p.Value * 2 >= withText).Select(p => p.Key)];
    }

    static string Shape(string line)
    {
        var shape = new StringBuilder(line.Length);
        var digits = false;
        foreach (var c in line)
        {
            if (char.IsDigit(c))
            {
                if (!digits) shape.Append('#');
                digits = true;
            }
            else
            {
                shape.Append(c);
                digits = false;
            }
        }
        return shape.ToString();
    }

    static string? Hash(List<string> lines)
    {
        var text = string.Join(' ', lines);
        if (text.Count(char.IsLetterOrDigit) < MinimumCharacters) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}
