using System.Globalization;
using System.Text.RegularExpressions;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core;

/// <summary>
/// An adventure a rule or a model thinks a file holds (slice 5 plan, choices 5 and 6): a title, its pages, the level
/// range its blurb gives, and the words that said so.
/// </summary>
public sealed record ProposedPart(string Title, PartPages Pages, string? Levels = null, string? Quote = null);

/// <summary>
/// The rule that proposes the adventures in a compilation (slice 5 plan, choice 5). The candidates are the top-level
/// bookmarks, or the level under a single top-level bookmark, leaving out front and back matter (credits, an
/// introduction, appendices, an index, the licence). A candidate starts an adventure when its first two pages carry an
/// adventure's blurb ("for four characters of 3rd level", "levels 1-3", "an adventure for"); it runs to the next
/// candidate. Two or more starts make a proposal. A file with no bookmarks is read page by page, and the blurb alone
/// decides, the first line of the page being the title. The blurb gives the level range.
/// </summary>
public static partial class PartRules
{
    /// <summary>Fewer starts than this aren't a compilation.</summary>
    public const int MinimumParts = 2;

    /// <summary>The pages read from each candidate for its blurb.</summary>
    const int BlurbPages = 2;

    /// <summary>
    /// The adventures <paramref name="outline"/> and <paramref name="pages"/> (each page's text, in order) say the
    /// file holds, or none.
    /// </summary>
    public static IReadOnlyList<ProposedPart> Propose(IReadOnlyList<(string Title, int Page, int Depth)> outline, IReadOnlyList<string> pages)
    {
        var count = pages.Count;
        if (count == 0) return [];
        var marks = outline.Where(o => o.Page >= 0 && o.Page < count && !string.IsNullOrWhiteSpace(o.Title)).ToList();
        var proposed = marks.Count == 0 ? FromPages(pages) : FromBookmarks(marks, pages);
        return proposed.Count >= MinimumParts ? proposed : [];
    }

    static List<ProposedPart> FromBookmarks(List<(string Title, int Page, int Depth)> marks, IReadOnlyList<string> pages)
    {
        var top = marks.Min(m => m.Depth);
        var candidates = marks.Where(m => m.Depth == top).ToList();
        // A single top-level bookmark is the book's own: its adventures are the level under it.
        if (candidates.Count == 1)
        {
            var under = marks.Where(m => m.Depth > top).ToList();
            if (under.Count == 0) return [];
            var next = under.Min(m => m.Depth);
            candidates = [.. under.Where(m => m.Depth == next)];
        }
        candidates = [.. candidates.OrderBy(c => c.Page)];

        var proposed = new List<ProposedPart>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var (title, page, _) = candidates[i];
            if (IsMatter(title)) continue;
            if (Blurb(pages, page) is not { } blurb) continue;
            var end = candidates.Skip(i + 1).Select(c => c.Page).FirstOrDefault(p => p > page, pages.Count) - 1;
            proposed.Add(new ProposedPart(MetadataText.Tidy(title), new PartPages(page, Math.Max(page, end)), blurb.Levels?.ToString(), blurb.Quote));
        }
        return proposed;
    }

    static List<ProposedPart> FromPages(IReadOnlyList<string> pages)
    {
        var starts = new List<(int Page, Found Blurb)>();
        for (var page = 0; page < pages.Count; page++)
        {
            if (Find(pages[page]) is not { } found) continue;
            // A blurb running onto the next page is still the one adventure's.
            if (starts.Count > 0 && page - starts[^1].Page < BlurbPages) continue;
            starts.Add((page, found));
        }
        var proposed = new List<ProposedPart>(starts.Count);
        for (var i = 0; i < starts.Count; i++)
        {
            var (page, blurb) = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1].Page - 1 : pages.Count - 1;
            var title = FirstLine(pages[page]) ?? $"Adventure {(i + 1).ToString(CultureInfo.CurrentCulture)}";
            proposed.Add(new ProposedPart(title, new PartPages(page, end), blurb.Levels?.ToString(), blurb.Quote));
        }
        return proposed;
    }

    /// <summary>Front and back matter that is never an adventure, however its pages read.</summary>
    public static bool IsMatter(string title)
    {
        var words = MetadataText.Words(MetadataText.Normalize(title));
        if (words.Length == 0) return true;
        return words[0] is "credits" or "introduction" or "intro" or "foreword" or "preface" or "appendix" or "appendices" or "index" or "contents"
                or "ogl" or "legal" or "license" or "licence"
            || (words.Length >= 3 && words[0] == "open" && words[1] == "game" && words[2] is "license" or "licence")
            || (words.Length >= 3 && words[0] == "table" && words[1] == "of" && words[2] == "contents");
    }

    sealed record Found(string Quote, LevelRange? Levels);

    /// <summary>The blurb on <paramref name="page"/> or the page after, if either has one.</summary>
    static Found? Blurb(IReadOnlyList<string> pages, int page)
    {
        for (var i = page; i < Math.Min(pages.Count, page + BlurbPages); i++)
            if (Find(pages[i]) is { } found) return found;
        return null;
    }

    /// <summary>An adventure's blurb in a page's text: what it says, and the levels it gives, if it gives them.</summary>
    static Found? Find(string text)
    {
        var tidy = MetadataText.Tidy(text);
        if (CharactersOfLevel().Match(tidy) is { Success: true } characters)
            return new Found(characters.Value, Range(characters.Groups["min"].Value, characters.Groups["max"].Value));
        if (LevelsSpan().Match(tidy) is { Success: true } span)
            return new Found(span.Value, Range(span.Groups["min"].Value, span.Groups["max"].Value));
        if (AnAdventureFor().Match(tidy) is { Success: true } adventure)
        {
            var levels = LevelOrdinal().Match(tidy.Substring(adventure.Index, Math.Min(160, tidy.Length - adventure.Index)));
            return new Found(adventure.Value, levels.Success ? Range(levels.Groups["min"].Value, levels.Groups["max"].Value) : null);
        }
        return null;
    }

    static LevelRange? Range(string min, string max)
    {
        if (!int.TryParse(min, NumberStyles.None, CultureInfo.InvariantCulture, out var low)) return null;
        var high = int.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : low;
        return low >= 0 && high <= LevelRange.Highest && low <= high ? new LevelRange(low, high) : null;
    }

    static string? FirstLine(string text)
    {
        var line = text.Split('\n').Select(MetadataText.Tidy).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : line.Length > 80 ? line[..80].TrimEnd() : line;
    }

    /// <summary>"for four characters of 3rd level", "for 3-5 characters of 1st to 4th level", "for characters of levels 2-4".</summary>
    [GeneratedRegex(@"\bfor\s+(?:\S+\s+){0,4}?characters\s+of\s+(?:levels?\s+)?(?<min>\d{1,2})(?:st|nd|rd|th)?(?:\s*(?:-|–|—|to|through)\s*(?<max>\d{1,2})(?:st|nd|rd|th)?)?(?:\s+level)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CharactersOfLevel();

    /// <summary>"levels 1-3", "levels 1–3", "levels 5 to 7".</summary>
    [GeneratedRegex(@"\blevels\s+(?<min>\d{1,2})\s*(?:-|–|—|to|through)\s*(?<max>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelsSpan();

    /// <summary>"an adventure for".</summary>
    [GeneratedRegex(@"\ban?\s+(?:\S+\s+){0,2}?adventure\s+for\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnAdventureFor();

    /// <summary>"3rd level", "1st- to 3rd-level", "5th-level".</summary>
    [GeneratedRegex(@"\b(?<min>\d{1,2})(?:st|nd|rd|th)(?:[\s-]*level)?(?:\s*(?:-|–|—|to|through)\s*(?<max>\d{1,2})(?:st|nd|rd|th))?[\s-]*level",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelOrdinal();
}
