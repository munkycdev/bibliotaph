using System.Text.Json.Serialization;

namespace Bibliotaph.Core;

/// <summary>Where a scanned chapter's pages came from.</summary>
public enum ChapterSource
{
    /// <summary>A top-level bookmark of the file.</summary>
    Outline,

    /// <summary>An entry on the book's printed contents page, with its page number.</summary>
    Contents,

    /// <summary>A run of pages, for a book with neither: what the reader sees as "pp. 21–45".</summary>
    Pages,
}

/// <summary>How far the AI has got with one chapter.</summary>
public enum ChapterReading
{
    /// <summary>Not read yet.</summary>
    Waiting,

    /// <summary>Read; its tags are what passed the evidence check, which may be none.</summary>
    Read,

    /// <summary>The model's answer for it couldn't be used. It isn't asked again until the book is scanned again.</summary>
    Failed,
}

/// <summary>
/// The fields a whole-book scan tags chapters with. Setting, environment and theme are the book-level fields of the
/// same names and use their vocabularies; creatures are free text, since no vocabulary could list them all.
/// </summary>
public static class ChapterFields
{
    public const string Setting = "setting";
    public const string Environment = "environment";
    public const string Theme = "theme";
    public const string Creature = "creature";

    /// <summary>The fields in the order they are asked for and shown.</summary>
    public static IReadOnlyList<string> All { get; } = [Setting, Environment, Theme, Creature];

    /// <summary>
    /// The prefix a chapter's values carry among a book's facets, so a search for a setting finds a book set there in
    /// one chapter while nothing mistakes that for the book's own setting.
    /// </summary>
    public const string InsidePrefix = "inside:";

    /// <summary>The facet field a chapter's value of <paramref name="field"/> is filed under: "inside:setting".</summary>
    public static string Inside(string field) => InsidePrefix + field;

    /// <summary>The field of an inside facet, or the field itself when it isn't one.</summary>
    public static string Outside(string facetField) =>
        facetField.StartsWith(InsidePrefix, StringComparison.Ordinal) ? facetField[InsidePrefix.Length..] : facetField;

    /// <summary>The vocabulary a field's values come from, or null for free text.</summary>
    public static string? VocabularyOf(string field) => field == Creature ? null : field;

    /// <summary>One line per field with values, in field order: "Creatures: Goblin, Ogre".</summary>
    public static IReadOnlyList<string> Describe(IEnumerable<ChapterTag> tags)
    {
        var byField = tags.ToLookup(t => t.Field, StringComparer.Ordinal);
        return [.. All.Where(f => byField[f].Any()).Select(f => $"{Heading(f)}: {string.Join(", ", byField[f].Select(t => t.Label).Distinct())}")];
    }

    /// <summary>"Setting", "Environments", "Themes", "Creatures": the heading a field's values show under.</summary>
    public static string Heading(string field) => field switch
    {
        Setting => "Setting",
        Environment => "Environments",
        Theme => "Themes",
        Creature => "Creatures",
        _ => field,
    };
}

/// <summary>One value the AI found in a chapter, with the page and the words that show it.</summary>
/// <param name="Field">One of <see cref="ChapterFields.All"/>.</param>
/// <param name="Value">A vocabulary term's key, or a creature's name in plain lower case.</param>
/// <param name="Label">What to show: the term's label, or the creature as the book wrote it.</param>
/// <param name="PdfPage">The zero-based PDF page the quote is on.</param>
/// <param name="Quote">The book's own words, kept as evidence; never shown outside the book's own panels.</param>
public sealed record ChapterTag(string Field, string Value, string Label, int PdfPage, string Quote);

/// <summary>A chapter of a scanned book: its title, its zero-based PDF pages (inclusive), and what was found in it.</summary>
public sealed record ScannedChapter(string Title, int FirstPage, int LastPage, ChapterSource Source, ChapterReading Reading, IReadOnlyList<ChapterTag> Tags)
{
    [JsonIgnore]
    public PartPages Pages => new(FirstPage, LastPage);
}
