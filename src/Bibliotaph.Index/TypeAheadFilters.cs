using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Index;

/// <summary>A field the Library filters by typing, and what its box says it is for.</summary>
public sealed record TypeAheadFilter(MetadataField Field, string Hint);

/// <summary>
/// The Library's type-ahead filters, for fields with too many values for a menu, in the Windows app and on the web:
/// which fields have one, and the values a box offers for what was typed.
/// </summary>
public static class TypeAheadFilters
{
    /// <summary>Enough to choose from without the list running past a short window.</summary>
    public const int MaxSuggestions = 8;

    /// <summary>Under More filters, in this order.</summary>
    public static IReadOnlyList<TypeAheadFilter> All { get; } =
    [
        new(MetadataFields.Settings, "Type a setting: a world, a region, a city"),
        new(MetadataFields.Themes, "Type a theme: horror, intrigue, fey"),
        new(MetadataFields.Environments, "Type an environment: a swamp, the underdark, a city"),
        new(MetadataFields.Publisher, "Type a publisher"),
        new(MetadataFields.Edition, "Type an edition: 5e, 2nd edition"),
    ];

    /// <summary>
    /// The values not yet picked whose name, or any other name they go by, contains what was typed, ignoring case,
    /// accents and punctuation: those starting with it first, then the most books.
    /// </summary>
    public static IReadOnlyList<ValueCount> Suggest(IReadOnlyList<ValueCount> counts, IReadOnlyCollection<string> picked, string text)
    {
        var typed = MetadataText.Normalize(text);
        if (typed.Length == 0) return [];
        return [.. counts
            .Where(c => !picked.Contains(c.Value))
            .Select(c => (Count: c, Label: MetadataText.Normalize(c.Label)))
            .Where(c => c.Label.Contains(typed, StringComparison.Ordinal) || c.Count.Names.Contains(typed, StringComparison.Ordinal))
            .OrderBy(c => !c.Label.StartsWith(typed, StringComparison.Ordinal))
            .Take(MaxSuggestions)
            .Select(c => c.Count)];
    }
}
