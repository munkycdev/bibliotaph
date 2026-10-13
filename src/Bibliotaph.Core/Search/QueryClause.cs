using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Search;

/// <summary>
/// Writes one piece of a search in the search box's own syntax, so something built outside the box (Ask's chips) runs
/// as ordinary query text the user can see and change: <c>system:Mothership</c>, <c>style:"dungeon crawl"</c>,
/// <c>-theme:horror</c>, <c>terraform</c>, <c>"chase through a city"</c>.
/// </summary>
public static class QueryClause
{
    /// <summary>
    /// The clause for <paramref name="value"/> in <paramref name="field"/>, or as words to find when the field is null;
    /// with <paramref name="exclude"/>, the clause that leaves it out. Null when the value has nothing to search for.
    /// Several words become a phrase, and a value that would read as syntax (OR, a stray colon or star) is quoted.
    /// </summary>
    public static string? Format(SearchFieldInfo? field, string? value, bool exclude = false)
    {
        var text = MetadataText.Tidy(value ?? "").Replace("\"", "", StringComparison.Ordinal).Replace("“", "", StringComparison.Ordinal)
            .Replace("”", "", StringComparison.Ordinal);
        if (!text.Any(char.IsLetterOrDigit)) return null;
        var quoted = text == "OR" ? $"\"{text}\"" : SearchGuide.Quote(text);
        var clause = field is null ? quoted : field.Name + ":" + quoted;
        return exclude ? "-" + clause : clause;
    }

    /// <summary>Clauses joined into one query, leaving out those with nothing to search for.</summary>
    public static string Join(IEnumerable<string?> clauses) => string.Join(' ', clauses.Where(c => !string.IsNullOrEmpty(c)));
}
