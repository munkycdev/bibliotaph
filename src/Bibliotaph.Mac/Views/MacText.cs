using System.Globalization;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Words the Mac shows differently from Windows and the web, which share the view models' text: headings, buttons and
/// menu items take title-style capitalization here, as Apple's guidelines ask.
/// </summary>
static class MacText
{
    /// <summary>Articles, conjunctions and short prepositions, which stay lower case inside a title.</summary>
    static readonly HashSet<string> SmallWords =
        ["a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into", "nor", "of", "on", "onto", "or", "per", "the", "to", "via", "with"];

    /// <summary>
    /// "Not from these books" as "Not from These Books", "Recently added" as "Recently Added". Only plain lower-case
    /// words change: a word with a capital, a digit or a dot (a name, "AI", "v2.pdf") is left as it is.
    /// </summary>
    public static string TitleCase(string text)
    {
        if (text.Length == 0) return text;
        var words = text.Split(' ');
        var last = Array.FindLastIndex(words, w => w.Length > 0);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length == 0 || !Plain(word)) continue;
            var small = SmallWords.Contains(Bare(word));
            // The first and last words are always capitalized, as is a word after a colon.
            var edge = i == 0 || i == last || (i > 0 && words[i - 1].EndsWith(':'));
            if (small && !edge) continue;
            words[i] = string.Join('-', word.Split('-').Select(Capitalize));
        }
        return string.Join(' ', words);
    }

    /// <summary>"ADVENTURES IN THIS FILE (3)" as "Adventures in This File (3)": a heading set in capitals on Windows.</summary>
    public static string FromCapitals(string heading) =>
        heading.Any(char.IsLower) ? heading : TitleCase(heading.ToLower(CultureInfo.CurrentCulture));

    // Lower-case letters, with an apostrophe or hyphen inside and punctuation at either end.
    static bool Plain(string word)
    {
        var bare = Bare(word);
        return bare.Length > 0 && bare.All(c => char.IsLower(c) || c is '\'' or '’' or '-');
    }

    static string Bare(string word) => word.Trim('(', ')', '“', '”', '"', ',', '.', '…', '?', '!', ':', ';');

    static string Capitalize(string part)
    {
        var at = 0;
        while (at < part.Length && !char.IsLetter(part[at])) at++;
        return at < part.Length && char.IsLower(part[at])
            ? string.Concat(part.AsSpan(0, at), char.ToUpper(part[at], CultureInfo.CurrentCulture).ToString(), part.AsSpan(at + 1))
            : part;
    }
}
