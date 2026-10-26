using System.Globalization;
using System.Text.RegularExpressions;

namespace Bibliotaph.Core.Reading;

/// <summary>A printed page number read off a page: the characters it covers ("34–36" whole), the number, and its PDF page.</summary>
public readonly record struct PageReference(TextRange Range, int Printed, int PageIndex);

/// <summary>
/// Finds the printed page numbers on an index or contents page whose entries aren't links in the PDF, so the reader
/// can follow them anyway. It is deliberately cautious: a number becomes a reference only at the end of a line, after
/// an entry's words (with a comma, dot leaders or a space between), on a page that plainly lists pages, and only when
/// the book has a page with that printed number (<see cref="PrintedPages"/>). A page lists pages when eight or more
/// of its lines end that way and they are most of its lines; when four or more do after dot leaders, as a contents
/// page's do; or, on a page under an "Index" or "Contents" bookmark or heading, when three or more do and they are at
/// least half its lines. Numbers inside sentences, dice (2d6, 1d20 + 5), stat
/// lines (AC 15), numbered table rows (1–2, 3–4, 5–6 down a column), copyright years and numbers with a leading zero
/// never count.
/// </summary>
public static partial class PageReferences
{
    /// <summary>A page with no hint lists pages when at least this many lines end in page numbers...</summary>
    public const int PlainMinimumLines = 8;

    /// <summary>...and they are at least this share of its lines with words.</summary>
    public const double PlainMinimumShare = 0.6;

    /// <summary>A contents page: at least this many lines end in dot leaders and a number...</summary>
    public const int LeaderMinimumLines = 4;

    /// <summary>...and the lines ending in page numbers are at least this share of its lines with words.</summary>
    public const double LeaderMinimumShare = 0.4;

    /// <summary>Under an "Index" or "Contents" bookmark or heading, at least this many lines end in page numbers...</summary>
    public const int HintedMinimumLines = 3;

    /// <summary>...and they are at least this share of its lines with words.</summary>
    public const double HintedMinimumShare = 0.5;

    /// <summary>An entry longer than this without dot leaders reads as a sentence.</summary>
    const int MaxEntryLength = 60;

    /// <summary>Longer lines are prose or a run-together column, and aren't looked at.</summary>
    const int MaxLineLength = 300;

    /// <summary>A bookmark marks at most this many pages, from its page on, as an index or contents.</summary>
    const int MaxListedPages = 40;

    /// <summary>Lines that count up from 1 like this many rows of a dice table are a table, not page numbers.</summary>
    const int TableRun = 3;

    /// <summary>
    /// The page references on a page. <paramref name="listed"/> says the page is under an "Index" or "Contents"
    /// bookmark (<see cref="ListedPages"/>). Empty when the page doesn't plainly list pages or nothing says how the
    /// book numbers its pages.
    /// </summary>
    public static IReadOnlyList<PageReference> Find(string text, PrintedPages printed, bool listed = false)
    {
        if (!printed.IsKnown || text.Length == 0) return [];
        var entries = new List<Entry>();
        var wordLines = 0;
        var heading = false;
        var seen = 0;
        var open = false;
        foreach (var (start, line) in Lines(text))
        {
            // The heading is the page's title, not one of its lines.
            if (seen++ < 3 && ReferenceTitle().IsMatch(line)) heading = true;
            else if (line.Count(char.IsLetter) >= 2) wordLines++;
            if (Read(line, start, printed, open) is { } entry) entries.Add(entry);
            open = line[^1] is ',' or ';';
        }

        DropTables(entries);
        var refLines = entries.Count(e => e.HasEntry);
        var leaderLines = entries.Count(e => e is { HasEntry: true, Leaders: true });
        var share = wordLines == 0 ? 0 : (double)refLines / wordLines;
        var lists = (leaderLines >= LeaderMinimumLines && share >= LeaderMinimumShare)
            || ((listed || heading) && refLines >= HintedMinimumLines && share >= HintedMinimumShare)
            || (refLines >= PlainMinimumLines && share >= PlainMinimumShare);
        return lists ? [.. entries.SelectMany(e => e.References)] : [];
    }

    /// <summary>
    /// What a link made from a printed number says it does: "Go to page 34" as the book numbers its pages, naming the
    /// PDF page too when the rest of the reader counts PDF pages there, as in a book without page labels whose number
    /// isn't the PDF page's: "Go to page 34 (PDF page 36)". <paramref name="label"/> is the target page's label.
    /// </summary>
    public static string Tip(int printed, int pageIndex, string? label)
    {
        var number = printed.ToString(CultureInfo.CurrentCulture);
        var same = label is null ? printed == pageIndex + 1 : label.Trim() == printed.ToString(CultureInfo.InvariantCulture);
        return same ? $"Go to page {number}" : $"Go to page {number} (PDF page {(pageIndex + 1).ToString(CultureInfo.CurrentCulture)})";
    }

    /// <summary>
    /// The pages under a bookmark titled "Index", "Contents", "Table of Contents" (or "Monster Index", "Appendix C:
    /// Index"): from its page up to the next bookmark at its level or above, at most <see cref="MaxListedPages"/>.
    /// </summary>
    public static IReadOnlySet<int> ListedPages(IReadOnlyList<(string Title, int PageIndex, int Depth)> outline, int pageCount)
    {
        var pages = new HashSet<int>();
        for (var i = 0; i < outline.Count; i++)
        {
            var (title, start, depth) = outline[i];
            if (start < 0 || start >= pageCount || !ReferenceTitle().IsMatch(title.Trim())) continue;
            var end = pageCount;
            for (var j = i + 1; j < outline.Count; j++)
            {
                if (outline[j].Depth > depth || outline[j].PageIndex <= start) continue;
                end = outline[j].PageIndex;
                break;
            }
            for (var page = start; page < Math.Min(end, start + MaxListedPages); page++) pages.Add(page);
        }
        return pages;
    }

    /// <summary>
    /// A line ending in page numbers. <see cref="HasEntry"/> is false for a line of numbers alone that carries on the
    /// line before; <see cref="First"/> and <see cref="Last"/> are its first and last numbers (the end of a range).
    /// </summary>
    sealed record Entry(bool HasEntry, bool Leaders, int First, int? Last, List<PageReference> References);

    /// <summary>The references at the end of one line (trimmed, starting at <paramref name="start"/> in the page's text), if it has any.</summary>
    static Entry? Read(string line, int start, PrintedPages printed, bool open)
    {
        if (line.Length > MaxLineLength || line.Contains('©') || line.Contains("copyright", StringComparison.OrdinalIgnoreCase)) return null;
        if (ReferenceLine().Match(line) is not { Success: true } match) return null;

        var lead = match.Groups["lead"];
        var leaders = false;
        if (lead.Success)
        {
            var sep = match.Groups["sep"].Value;
            leaders = sep.Count(c => c is '.' or '…' or '·') >= 2 || sep.Contains('…');
            var entry = lead.Value;
            var last = entry[^1];
            if (entry.Count(char.IsLetter) < 2) return null;
            // "Chapter 1 ....... 5" is a contents line; "Hit Dice 3 15" and "1d20 + 5" are not.
            if ((char.IsDigit(last) && !leaders) || last is '+' or '-' or '−' or '×' or '*' or '/' or '=' or '<' or '>' or '±' or '%' or '#' or '$') return null;
            if (!leaders && (entry.Length > MaxEntryLength || SentenceBreak().IsMatch(entry))) return null;
            if (string.IsNullOrWhiteSpace(sep) && StatWord().IsMatch(entry)) return null;
        }
        // Numbers alone on a line only carry on an entry whose line ended in a comma: otherwise they are the page's own number.
        else if (!open) return null;

        var references = new List<PageReference>();
        var captures = match.Groups["ref"].Captures;
        foreach (Capture capture in captures)
        {
            var (first, _) = Range(capture.Value);
            if (first is { } number && printed.IndexOf(number) is { } index)
                references.Add(new PageReference(new TextRange(start + capture.Index, capture.Length), number, index));
        }
        if (references.Count == 0) return null;
        var (from, _) = Range(captures[0].Value);
        var (lastFirst, lastEnd) = Range(captures[^1].Value);
        return new Entry(lead.Success, leaders, from ?? 0, lastEnd ?? lastFirst, references);
    }

    /// <summary>A reference's first number, and the number a range ends at ("34–36" is 34 and 36; "34" is 34 and null).</summary>
    static (int? First, int? End) Range(string reference)
    {
        var dash = reference.IndexOfAny(['-', '–', '—']);
        return dash < 0
            ? (PrintedPages.Number(reference.Trim()), null)
            : (PrintedPages.Number(reference[..dash].Trim()), PrintedPages.Number(reference[(dash + 1)..].Trim()));
    }

    /// <summary>
    /// Drops runs of lines whose numbers count on from 1 (1–2, 3–4, 5–6, or 1, 2, 3): the rows of a dice table. Lines
    /// with dot leaders are a contents page's, and stay.
    /// </summary>
    static void DropTables(List<Entry> entries)
    {
        var drop = new HashSet<Entry>(ReferenceEqualityComparer.Instance);
        var run = new List<Entry>();
        foreach (var entry in entries)
        {
            if (entry.Leaders || !entry.HasEntry)
            {
                Close();
                continue;
            }
            if (run.Count > 0 && run[^1].Last is { } last && entry.First == last + 1) run.Add(entry);
            else
            {
                Close();
                run.Add(entry);
            }
        }
        Close();
        entries.RemoveAll(drop.Contains);

        void Close()
        {
            if (run.Count >= TableRun && run[0].First == 1) drop.UnionWith(run);
            run.Clear();
        }
    }

    /// <summary>The page's lines, trimmed, with where each starts in the text; empty lines are skipped.</summary>
    static IEnumerable<(int Start, string Line)> Lines(string text)
    {
        var start = 0;
        while (start <= text.Length)
        {
            var end = text.IndexOfAny(['\n', '\r', '\f', '\u2028', '\u2029'], start);
            if (end < 0) end = text.Length;
            var line = text[start..end];
            var lead = line.Length - line.TrimStart().Length;
            var trimmed = line.Trim();
            if (trimmed.Length > 0) yield return (start + lead, trimmed);
            start = end + 1;
        }
    }

    // An entry, then a comma, colon, semicolon, dot leaders or a space, then page numbers and ranges separated by
    // commas or semicolons to the end of the line (where a comma may say the entry carries on). With no entry, the
    // line is numbers alone.
    [GeneratedRegex(@"^(?:(?<lead>.*?[^\s,;:…·])(?<sep>\s*(?:…|[.…·](?:\s?[.…·])+|[,;:])\s*|\s+))?(?:(?<ref>[1-9]\d{0,3}(?:\s*[-–—]\s*\d{1,4})?)\s*[,;]\s*)*(?<ref>[1-9]\d{0,3}(?:\s*[-–—]\s*\d{1,4})?)\s*[,;]?$", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceLine();

    // "Index", "Contents", "Table of Contents", "Monster Index", "Appendix C: Index", "Index (continued)".
    [GeneratedRegex(@"^(?:[^:]{0,40}:\s*)?(?:[\p{L}’' ]{1,24}\s)?(?:index|contents|table of contents)(?:\s*\(cont(?:inued|'d|’d|\.)?\)|\s+cont(?:inued|'d|’d|\.)?)?:?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceTitle();

    // Two lowercase letters, a full stop and a capital: one sentence ending and another starting.
    [GeneratedRegex(@"\p{Ll}{2}[.!?]\s+\p{Lu}", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    // A stat block's label just before its number: "AC 15", "Hit Points 27", "STR 18".
    [GeneratedRegex(@"(?:^|\W)(?:str|dex|con|int|wis|cha|ac|hp|hd|xp|cr|mv|dc|thac0|armou?r class|hit points|hit dice|challenge|speed|initiative|perception|morale)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StatWord();
}
