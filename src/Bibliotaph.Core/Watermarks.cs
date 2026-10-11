using System.Text.RegularExpressions;

namespace Bibliotaph.Core;

/// <summary>
/// Watermark lines: what a store prints on a copy for its buyer, such as "Prepared exclusively for Ana Example (Order
/// #1234)". They name a person and an order, so they never go to a model and are never kept (scenario review, row 7):
/// the classifier's excerpt leaves them out, and Get it from keeps only the store a watermark names. A line is a
/// watermark when it reads like one (who a copy was prepared, sold or licensed for, where it was downloaded from, an
/// order or transaction number, an email address), or when it is short and repeats on half or more of the pages, as a
/// watermark with only a name does, and as running headers do too (<see cref="PageFingerprints.Repeats"/>).
/// </summary>
public static partial class Watermarks
{
    /// <summary>A repeated line longer than this is a page's own text laid out as one line, not a watermark.</summary>
    public const int RepeatedLineLength = 160;

    [GeneratedRegex(
        @"\b(?:prepared|made|produced|licen[cs]ed|registered|issued|sold|purchased|bought|downloaded|personali[sz]ed|watermarked)\s+(?:exclusively\s+|especially\s+|solely\s+|only\s+)?(?:for|to|by)\b"
        + @"|\b(?:downloaded|purchased|bought|ordered)\s+(?:from|at|on|via)\b"
        + @"|\b(?:order|transaction|invoice|receipt)\b\s*(?:#|no\.?|number|id)?\s*[:#]?\s*\d{3,}"
        + @"|[\w.+-]+@[\w-]+(?:\.[\w-]+)+",
        RegexOptions.IgnoreCase)]
    private static partial Regex Wording();

    /// <summary>Whether a line reads like a watermark wherever it is: "Prepared exclusively for…", "Order #1234", an email address.</summary>
    public static bool ReadsLikeOne(string line) => Wording().IsMatch(line);

    /// <summary>Each page's text without its watermark lines, in page order.</summary>
    public static IReadOnlyList<string> Strip(IReadOnlyList<string?> pages)
    {
        var repeats = PageFingerprints.Repeats(pages);
        return [.. pages.Select(page => string.Join('\n', Lines(page).Where(line => !IsWatermark(line, repeats))))];
    }

    /// <summary>
    /// The watermark lines of a file's pages, each once, in the order they first appear. They are for finding the store
    /// a watermark names and nothing else: never stored, logged or sent anywhere.
    /// </summary>
    public static IReadOnlyList<string> Find(IReadOnlyList<string?> pages)
    {
        var repeats = PageFingerprints.Repeats(pages);
        return [.. pages.SelectMany(Lines).Where(line => IsWatermark(line, repeats)).Distinct(StringComparer.Ordinal)];
    }

    static bool IsWatermark(string line, Func<string, bool> repeats) =>
        ReadsLikeOne(line) || line.Length <= RepeatedLineLength && repeats(line);

    static IEnumerable<string> Lines(string? text) =>
        string.IsNullOrEmpty(text) ? []
        : text.Split(['\n', '\r', '\f', '\v', '\u2028', '\u2029'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
