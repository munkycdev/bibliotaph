using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.App.Tests;

/// <summary>Made-up vocabulary and field values, as the catalog would compute them.</summary>
static class Values
{
    public static readonly Vocabulary Terms = new(
    [
        new Term("system", "dnd5e", "D&D 5e"),
        new Term("system", "pf2e", "Pathfinder 2e"),
        new Term("type", "map", "Map"),
        new Term("type", "adventure", "Adventure"),
    ]);

    /// <summary>A value of a field, suggested by a folder name or set by the user.</summary>
    public static EffectiveValue Value(MetadataField field, string value, bool confirmed = false, AssertionOrigin origin = AssertionOrigin.Folder)
    {
        var normalized = MetadataValues.Normalize(field, value);
        var claim = new MetadataClaim(1, field.Key, value, normalized, confirmed ? AssertionOrigin.User : origin,
            confirmed ? AssertionState.Confirmed : AssertionState.Provisional, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        return new EffectiveValue(value, normalized, confirmed, [claim]);
    }

    /// <summary>One book's field holding <paramref name="values"/>.</summary>
    public static EffectiveField Field(MetadataField field, params EffectiveValue[] values) => new(field, values, []);
}
