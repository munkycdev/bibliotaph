using System.Text.RegularExpressions;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core;

/// <summary>Where a book says it holds separate adventures: the PDF page and the sentence that says so.</summary>
public sealed record CollectionEvidence(int PdfPage, string Quote);

/// <summary>A compilation the rules found: where the book says it is one, and its adventures in page order.</summary>
public sealed record Compilation(CollectionEvidence Evidence, IReadOnlyList<ProposedPart> Parts);

public static partial class PartRules
{
    /// <summary>
    /// The opening pages read for the book's description of itself: covers, credits, contents, a foreword and an
    /// introduction come before this in any book.
    /// </summary>
    public const int FrontMatterPages = 24;

    /// <summary>The pages at the end read for a back-cover description.</summary>
    const int BackCoverPages = 2;

    /// <summary>A stored sentence is cut to about this many characters around the words that matched.</summary>
    const int MaxQuote = 240;

    /// <summary>
    /// The pages where a book describes itself, given that its first adventure starts on <paramref name="firstStart"/>:
    /// everything up to and including that page, within the front matter, and the back cover. Chapter blurbs
    /// further in describe one adventure, not the book.
    /// </summary>
    public static IReadOnlyList<int> DescriptionPages(int pageCount, int firstStart)
    {
        var front = Enumerable.Range(0, Math.Clamp(Math.Min(firstStart + 1, FrontMatterPages), 0, pageCount));
        var back = Enumerable.Range(Math.Max(0, pageCount - BackCoverPages), Math.Min(BackCoverPages, pageCount));
        return [.. front.Concat(back).Distinct().Order()];
    }

    /// <summary>
    /// Where the book says, in its own words, that it is a collection of separate adventures or one-shots ("a collection
    /// of five adventures", "four one-shots", "each can be run on its own"), on one of its <see cref="DescriptionPages"/>;
    /// or null. A sentence that calls them linked or a campaign doesn't count unless it also says they stand alone.
    /// </summary>
    public static CollectionEvidence? FindCollection(IReadOnlyList<string> pages, int firstStart)
    {
        foreach (var page in DescriptionPages(pages.Count, firstStart))
            if (CollectionSentence(pages[page]) is { } quote)
                return new CollectionEvidence(page, quote);
        return null;
    }

    /// <summary>The first sentence of <paramref name="text"/> that says the book holds separate adventures, cut to length; or null.</summary>
    public static string? CollectionSentence(string text)
    {
        foreach (var sentence in SentenceBreak().Split(MetadataText.Tidy(text)))
        {
            if (Linked().IsMatch(sentence) && !StandAlone().IsMatch(sentence)) continue;
            var match = Statements.Select(s => s.Match(sentence)).FirstOrDefault(m => m.Success);
            if (match is not null) return Around(sentence, match);
        }
        return null;
    }

    /// <summary>
    /// Whether a quote a model gave as the book's description of itself is about several separate adventures, rather
    /// than one adventure's blurb or a campaign's chapters. Looser than <see cref="CollectionSentence"/>, since a model
    /// reads phrasing no pattern lists, but it must still name adventures in the plural and not tie them into one story.
    /// </summary>
    public static bool DescribesCollection(string quote)
    {
        if (CollectionSentence(quote) is not null) return true;
        var tidy = MetadataText.Tidy(quote);
        return PluralAdventures().IsMatch(tidy) && (!Linked().IsMatch(tidy) || StandAlone().IsMatch(tidy));
    }

    static readonly Regex[] Statements = [OneShots(), StandAloneAdventures(), CollectionOf(), RunOnTheirOwn(), Counted()];

    static string Around(string sentence, Match match)
    {
        if (sentence.Length <= MaxQuote) return sentence;
        var margin = Math.Max(0, (MaxQuote - match.Length) / 2);
        var start = Math.Max(0, match.Index - margin);
        var end = Math.Min(sentence.Length, Math.Max(match.Index + match.Length, start + MaxQuote));
        return (start > 0 ? "…" : "") + sentence[start..end].Trim() + (end < sentence.Length ? "…" : "");
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    const string Adventures = @"(?:adventures|scenarios|modules|quests|missions|one[\s-]?shots)";

    /// <summary>"four one-shots", "one-shot adventures".</summary>
    [GeneratedRegex(@"\bone[\s-]?shots\b|\bone[\s-]?shot\s+(?:adventures|scenarios|games|modules)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OneShots();

    /// <summary>"stand-alone adventures", "self-contained scenarios", "five independent short adventures".</summary>
    [GeneratedRegex(@"\b(?:stand[\s-]?alone|self[\s-]contained|independent|separate|unconnected|unrelated)\s+(?:[\w-]+\s+){0,2}?" + Adventures + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandAloneAdventures();

    /// <summary>"a collection of five short adventures", "an anthology of scenarios", "this adventure anthology".</summary>
    [GeneratedRegex(@"\b(?:collection|anthology|compilation|compendium|selection|assortment)\s+of\s+(?:[\w-]+\s+){0,4}?" + Adventures + @"\b|\badventure\s+(?:anthology|collection|compilation)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectionOf();

    /// <summary>"each can be run on its own", "these adventures can be played in any order", "any of them may be used as a one-shot".</summary>
    [GeneratedRegex(@"\b(?:each|every|any|all|these|they|adventures|scenarios)\b(?:\s+[\w,'’-]+){0,6}?\s+(?:can|could|may|are\s+(?:designed|meant|written)\s+to)\s+(?:also\s+|each\s+|all\s+|easily\s+)?be\s+(?:run|played|used|enjoyed)\b(?:\s+[\w-]+){0,4}?\s+(?:on\s+(?:its|their)\s+own|alone|independently|separately|by\s+(?:itself|themselves)|in\s+any\s+order|as\s+(?:an?\s+)?(?:stand[\s-]?alones?|one[\s-]?shots?|self[\s-]contained))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RunOnTheirOwn();

    /// <summary>"contains seven adventures", "12 short scenarios", but not "the first two adventures".</summary>
    [GeneratedRegex(@"(?<!\b(?:first|last|next|previous|other|later|earlier|following)\s+)\b(?:two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|twenty|[2-9]|[1-9]\d)\s+(?:[\w-]+\s+){0,2}?" + Adventures + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Counted();

    /// <summary>Words that tie adventures into one story: a sentence with them isn't about separate adventures by itself.</summary>
    [GeneratedRegex(@"\b(?:linked|connected|interconnected|continuing|sequential|in\s+sequence|in\s+order|one\s+after\s+another|(?:forms?|forming|make\s+up|makes\s+up|making\s+up|comprises?)\s+(?:[\w-]+\s+){0,3}?(?:campaign|saga|story)|campaign\s+in\s+(?:[\w-]+\s+)?(?:parts|chapters|episodes))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Linked();

    /// <summary>Words that say adventures stand alone, which outweigh a mention of a campaign.</summary>
    [GeneratedRegex(@"\b(?:stand[\s-]?alones?|self[\s-]contained|on\s+(?:its|their)\s+own|independent(?:ly)?|separately|in\s+any\s+order|by\s+(?:itself|themselves)|one[\s-]?shots?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StandAlone();

    /// <summary>Adventures named in the plural, or an anthology of them.</summary>
    [GeneratedRegex(@"\b(?:adventures|scenarios|modules|quests|one[\s-]?shots|anthology)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PluralAdventures();
}
