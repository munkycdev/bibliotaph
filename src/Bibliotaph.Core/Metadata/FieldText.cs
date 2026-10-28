using System.Globalization;

namespace Bibliotaph.Core.Metadata;

/// <summary>
/// How a metadata value and its sources read in a book's details, kept here so the Windows app and the web client
/// describe them in the same words.
/// </summary>
public static class FieldText
{
    /// <summary>As it reads: a term's label, "Levels 1–5", "4–6 players", a store's name, the text itself.</summary>
    public static string Display(MetadataField field, string value, Vocabulary vocabulary) => field.Kind switch
    {
        FieldKind.Term => vocabulary.Label(field.Vocabulary!, value),
        FieldKind.Levels when LevelRange.TryParse(value, out var range) => range.Describe(),
        FieldKind.Players when PlayerRange.TryParse(value, out var players) => players.Describe(),
        FieldKind.Store => vocabulary.Label(field.Vocabulary!, StoreLink.Parse(value).Store),
        _ => value,
    };

    /// <summary>As a value reads where its link has to show too, as on a bulk edit chip: "DriveThruRPG · drivethrurpg.com/…".</summary>
    public static string DisplayWithLink(MetadataField field, string value, Vocabulary vocabulary) =>
        field.Kind == FieldKind.Store && StoreLink.Parse(value) is { ShortUrl: { } link } ? $"{Display(field, value, vocabulary)} · {link}" : Display(field, value, vocabulary);

    /// <summary>A field's values as they read, separated by commas, or "Not set".</summary>
    public static string Join(IReadOnlyCollection<string> texts) => texts.Count > 0 ? string.Join(", ", texts) : "Not set";

    /// <summary>The link that opens a field's evidence: "Sources disagree", "Suggested" while nobody has confirmed it, or "Sources".</summary>
    public static string EvidenceLabel(bool needsReview, bool suggested) => needsReview ? "Sources disagree" : suggested ? "Suggested" : "Sources";

    /// <summary>How to type Get it from, wherever it is edited.</summary>
    public const string StoreHint = "A store, a link, or both: DriveThruRPG https://… Separate stores with commas";

    /// <summary>How to type Also own, wherever it is edited: a place, and the book's page there when it is online.</summary>
    public const string OwnHint = "Where, a link to the book there, or both: D&D Beyond https://… Separate them with commas";

    /// <summary>How to type a linked field (<see cref="FieldKind.Store"/>).</summary>
    public static string LinkHint(MetadataField field) => field == MetadataFields.AlsoOwn ? OwnHint : StoreHint;

    /// <summary>The line under a field's editor: how to type it, or nothing for plain text.</summary>
    public static string EditHint(MetadataField field) => field.Kind switch
    {
        FieldKind.Levels => "3, 1-5 or n/a",
        FieldKind.Players => "4 or 3-5",
        FieldKind.Year => "2019",
        FieldKind.Store => LinkHint(field),
        _ when field.Multiple => "Separate values with commas",
        _ => "",
    };

    /// <summary>What a new term of the field is called: "a new store", "a new place", "a new setting".</summary>
    public static string TermNoun(MetadataField field) =>
        field == MetadataFields.AlsoOwn ? "place" : field.Kind == FieldKind.Store ? field.Vocabulary! : field.Label.ToLower(CultureInfo.CurrentCulture);

    /// <summary>The list under a term field's editor: picking adds to a list, or replaces a single value.</summary>
    public static string ChoicesLabel(MetadataField field) => field.Multiple ? "Add from the list" : "Or pick from the list";

    /// <summary>The labels of a term field's vocabulary, to pick from; empty for other fields.</summary>
    public static IReadOnlyList<string> Choices(MetadataField field, Vocabulary vocabulary) =>
        field.Kind is FieldKind.Term or FieldKind.Store
            ? [.. vocabulary.InVocabulary(field.Vocabulary!).Select(t => t.Label).Order(StringComparer.CurrentCultureIgnoreCase)]
            : [];

    /// <summary>
    /// For a term field, how what is typed will be filed: "“Campaign” is another name for Adventure", or "“Heists” will
    /// be added as a new type". Empty for other fields, or when every value is typed as its own label.
    /// </summary>
    public static string Resolution(MetadataField field, string? typed, Vocabulary vocabulary)
    {
        if (field.Kind is not (FieldKind.Term or FieldKind.Store)) return "";
        var notes = new List<string>();
        foreach (var typedPart in MetadataValues.Split(field, typed ?? ""))
        {
            if (MetadataText.Normalize(typedPart).Length == 0) continue;
            var part = typedPart;
            Term? term;
            if (field.Kind == FieldKind.Store)
            {
                (term, part, var url) = StoreLink.Read(typedPart, vocabulary, field);
                if (term is not null && url is not null && MetadataText.Normalize(StoreLink.Split(typedPart).Name).Length == 0)
                {
                    notes.Add($"The link goes to {term.Label}.");
                    continue;
                }
            }
            else term = vocabulary.Resolve(field.Vocabulary!, part);
            var kind = TermNoun(field);
            if (term is null) notes.Add($"“{part}” will be added as a new {kind}.");
            else if (MetadataText.Normalize(term.Label) != MetadataText.Normalize(part)) notes.Add($"“{part}” is another name for {term.Label}.");
        }
        return string.Join(" ", notes);
    }

    /// <summary>Where one claim came from, in words: "From the folder name “D&amp;D 5e”", with its pages unless told not to.</summary>
    public static string Source(MetadataClaim claim, bool pages = true)
    {
        var quote = string.IsNullOrWhiteSpace(claim.Quote) ? "" : $" “{claim.Quote}”";
        var source = claim.Origin switch
        {
            AssertionOrigin.User => "You set this",
            AssertionOrigin.Folder => "From the folder name" + quote,
            AssertionOrigin.Filename => "From the file name" + quote,
            AssertionOrigin.Embedded => "From the PDF's own information" + quote,
            AssertionOrigin.Rule => (claim.Field == MetadataFields.GetItFrom.Key ? "Named in the file's watermark" : "Written in the file's name or title") + quote,
            AssertionOrigin.FolderRule => string.IsNullOrWhiteSpace(claim.Quote) ? "From your rule for this library folder" : $"From your rule for the folder “{claim.Quote}”",
            AssertionOrigin.Ai => "Suggested by the AI model" + quote + (pages && claim.Pages.Count > 0 ? $", page {string.Join(", ", claim.Pages.Select(p => p + 1))}" : ""),
            AssertionOrigin.Lookup => "Found online" + (string.IsNullOrWhiteSpace(claim.Quote) ? "" : $", in {claim.Quote}"),
            AssertionOrigin.UserTag => "Your words for this tag are on its pages" + quote
                + (pages && claim.Pages.Count > 0 ? $", page {string.Join(", ", claim.Pages.Select(p => p + 1))}" : ""),
            _ => claim.Origin.ToString(),
        };
        return claim.Origin != AssertionOrigin.User && claim.State == AssertionState.Confirmed ? $"You kept this. {source}" : source;
    }
}
