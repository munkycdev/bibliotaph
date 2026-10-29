using System.Globalization;

namespace Bibliotaph.Core.Metadata;

/// <summary>A term as the overlap check sees it: its key, every name it answers to, and whether it came with Bibliotaph.</summary>
public sealed record OverlapTerm(string Key, string Label, string? ShortLabel, IReadOnlyList<string> OtherNames, bool Starter)
{
    /// <summary>The label, short name and other names, in that order.</summary>
    public IEnumerable<string> Names => OtherNames.Prepend(ShortLabel).Prepend(Label).OfType<string>();
}

/// <summary>Terms that look like the same thing, with why, and whether the names alone make it likely.</summary>
public sealed record TermOverlap(IReadOnlyList<string> Keys, string Reason, bool Sure);

/// <summary>
/// Finds the terms of one vocabulary that look like the same thing by their names alone: the same name but for small
/// words, one name starting or ending the other, a near spelling, or initials. Two starter terms are never paired,
/// since that list is curated; the user's own and the AI's terms are where the doubles grow.
/// </summary>
public static class TermOverlaps
{
    static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal) { "the", "a", "an", "of" };

    /// <summary>
    /// A name in comparison form: lower case without accents, a bracketed tail dropped, "&amp;" read as "and", small
    /// words dropped, plural endings dropped: "The Forgotten Realms (5e)" and "Forgotten Realm" both fold to
    /// "forgotten realm".
    /// </summary>
    public static string Fold(string name) => string.Join(' ', FoldWords(name).Where(w => !SmallWords.Contains(w)));

    /// <summary>The words of a name in comparison form, small words kept, for initials: "Wizards of the Coast" gives "wotc".</summary>
    static List<string> FoldWords(string name)
    {
        var cut = name.IndexOfAny(['(', '[']);
        if (cut > 0) name = name[..cut];
        return [.. MetadataText.Words(MetadataText.Normalize(name).Replace("&", " and ", StringComparison.Ordinal).Replace("+", " plus ", StringComparison.Ordinal))
            .Select(Singular)];
    }

    static string Singular(string word)
    {
        if (word.Length <= 3 || word.Any(char.IsDigit) || word.EndsWith("ss", StringComparison.Ordinal)) return word;
        if (word.EndsWith("ies", StringComparison.Ordinal)) return word[..^3] + "y";
        if (word.EndsWith("ses", StringComparison.Ordinal) || word.EndsWith("xes", StringComparison.Ordinal)
            || word.EndsWith("ches", StringComparison.Ordinal) || word.EndsWith("shes", StringComparison.Ordinal)) return word[..^2];
        return word.EndsWith('s') ? word[..^1] : word;
    }

    /// <summary>
    /// The groups of terms that look like the same thing, each with the first reason found. Pairs in
    /// <paramref name="keptApart"/> (keys, either order) are never paired, nor two starter terms.
    /// </summary>
    public static IReadOnlyList<TermOverlap> Find(IReadOnlyList<OverlapTerm> terms, IReadOnlyCollection<(string First, string Second)>? keptApart = null)
    {
        var apart = new HashSet<(string, string)>(keptApart ?? []);
        var folded = terms.Select(t => (Term: t, Names: t.Names.Select(n => (Folded: Fold(n), Initials: string.Concat(FoldWords(n).Select(w => w[0]))))
            .Where(n => n.Folded.Length > 0).DistinctBy(n => n.Folded, StringComparer.Ordinal).ToList())).ToList();
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        var reasons = new Dictionary<int, (string Reason, bool Sure)>();
        var next = 0;
        for (var i = 0; i < folded.Count; i++)
            for (var j = i + 1; j < folded.Count; j++)
            {
                var (a, b) = (folded[i], folded[j]);
                if (a.Term.Starter && b.Term.Starter) continue;
                if (apart.Contains((a.Term.Key, b.Term.Key)) || apart.Contains((b.Term.Key, a.Term.Key))) continue;
                if (Compare(a.Names, b.Names) is not { } found) continue;
                var ga = groups.TryGetValue(a.Term.Key, out var x) ? x : -1;
                var gb = groups.TryGetValue(b.Term.Key, out var y) ? y : -1;
                int group;
                if (ga < 0 && gb < 0) reasons[group = next++] = found;
                else if (ga < 0) group = gb;
                else if (gb < 0 || ga == gb) group = ga;
                else
                {
                    // Two groups meet: the second joins the first, with the surer reason.
                    group = ga;
                    foreach (var key in groups.Where(g => g.Value == gb).Select(g => g.Key).ToList()) groups[key] = ga;
                    if (reasons[gb].Sure && !reasons[ga].Sure) reasons[ga] = reasons[gb];
                    reasons.Remove(gb);
                }
                groups[a.Term.Key] = group;
                groups[b.Term.Key] = group;
                if (found.Sure && !reasons[group].Sure) reasons[group] = found;
            }
        return [.. groups.GroupBy(g => g.Value).OrderBy(g => g.Key)
            .Select(g => new TermOverlap([.. terms.Select(t => t.Key).Where(k => g.Any(m => m.Key == k))], reasons[g.Key].Reason, reasons[g.Key].Sure))];
    }

    static (string Reason, bool Sure)? Compare(List<(string Folded, string Initials)> a, List<(string Folded, string Initials)> b)
    {
        (string, bool)? maybe = null;
        foreach (var (x, xInitials) in a)
            foreach (var (y, yInitials) in b)
            {
                if (x == y) return ("The same name but for small words, brackets or a plural", true);
                if (!x.Any(char.IsDigit) && !y.Any(char.IsDigit) && Distance(x, y) <= (Math.Min(x.Length, y.Length) <= 8 ? 1 : 2))
                    return ("Spelt almost the same", true);
                maybe ??= StartsOrEnds(x, y) ? ("One name starts or ends the other", false)
                    : IsInitials(x, yInitials) || IsInitials(y, xInitials) ? ("One name is the other's initials", false) : null;
            }
        return maybe;
    }

    static bool StartsOrEnds(string x, string y)
    {
        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        if (shorter == longer || (!shorter.Contains(' ') && shorter.Length < 6)) return false;
        return longer.StartsWith(shorter + " ", StringComparison.Ordinal) || longer.EndsWith(" " + shorter, StringComparison.Ordinal);
    }

    /// <summary>Whether a one-word name is the initials of a name of several words.</summary>
    static bool IsInitials(string letters, string initials) =>
        !letters.Contains(' ') && letters.Length is >= 2 and <= 5 && initials.Length >= 2 && letters == initials;

    /// <summary>Damerau-Levenshtein distance, with a transposition as one edit.</summary>
    static int Distance(string s, string t)
    {
        var d = new int[s.Length + 1, t.Length + 1];
        for (var i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= t.Length; j++) d[0, j] = j;
        for (var i = 1; i <= s.Length; i++)
            for (var j = 1; j <= t.Length; j++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && s[i - 1] == t[j - 2] && s[i - 2] == t[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[s.Length, t.Length];
    }

    /// <summary>"3 books" or "1 book", for a term's count.</summary>
    public static string Books(int count) => count == 1 ? "1 book" : count.ToString("N0", CultureInfo.CurrentCulture) + " books";
}
