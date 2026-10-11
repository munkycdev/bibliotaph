namespace Bibliotaph.Core.Metadata;

/// <summary>
/// What an adventure inside a file takes from the file (slice 5 plan, choice 8). A part has its own title, types,
/// levels, themes, environments and authors. Its game system, edition, publisher, series, year and setting come from
/// the file: a value confirmed on the file is the part's too, and one only suggested for the file is suggested for the
/// part, unless the part has a confirmed value of its own in that field.
/// </summary>
public static class PartMetadata
{
    /// <summary>The fields a part takes from its file.</summary>
    public static IReadOnlyList<MetadataField> Inherited { get; } =
        [MetadataFields.System, MetadataFields.Edition, MetadataFields.Publisher, MetadataFields.Series, MetadataFields.Year, MetadataFields.Settings];

    /// <summary>The quote on a value a part shows because its file has it.</summary>
    public const string FromFile = "From the file it is in";

    /// <summary>
    /// The part's own claims, plus the file's values for each inherited field the part hasn't settled itself. The
    /// file's values come as claims with negative ids, dated at the start of time so any value of the part's own wins
    /// a tie, and confirmed when the file's value is.
    /// </summary>
    public static IReadOnlyList<MetadataClaim> Inherit(IReadOnlyList<MetadataClaim> own, EffectiveMetadata file)
    {
        var claims = new List<MetadataClaim>(own);
        long id = -1;
        foreach (var field in Inherited)
        {
            if (own.Any(c => c.Field == field.Key && c.State == AssertionState.Confirmed)) continue;
            foreach (var value in file[field].Values)
                claims.Add(new MetadataClaim(id--, field.Key, value.Value, value.Normalized, value.Origin,
                    value.Confirmed ? AssertionState.Confirmed : AssertionState.Provisional, DateTime.MinValue, FromFile,
                    DecidedUtc: value.Confirmed ? DateTime.MinValue : null));
        }
        return claims;
    }

    /// <summary>A claim a part shows only because its file has the value.</summary>
    public static bool IsInherited(MetadataClaim claim) => claim.Id < 0;
}
