using System.Text.RegularExpressions;

namespace Bibliotaph.Core;

/// <summary>
/// A readable title from a file name, used until metadata supplies a better one. Separators become spaces, words run
/// together in camelCase are split ("CurseOfTheVault"), copy markers and bracketed tags ("(1)", "[PDF]") go, download-site
/// suffixes, versions, dates and product codes are dropped from the ends, and the words are title-cased without touching
/// acronyms the author capitalised. Hyphens count as separators only in a name that has no spaces or underscores
/// ("the-sunless-citadel"), so "Half-Elf" survives; dots likewise, and only in a file's name.
/// </summary>
public static partial class DisplayTitle
{
    static readonly HashSet<string> NoiseWords =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "digital", "hi-res", "hires", "lo-res", "lowres", "watermarked", "opt", "optimized", "web", "print", "final", "bookmarked",
        "pdf", "epub", "mobi", "azw3", "ebook", "ocr", "hq", "lq", "compressed", "nobleed", "bleed", "printerfriendly", "singlepages",
        "spreads", "dtrpg", "drivethrurpg", "copy",
    ];

    /// <summary>Short words a title keeps lowercase except at its start or end, or after a colon or dash.</summary>
    static readonly HashSet<string> SmallWords =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into", "nor", "of", "on", "or", "the", "to", "vs", "with",
    ];

    /// <summary>Words a lowercase or shouting file name spells with the wrong case, in their usual spelling.</summary>
    static readonly Dictionary<string, string> Spellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["d&d"] = "D&D", ["ad&d"] = "AD&D", ["dnd"] = "DnD", ["osr"] = "OSR", ["ose"] = "OSE", ["dcc"] = "DCC", ["gurps"] = "GURPS",
        ["rpg"] = "RPG", ["ttrpg"] = "TTRPG", ["gm"] = "GM", ["dm"] = "DM", ["npc"] = "NPC", ["npcs"] = "NPCs", ["pc"] = "PC",
        ["pcs"] = "PCs", ["srd"] = "SRD", ["ogl"] = "OGL", ["phb"] = "PHB", ["dmg"] = "DMG", ["pbta"] = "PbtA",
        ["ii"] = "II", ["iii"] = "III", ["iv"] = "IV", ["vi"] = "VI", ["vii"] = "VII", ["viii"] = "VIII", ["ix"] = "IX",
        ["xi"] = "XI", ["xii"] = "XII",
    };

    [GeneratedRegex(@"[_\s]+")]
    private static partial Regex Separators();

    /// <summary>Two-word download-site suffixes, joined into one noise word before the name is split.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(hi|high|lo|low)[\s_\-]?res(?:olution)?|(printer|print)[\s_\-]?friendly|no[\s_\-]?bleed|single[\s_\-]?pages?)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoisePhrase();

    /// <summary>Windows' copy markers: "Copy of X", "X - Copy", "X - Copy (2)" and a download's "X (1)".</summary>
    [GeneratedRegex(@"^copy[\s_]of[\s_]+|[\s_]*-[\s_]*copy(?:[\s_]*\(\d+\))?$|[\s_]*\(\d{1,2}\)", RegexOptions.IgnoreCase)]
    private static partial Regex CopyMarker();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\(([^()]*)\)")]
    private static partial Regex Parenthesised();

    /// <summary>A dot between words, not one inside a number such as 3.5.</summary>
    [GeneratedRegex(@"(?<!\d)\.|\.(?!\d)")]
    private static partial Regex WordDot();

    /// <summary>
    /// A version ("v2", "v1.2", "rev3", "1.2.3"), a date ("20240115", "2024-01-15") or a long number. A year stays, since
    /// it may tell editions apart, and so does an edition such as 3.5.
    /// </summary>
    [GeneratedRegex(@"^(?:(?:v|ver|rev|r)\.?\d+(?:[.\-]\d+)*[a-z]?|\d+\.\d+\.\d+|\d{4}[.\-]\d{1,2}[.\-]\d{1,2}|\d{1,2}[.\-]\d{1,2}[.\-]\d{2,4}|\d{5,}|(?!19|20)\d{4})$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VersionOrDate();

    /// <summary>A publisher's product code: letters then three or more digits ("CHA23150", "WOTC-786950").</summary>
    [GeneratedRegex(@"^\p{L}{2,6}-?\d{3,}\p{L}?$")]
    private static partial Regex ProductCode();

    /// <summary>A word run together in camelCase: a lowercase letter then a capital, or capitals then a capitalised word.</summary>
    [GeneratedRegex(@"\p{Ll}\p{Lu}|\p{Lu}\p{Lu}\p{Ll}|\d\p{Lu}\p{Ll}")]
    private static partial Regex CamelJoin();

    /// <summary>
    /// The parts of a camelCase word, in order of preference: names spelled in mixed case, editions ("5e", "3.5e",
    /// "PF2e"), ordinals, versions, an acronym before a capitalised word ("DM" in "DMGuide"), "Mc"/"Mac" names, words,
    /// runs of capitals, numbers and anything else one character at a time.
    /// </summary>
    [GeneratedRegex(@"AD&D|D&D|DnD|PbtA|\p{Lu}{0,4}\d+(?:\.\d+)?[eE](?!\p{Ll})|\d+(?:st|nd|rd|th)(?!\p{Ll})|[vV]\d+(?:\.\d+)*(?!\p{Ll})|\p{Lu}+(?=\p{Lu}\p{Ll})|Ma?c\p{Lu}\p{Ll}+|\p{Lu}?\p{Ll}+(?:'\p{Ll}+)?|\p{Lu}+|\d+|.")]
    private static partial Regex CamelPart();

    public static string FromFileName(string fileName) => FromStem(Path.GetFileNameWithoutExtension(fileName), dotsSeparate: true);

    /// <summary>A pack's title from its folder's name, which has no extension to drop ("Tokens.v2" keeps its dot).</summary>
    public static string FromFolderName(string folderName) => FromStem(folderName, dotsSeparate: false);

    static string FromStem(string stem, bool dotsSeparate)
    {
        var name = CopyMarker().Replace(stem, "");
        name = Bracketed().Replace(name, " ");
        name = NoisePhrase().Replace(name, m => m.Value.Contains("res", StringComparison.OrdinalIgnoreCase) ? "hires"
            : m.Value.Contains("friendly", StringComparison.OrdinalIgnoreCase) ? "printerfriendly"
            : m.Value.Contains("bleed", StringComparison.OrdinalIgnoreCase) ? "nobleed" : "singlepages");
        name = Parenthesised().Replace(name, m => Separators().Split(m.Groups[1].Value).All(w => w.Length == 0 || IsNoise(w)) ? " " : m.Value);

        var spaced = name.Contains(' ', StringComparison.Ordinal);
        var separated = Separators().IsMatch(name.Trim());
        if (dotsSeparate && !separated) name = WordDot().Replace(name, " ");
        separated = Separators().IsMatch(name.Trim());
        List<string> words = [.. (separated ? Separators().Split(name) : name.Split('-')).Where(w => w.Length > 0)];

        Trim(words, codes: true);
        // A name with spaces was typed by a person, who meant its capitals; one without may have run its words together.
        if (!spaced) words = [.. words.SelectMany(SplitCamel)];
        Trim(words, codes: false);

        if (words.Count == 0 || !words.Any(w => w.Any(char.IsLetterOrDigit))) return stem;
        return TitleCase(words);
    }

    static bool IsNoise(string word) => NoiseWords.Contains(word) || VersionOrDate().IsMatch(word);

    static bool IsPunctuation(string word) => !word.Any(char.IsLetterOrDigit);

    /// <summary>
    /// Drops noise from the end, and stray punctuation from both ends, so a title that starts with a number keeps it.
    /// With <paramref name="codes"/>, a product code at either end goes too, and a long number at the start.
    /// </summary>
    static void Trim(List<string> words, bool codes)
    {
        while (words.Count > 1)
        {
            var last = words[^1];
            if (IsNoise(last) || IsPunctuation(last) || (codes && ProductCode().IsMatch(last))) words.RemoveAt(words.Count - 1);
            else if (IsPunctuation(words[0]) || (codes && (ProductCode().IsMatch(words[0]) || (words[0].Length >= 5 && words[0].All(char.IsDigit)))))
                words.RemoveAt(0);
            else break;
        }
    }

    static IEnumerable<string> SplitCamel(string word)
    {
        if (!CamelJoin().IsMatch(word))
        {
            yield return word;
            yield break;
        }
        string? pending = null;
        foreach (var part in CamelPart().Matches(word).Select(m => m.Value))
        {
            // Punctuation stays with the word before it ("Vault:" in "Vault:TheReturn"), except "&" and "+", which stand alone.
            if (IsPunctuation(part) && part is not ("&" or "+") && pending is not null)
            {
                pending += part;
                continue;
            }
            if (pending is not null) yield return pending;
            pending = part;
        }
        if (pending is not null) yield return pending;
    }

    static string TitleCase(List<string> words)
    {
        var letters = words.SelectMany(w => w).Where(char.IsLetter).ToList();
        var shouting = letters.Count > 3 && letters.All(char.IsUpper);
        var result = new string[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var opens = i == 0 || i == words.Count - 1 || words[i - 1] is "-" or "\u2013" or "\u2014" || words[i - 1].EndsWith(':');
            result[i] = Spellings.TryGetValue(word, out var spelled) && (shouting || !word.Any(char.IsUpper)) ? spelled
                : SmallWords.Contains(word) && !opens && (shouting || !word.Skip(1).Any(char.IsUpper)) ? word.ToLowerInvariant()
                : shouting && word.All(c => !char.IsLetter(c) || char.IsUpper(c)) && word.Any(char.IsLetter) && word.All(c => char.IsLetter(c) || c is '\'' or '-')
                    ? Capitalise(word.ToLowerInvariant())
                : word.Any(char.IsUpper) ? word
                : Capitalise(word);
        }
        return string.Join(' ', result);
    }

    /// <summary>The first letter of each hyphenated part in capitals, the rest as written: "half-elf" is "Half-Elf".</summary>
    static string Capitalise(string word) =>
        string.Join('-', word.Split('-').Select(part => part.Length == 0 || !char.IsLetter(part[0]) ? part : char.ToUpperInvariant(part[0]) + part[1..]));
}
