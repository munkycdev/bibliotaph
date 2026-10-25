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
            AssertionOrigin.UserTag => "Your words for this tag are on its pages" + quote
                + (pages && claim.Pages.Count > 0 ? $", page {string.Join(", ", claim.Pages.Select(p => p + 1))}" : ""),
            _ => claim.Origin.ToString(),
        };
        return claim.Origin != AssertionOrigin.User && claim.State == AssertionState.Confirmed ? $"You kept this. {source}" : source;
    }
}
