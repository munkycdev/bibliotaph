namespace Bibliotaph.Core.Metadata;

/// <summary>
/// What a companion file (a map, a handout, a character sheet, a pack of images) takes from the book it goes with. It
/// keeps its own title, type and the fields that describe a whole book's contents; the book's game, publisher, line,
/// setting, credits, levels and the user's tags reach it as suggestions of origin <see cref="AssertionOrigin.SourceBook"/>,
/// ranked above lookups and the AI. A single-value field the companion has a confirmed value of its own in keeps that
/// value; a multi-value field adds the book's values to the companion's own.
/// </summary>
public static class SourceBookMetadata
{
    /// <summary>The fields a companion takes from its book.</summary>
    public static IReadOnlyList<MetadataField> Inherited { get; } =
    [
        MetadataFields.System, MetadataFields.Edition, MetadataFields.Publisher, MetadataFields.Series, MetadataFields.Year,
        MetadataFields.Settings, MetadataFields.Authors, MetadataFields.Levels, MetadataFields.Players, MetadataFields.Tags,
    ];

    /// <summary>
    /// The companion's own claims, plus the book's values for each inherited field. The book's values come as claims
    /// with negative ids below <paramref name="firstId"/>, quoting the book's title. They are confirmed only when the user
    /// confirmed the link and the book's value is confirmed; otherwise they are suggestions, dated when the link was
    /// made or the book's value arrived, whichever is later, so the companion's newer decisions still win.
    /// </summary>
    public static IReadOnlyList<MetadataClaim> Inherit(IReadOnlyList<MetadataClaim> own, EffectiveMetadata book, string bookTitle, bool linkConfirmed,
        DateTime linkedUtc, long firstId = -1)
    {
        var claims = new List<MetadataClaim>(own);
        var id = firstId;
        foreach (var field in Inherited)
        {
            if (!field.Multiple && own.Any(c => c.Field == field.Key && c.State == AssertionState.Confirmed)) continue;
            foreach (var value in book[field].Values)
            {
                var confirmed = linkConfirmed && value.Confirmed;
                var created = value.Source.CreatedUtc > linkedUtc ? value.Source.CreatedUtc : linkedUtc;
                claims.Add(new MetadataClaim(id--, field.Key, value.Value, value.Normalized, AssertionOrigin.SourceBook,
                    confirmed ? AssertionState.Confirmed : AssertionState.Provisional, created, bookTitle,
                    DecidedUtc: confirmed ? linkedUtc : null));
            }
        }
        return claims;
    }

    /// <summary>A claim a companion shows only because the book it goes with has the value.</summary>
    public static bool IsInherited(MetadataClaim claim) => claim.Origin == AssertionOrigin.SourceBook;
}
