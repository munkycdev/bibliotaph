using System.Text;

namespace Bibliotaph.Core.Metadata;

/// <summary>
/// One publisher under one name. Two names are the same publisher when they match with legal suffixes ("Inc",
/// "Corporation", "LLC", "GmbH"), a leading "The", punctuation and case set aside: "Brass Lantern Press" and "Brass
/// Lantern Press, Inc.". The library files them under the form most of its books use, and on a tie the shorter.
/// </summary>
public static class PublisherNames
{
    /// <summary>Words that say what kind of company it is, not which, as compared (lower case, no dots).</summary>
    static readonly HashSet<string> Suffixes = new(StringComparer.Ordinal)
    {
        "inc", "incorporated", "corp", "corporation", "co", "company", "llc", "ltd", "limited", "gmbh", "sa",
    };

    /// <summary>
    /// What two names of one publisher share: "brass lantern press" for "Brass Lantern Press Corporation" and "The
    /// Brass Lantern Press, Inc.". Empty for a name that is nothing but punctuation.
    /// </summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        // "L.L.C." and "S.A." are one word each once their dots go; an ampersand is a word of its own.
        var text = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormKC))
            text.Append(c switch
            {
                '.' or '\'' or '’' => "",
                '&' => " & ",
                _ when char.IsLetterOrDigit(c) => char.ToLowerInvariant(c).ToString(),
                _ => " ",
            });
        var words = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the") words.RemoveAt(0);
        while (words.Count > 1 && Suffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>Whether two names are of one publisher.</summary>
    public static bool Same(string? a, string? b) => Key(a) is { Length: > 0 } key && key == Key(b);

    /// <summary>
    /// The name to file a publisher under, of the names its books use (each with how many books): the most used, and
    /// on a tie the shorter, then the first in order.
    /// </summary>
    public static string Choose(IEnumerable<(string Name, long Books)> names) =>
        names.OrderByDescending(n => n.Books).ThenBy(n => n.Name.Length).ThenBy(n => n.Name, StringComparer.Ordinal).First().Name;

    /// <summary>
    /// Each name in <paramref name="names"/> (with how many books use it) mapped to the name its publisher is filed
    /// under, and that publisher's books in all.
    /// </summary>
    public static IReadOnlyDictionary<string, (string Name, long Books)> Group(IEnumerable<(string Name, long Books)> names)
    {
        var map = new Dictionary<string, (string Name, long Books)>(StringComparer.Ordinal);
        foreach (var group in names.Where(n => Key(n.Name).Length > 0).GroupBy(n => Key(n.Name)))
        {
            var chosen = (Choose(group), group.Sum(n => n.Books));
            foreach (var name in group) map[name.Name] = chosen;
        }
        return map;
    }

    /// <summary>
    /// The form of <paramref name="name"/> the library already files its publisher under, from <paramref name="known"/>
    /// (each with how many books use it), or null when the library has no other form of it.
    /// </summary>
    public static string? Existing(string name, IEnumerable<(string Name, long Books)> known)
    {
        var key = Key(name);
        if (key.Length == 0) return null;
        var same = known.Where(k => Key(k.Name) == key).ToList();
        if (same.Count == 0) return null;
        var chosen = Choose(same);
        return string.Equals(chosen, name, StringComparison.Ordinal) ? null : chosen;
    }

}
