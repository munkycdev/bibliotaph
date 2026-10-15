using System.Globalization;
using System.Text.RegularExpressions;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core;

/// <summary>
/// An adventure a rule or a model thinks a file holds: a title, its pages, the level range its blurb gives, and the
/// words that said so, which also give the party size when the blurb does (<see cref="PartRules.Party"/>).
/// </summary>
public sealed record ProposedPart(string Title, PartPages Pages, string? Levels = null, string? Quote = null);

/// <summary>
/// The rule that proposes the adventures in a compilation. The candidates are the top-level bookmarks, or the level
/// under a single top-level bookmark, leaving out front and back matter (credits, an introduction, appendices, an
/// index, the licence, covers and advertisements). A candidate starts an adventure when its first two pages carry an adventure's blurb ("for four
/// characters of 3rd level", "four 1st- to 2nd-level PCs", "levels 1-3", "an adventure for"); it runs to the next
/// candidate. Two or more starts make
/// a proposal. A file with no bookmarks is read page by page, and the blurb alone finds the starts, the first line of
/// the page being the title. The blurb gives the level range and party size. Blurbs alone never make a proposal: campaign books print
/// one at the start of every chapter, so the book must also say, before its first adventure or on its back cover, that
/// it is a collection of separate adventures (<see cref="FindCollection"/>).
/// </summary>
public static partial class PartRules
{
    /// <summary>Fewer starts than this aren't a compilation.</summary>
    public const int MinimumParts = 2;

    /// <summary>The pages read from each candidate for its blurb.</summary>
    const int BlurbPages = 2;

    /// <summary>
    /// The adventures <paramref name="outline"/> and <paramref name="pages"/> (each page's text, in order) say the
    /// file holds, with where the book says it is a collection; or null.
    /// </summary>
    public static Compilation? Propose(IReadOnlyList<(string Title, int Page, int Depth)> outline, IReadOnlyList<string> pages)
    {
        var count = pages.Count;
        if (count == 0) return null;
        var proposed = HasMarks(outline, count) ? FromBookmarks(Candidates(outline, count), pages) : FromPages(pages);
        if (proposed.Count < MinimumParts) return null;
        return FindCollection(pages, proposed[0].Pages.First) is { } evidence ? new Compilation(evidence, proposed) : null;
    }

    static bool HasMarks(IReadOnlyList<(string Title, int Page, int Depth)> outline, int pageCount) =>
        outline.Any(o => o.Page >= 0 && o.Page < pageCount && !string.IsNullOrWhiteSpace(o.Title));

    /// <summary>
    /// The bookmarks that could each start an adventure, with the pages up to the next: the top-level bookmarks, or the
    /// level under a single top-level one, leaving out front and back matter. A model is asked about these too. Empty
    /// for a file without bookmarks.
    /// </summary>
    public static IReadOnlyList<ProposedPart> Candidates(IReadOnlyList<(string Title, int Page, int Depth)> outline, int pageCount)
    {
        var marks = outline.Where(o => o.Page >= 0 && o.Page < pageCount && !string.IsNullOrWhiteSpace(o.Title)).ToList();
        if (marks.Count == 0) return [];
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

        var found = new List<ProposedPart>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var (title, page, _) = candidates[i];
            if (IsMatter(title)) continue;
            var end = candidates.Skip(i + 1).Select(c => c.Page).FirstOrDefault(p => p > page, pageCount) - 1;
            found.Add(new ProposedPart(MetadataText.Tidy(title), new PartPages(page, Math.Max(page, end))));
        }
        return found;
    }

    static List<ProposedPart> FromBookmarks(IReadOnlyList<ProposedPart> candidates, IReadOnlyList<string> pages)
    {
        var proposed = new List<ProposedPart>();
        foreach (var candidate in candidates)
            if (ReadBlurb(pages, candidate.Pages) is { } blurb)
                proposed.Add(candidate with { Levels = blurb.Levels?.ToString(), Quote = blurb.Quote });
        return proposed;
    }

    static List<ProposedPart> FromPages(IReadOnlyList<string> pages)
    {
        var starts = new List<(int Page, AdventureBlurb Blurb)>();
        for (var page = 0; page < pages.Count; page++)
        {
            if (Find(pages[page], page) is not { } found) continue;
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
        var words = Unnumbered(MetadataText.Words(MetadataText.Normalize(title)));
        if (words.Length == 0) return true;
        return words[0] is "credits" or "introduction" or "intro" or "foreword" or "preface" or "appendix" or "appendices" or "index" or "contents"
                or "ogl" or "legal" or "license" or "licence" or "cover" or "ad" or "ads" or "advert" or "advertisement" or "advertisements"
            || (words.Length >= 2 && words[0] is "front" or "back" or "rear" && words[1] == "cover")
            || (words.Length >= 3 && words[0] == "open" && words[1] == "game" && words[2] is "license" or "licence")
            || (words.Length >= 3 && words[0] == "table" && words[1] == "of" && words[2] == "contents");
    }

    /// <summary>A title's words without its numbering: "1. Introduction" and "Chapter 2: Credits" are matter too.</summary>
    static string[] Unnumbered(string[] words)
    {
        var skip = 0;
        if (words.Length > 1 && words[0] is "chapter" or "part" or "section" or "act") skip = 1;
        while (skip < words.Length - 1 && (words[skip].All(char.IsAsciiDigit) || RomanNumeral().IsMatch(words[skip]))) skip++;
        return words[skip..];
    }

    [GeneratedRegex(@"^(?:i{1,3}|iv|vi{0,3}|ix|xi{0,3})$")]
    private static partial Regex RomanNumeral();

    /// <summary>
    /// The blurb on the first page of <paramref name="range"/> or the page after, if either has one: for a proposal, and
    /// for an adventure marked by hand, whose levels and party size it suggests.
    /// </summary>
    public static AdventureBlurb? ReadBlurb(IReadOnlyList<string> pages, PartPages range)
    {
        // Only the adventure's own pages: the next one's blurb, or a back cover's, isn't this one's.
        for (var i = range.First; i < Math.Min(Math.Min(pages.Count, range.Last + 1), range.First + BlurbPages); i++)
            if (Find(pages[i], i) is { } found) return found;
        return null;
    }

    /// <summary>
    /// The party size a blurb's words give: "four" in "for four characters of 3rd level", 4–6 in "four to six 5th-level
    /// characters"; null when they give none ("for characters of levels 2-4").
    /// </summary>
    public static PlayerRange? Party(string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote)) return null;
        var match = PartySize().Match(MetadataText.Tidy(quote));
        if (!match.Success) return null;
        var count = match.Groups["max"].Success ? $"{match.Groups["min"].Value} to {match.Groups["max"].Value}" : match.Groups["min"].Value;
        return PlayerRange.TryParse(count, out var players) ? players : null;
    }

    /// <summary>A page giving this many different level ranges lists adventures, as a contents page does, rather than opening one.</summary>
    const int ListedLevels = 4;

    /// <summary>An adventure's blurb in a page's text: what it says, and the levels and party size it gives, if it gives them.</summary>
    static AdventureBlurb? Find(string text, int page)
    {
        var tidy = MetadataText.Tidy(text);
        var given = CharactersOfLevel().Matches(tidy).Concat(PartyOfLevel().Matches(tidy)).Concat(LevelsSpan().Matches(tidy))
            .Select(m => m.Value.ToUpperInvariant()).Distinct().Count();
        if (given >= ListedLevels) return null;
        if (CharactersOfLevel().Match(tidy) is { Success: true } characters)
            return WithParty(tidy, page, characters, Range(characters.Groups["min"].Value, characters.Groups["max"].Value));
        if (PartyOfLevel().Match(tidy) is { Success: true } party)
            return WithParty(tidy, page, party, Range(party.Groups["min"].Value, party.Groups["max"].Value));
        if (LevelsSpan().Match(tidy) is { Success: true } span)
            return WithParty(tidy, page, span, Range(span.Groups["min"].Value, span.Groups["max"].Value));
        if (AnAdventureFor().Match(tidy) is { Success: true } adventure)
        {
            var levels = LevelOrdinal().Match(tidy.Substring(adventure.Index, Math.Min(160, tidy.Length - adventure.Index)));
            return WithParty(tidy, page, adventure, levels.Success ? Range(levels.Groups["min"].Value, levels.Groups["max"].Value) : null, PartyReach);
        }
        return null;
    }

    /// <summary>How far before a blurb's words, or after "an adventure for", a party size still belongs to it.</summary>
    const int PartyReach = 60;

    /// <summary>
    /// The blurb, its quote widened to take in the party size written just around it: "four to six 5th-level
    /// characters" matches the levels from "six", and "for four players, levels 2-3" gives the levels after the count.
    /// </summary>
    static AdventureBlurb WithParty(string tidy, int page, Match blurb, LevelRange? levels, int after = 0)
    {
        var start = Math.Max(0, blurb.Index - PartyReach);
        // A cut word could leave "ven to nine" of "seven to nine", so the window starts at a word.
        while (start > 0 && char.IsLetterOrDigit(tidy[start - 1])) start--;
        var end = Math.Min(tidy.Length, blurb.Index + blurb.Length + after);
        // The nearest count is the blurb's own: a sentence before it may name another group.
        var blurbEnd = blurb.Index + blurb.Length;
        var party = PartySize().Matches(tidy[..end], start)
            .MinBy(m => Math.Max(0, Math.Max(m.Index - blurbEnd, blurb.Index - (m.Index + m.Length))));
        if (party is not null)
        {
            var from = Math.Min(party.Index, blurb.Index);
            var to = Math.Max(party.Index + party.Length, blurb.Index + blurb.Length);
            var quote = tidy[from..to];
            return new AdventureBlurb(page, quote, levels, Party(quote));
        }
        return new AdventureBlurb(page, blurb.Value, levels, null);
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

    /// <summary>"four 1st- to 2nd-level PCs", "4 3rd-level characters", "five 5th–7th level heroes".</summary>
    [GeneratedRegex(@"\b(?:one|two|three|four|five|six|seven|eight|\d)\s+(?<min>\d{1,2})(?:st|nd|rd|th)(?:[\s-]*(?:level[\s-]*)?(?:-|–|—|to|through)[\s-]*(?<max>\d{1,2})(?:st|nd|rd|th))?[\s-]*level\s+(?:pcs|characters|player\s+characters|adventurers|heroes)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartyOfLevel();

    /// <summary>"levels 1-3", "levels 1–3", "levels 5 to 7".</summary>
    [GeneratedRegex(@"\blevels\s+(?<min>\d{1,2})\s*(?:-|–|—|to|through)\s*(?<max>\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelsSpan();

    const string PartyCount = "(?:\\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)";
    const string PartyCounts = "(?<min>" + PartyCount + ")(?:\\s*(?:-|–|—|to|or)\\s*(?<max>" + PartyCount + "))?";

    /// <summary>
    /// A party size: "four characters", "4-6 PCs", "four to six 5th-level characters", "three 1st- to 2nd-level
    /// adventurers", "a party of 4". "4th-level characters" gives a level, not a count.
    /// </summary>
    [GeneratedRegex(@"\b" + PartyCounts + @"(?:\s+\d{1,2}(?:st|nd|rd|th)[\w\s–—-]{0,24}?)?\s+(?:characters|pcs|player\s+characters|players|adventurers|heroes)\b"
        + @"|\b(?:party|group)\s+of\s+" + PartyCounts + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartySize();

    /// <summary>"an adventure for".</summary>
    [GeneratedRegex(@"\ban?\s+(?:\S+\s+){0,2}?adventure\s+for\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnAdventureFor();

    /// <summary>"3rd level", "1st- to 3rd-level", "5th-level".</summary>
    [GeneratedRegex(@"\b(?<min>\d{1,2})(?:st|nd|rd|th)(?:[\s-]*level)?(?:\s*(?:-|–|—|to|through)\s*(?<max>\d{1,2})(?:st|nd|rd|th))?[\s-]*level",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LevelOrdinal();
}

/// <summary>
/// An adventure's blurb ("for four characters of 3rd level"): the page it is on, its words, and the levels and party
/// size they give, when they do.
/// </summary>
public sealed record AdventureBlurb(int Page, string Quote, LevelRange? Levels, PlayerRange? Players);
