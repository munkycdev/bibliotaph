using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// "Add a book I own elsewhere", in the Windows app and on the web: the dialog's words and choices, how what was
/// picked becomes a draft, and what removing the book says.
/// </summary>
public static class ElsewhereText
{
    public const string Eyebrow = "OWNED ELSEWHERE";

    public const string Heading = "Add a book you own elsewhere";

    public const string Intro = "In print or on a virtual tabletop. It gets a card with no file, so search finds it with the rest.";

    public const string OtherHint = "Somewhere else, or the book's link online: D&D Beyond https://…";

    public const string OtherTip = "Somewhere not in the list, such as another VTT, or the book's page online, which Open then opens. Separate several with commas.";

    public const string UndoRemoveFailed = "The book couldn't be brought back.";

    public static string Removed(string title) => $"Removed {title} from your library.";

    /// <summary>Game systems and editions, by label. Anything else can be typed in the details afterwards.</summary>
    public static IReadOnlyList<string> Systems(Vocabulary vocabulary) =>
        // Editions are offered beside systems, by a name that finds them again: "D&D 5e" is how most people name what they play.
        [.. Labels(vocabulary, "system").Concat(vocabulary.InVocabulary("edition").Select(e => EditionName(vocabulary, e)).OfType<string>())
            .Distinct().Order(StringComparer.CurrentCultureIgnoreCase)];

    public static IReadOnlyList<string> Types(Vocabulary vocabulary) => [.. Labels(vocabulary, "type")];

    /// <summary>The places to own a book, Print first.</summary>
    public static IReadOnlyList<Term> Owns(Vocabulary vocabulary) =>
        [.. vocabulary.InVocabulary(MetadataFields.AlsoOwn.Vocabulary!).OrderBy(t => t.Key == "print" ? 0 : 1).ThenBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>What the dialog collected: the places ticked, then what was typed for somewhere else.</summary>
    public static ElsewhereDraft Draft(string title, string? system, string? type, string? publisher, IEnumerable<string> owns, string other)
    {
        List<string> also = [.. owns];
        if (other.Trim().Length > 0) also.AddRange(MetadataValues.Split(MetadataFields.AlsoOwn, other));
        return new ElsewhereDraft(title.Trim(), system, type is { } t ? [t] : null, publisher?.Trim() is { Length: > 0 } p ? p : null, also);
    }

    static IEnumerable<string> Labels(Vocabulary vocabulary, string name) =>
        vocabulary.InVocabulary(name).Select(t => t.Label).Order(StringComparer.CurrentCultureIgnoreCase);

    static string? EditionName(Vocabulary vocabulary, Term edition)
    {
        string?[] names = [vocabulary.Find("system", edition.ParentKey ?? "") is { } system ? $"{system.Brief} {edition.Brief}" : null, edition.Label];
        return names.FirstOrDefault(n => n is not null && vocabulary.Resolve("system", n) is null && vocabulary.Resolve("edition", n)?.Key == edition.Key);
    }
}
