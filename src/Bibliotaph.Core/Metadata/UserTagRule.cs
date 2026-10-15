namespace Bibliotaph.Core.Metadata;

/// <summary>
/// The rules for the user's own tags: what a tag's name and words may be, and how often a book's pages must use the
/// words before the tag is guessed for it. A tag is found by the text already indexed, so defining one never reads a
/// file again.
/// </summary>
public static class UserTagRule
{
    /// <summary>
    /// The fewest of a book's pages that must have any of the words. One or two pages is a passing mention, a glossary
    /// line or an index entry; three is where a subject starts to be part of the book.
    /// </summary>
    public const int MinimumPages = 3;

    /// <summary>
    /// The smallest share of a book's pages with text that must have any of the words: one page in twenty. It keeps a
    /// thick rulebook that mentions zombies on a handful of its 300 pages from being tagged Undead, while a 12-page
    /// adventure needs only the <see cref="MinimumPages"/>. Pages are counted rather than words, so a single page that
    /// repeats a word many times counts once.
    /// </summary>
    public const double MinimumShare = 0.05;

    /// <summary>How many of the pages that use the words a guess keeps as its evidence, to show and open.</summary>
    public const int EvidencePages = 12;

    /// <summary>Whether <paramref name="pages"/> pages with the words, out of <paramref name="textPages"/> with text, earn the tag.</summary>
    public static bool Qualifies(int pages, int textPages) =>
        pages >= MinimumPages && textPages > 0 && pages >= MinimumShare * textPages;

    /// <summary>
    /// A tag's name as stored, or why it can't be one. It is a value of Your tags, which is typed as a list separated by
    /// commas, so a name can't hold a comma or a semicolon.
    /// </summary>
    public static (string? Name, string? Problem) ReadName(string typed)
    {
        var name = MetadataText.Tidy(typed);
        if (MetadataText.Normalize(name).Length == 0) return (null, "A tag needs a name with some letters or numbers.");
        if (name.IndexOfAny([',', ';']) >= 0) return (null, "A tag's name can't have a comma or a semicolon, which separate tags.");
        return (name, null);
    }

    /// <summary>
    /// The words and phrases that signal a tag, as typed: separated by commas, semicolons or new lines, each tidied, with
    /// repeats and entries with no letters or numbers left out.
    /// </summary>
    public static IReadOnlyList<string> ReadWords(string typed) =>
        [.. typed.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(MetadataText.Tidy)
            .Where(w => MetadataText.Normalize(w).Length > 0)
            .DistinctBy(MetadataText.Normalize)];

    /// <summary>A guess's evidence quote, as the details show it: the words it was found by.</summary>
    public static string Quote(IEnumerable<string> words) => string.Join(", ", words);
}
