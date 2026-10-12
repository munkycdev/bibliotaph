using System.Globalization;
using System.Text;

namespace Bibliotaph.Core.Metadata;

/// <summary>
/// The comparison form of a value: case, accents, quote styles and punctuation runs don't count, so "Dungeons &amp;
/// Dragons", "dungeons &amp; dragons" and "Dungeons  &amp;  Dragons!" are the same value. Rejections, aliases and
/// duplicate checks all compare in this form. Ampersands and plus signs stay, since "D&amp;D" and "C++" mean something.
/// </summary>
public static class MetadataText
{
    public static string Normalize(string text)
    {
        var decomposed = UnicodeForm(text, NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var space = false;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c) || c is '&' or '+')
            {
                if (space && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else space = true;
        }
        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> in a Unicode normalization form. Text read from a PDF can hold half of a surrogate pair or
    /// U+FFFE, which <see cref="string.Normalize(NormalizationForm)"/> refuses with an exception, so each one becomes
    /// U+FFFD first: it is neither a letter nor a digit, so it drops out of the comparison forms like any other symbol.
    /// </summary>
    public static string UnicodeForm(string text, NormalizationForm form)
    {
        var refused = Refused(text, 0);
        if (refused < 0) return text.Normalize(form);
        var repaired = text.ToCharArray();
        for (; refused >= 0; refused = Refused(text, refused + 1)) repaired[refused] = '\uFFFD';
        return new string(repaired).Normalize(form);
    }

    /// <summary>
    /// The index of the first character at or after <paramref name="start"/> that normalizing refuses, or -1: an unpaired
    /// surrogate, or U+FFFE, a noncharacter .NET rejects though Unicode allows it.
    /// </summary>
    static int Refused(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\uFFFE') return i;
            if (!char.IsSurrogate(text[i])) continue;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else return i;
        }
        return -1;
    }

    /// <summary>The words of a normalised text, for phrase matching.</summary>
    public static string[] Words(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Trims and collapses inner whitespace, for storing a value the way it reads.</summary>
    public static string Tidy(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// A level range in a document's own game system, as stored: "3", "1-5", or "n/a" when levels don't apply (a map,
/// a system without levels). Unknown is the absence of a value, never a stored one.
/// </summary>
public readonly record struct LevelRange(int Min, int Max, bool NotApplicable = false)
{
    public const int Highest = 30;

    public static LevelRange None => new(0, 0, true);

    /// <summary>"3", "1-5", "1–5", "levels 1 to 5", "n/a", "not applicable", "none".</summary>
    public static bool TryParse(string? text, out LevelRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = MetadataText.Normalize(text);
        if (normalized is "n a" or "na" or "not applicable" or "none" or "no levels")
        {
            range = None;
            return true;
        }
        var words = MetadataText.Words(normalized).Where(w => w is not ("level" or "levels" or "lvl" or "to")).ToArray();
        if (words.Length is 0 or > 2) return false;
        if (!int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out var min)) return false;
        var max = min;
        if (words.Length == 2 && !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out max)) return false;
        if (min < 0 || max > Highest || min > max) return false;
        range = new LevelRange(min, max);
        return true;
    }

    public bool Contains(int level) => !NotApplicable && Min <= level && level <= Max;

    public bool Overlaps(int min, int max) => !NotApplicable && Min <= max && min <= Max;

    /// <summary>The stored form, which is also how it reads: "3", "1-5", "n/a".</summary>
    public override string ToString() =>
        NotApplicable ? "n/a" : Min == Max ? Min.ToString(CultureInfo.InvariantCulture) : $"{Min.ToString(CultureInfo.InvariantCulture)}-{Max.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"Levels 1–5", "Level 3", "No levels".</summary>
    public string Describe() =>
        NotApplicable ? "No levels" : Min == Max ? $"Level {Min.ToString(CultureInfo.CurrentCulture)}" : $"Levels {Min.ToString(CultureInfo.CurrentCulture)}–{Max.ToString(CultureInfo.CurrentCulture)}";
}

/// <summary>
/// How many players a book is written for, as stored: "4" or "4-6". Books say it as "for 4–6 characters", "four to six
/// players" or "a party of 4", so those read too. Unknown is the absence of a value, never a stored one.
/// </summary>
public readonly record struct PlayerRange(int Min, int Max)
{
    public const int Highest = 20;

    /// <summary>Words around the numbers that say nothing more: "for 4 to 6 player characters".</summary>
    static readonly HashSet<string> Filler =
    [
        with(StringComparer.Ordinal),
        "for", "a", "an", "party", "group", "of", "to", "or", "and", "up", "between", "from", "about", "players", "player", "characters",
        "character", "pcs", "pc", "people", "persons", "adventurers", "heroes", "the",
    ];

    static readonly string[] NumberWords = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

    /// <summary>The number a word in comparison form spells, from one to twelve, or null: "four" is 4.</summary>
    public static int? FromWord(string word) => Array.IndexOf(NumberWords, word) is var n and > 0 ? n : null;

    /// <summary>"4", "4-6", "4–6 players", "for four to six characters", "a party of 4", "solo".</summary>
    public static bool TryParse(string? text, out PlayerRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var numbers = new List<int>();
        foreach (var word in MetadataText.Words(MetadataText.Normalize(text)))
        {
            if (Filler.Contains(word)) continue;
            if (word is "solo" or "single") numbers.Add(1);
            else if (FromWord(word) is { } named) numbers.Add(named);
            else if (int.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) numbers.Add(number);
            else return false;
        }
        if (numbers.Count is 0 or > 2) return false;
        var (min, max) = (numbers[0], numbers[^1]);
        if (min < 1 || max > Highest || min > max) return false;
        range = new PlayerRange(min, max);
        return true;
    }

    public bool Contains(int players) => Min <= players && players <= Max;

    public bool Overlaps(int min, int max) => Min <= max && min <= Max;

    /// <summary>The stored form: "4", "4-6".</summary>
    public override string ToString() =>
        Min == Max ? Min.ToString(CultureInfo.InvariantCulture) : $"{Min.ToString(CultureInfo.InvariantCulture)}-{Max.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>"4–6 players", "1 player".</summary>
    public string Describe() =>
        Min == Max ? $"{Min.ToString(CultureInfo.CurrentCulture)} {(Min == 1 ? "player" : "players")}"
        : $"{Min.ToString(CultureInfo.CurrentCulture)}–{Max.ToString(CultureInfo.CurrentCulture)} players";
}

/// <summary>Turning what someone typed, or a source proposed, into a field's stored and comparison forms.</summary>
public static class MetadataValues
{
    public const int EarliestYear = 1970;
    public const int LatestYear = 2100;

    /// <summary>The comparison form of a stored value: a term's key (and a store's link) as is, everything else normalised.</summary>
    public static string Normalize(MetadataField field, string value) => field.Kind switch
    {
        FieldKind.Term or FieldKind.Store => value,
        FieldKind.Levels => LevelRange.TryParse(value, out var range) ? range.ToString() : MetadataText.Normalize(value),
        FieldKind.Players => PlayerRange.TryParse(value, out var players) ? players.ToString() : MetadataText.Normalize(value),
        _ => MetadataText.Normalize(value),
    };

    /// <summary>
    /// The stored form of a Text, Levels, Players or Year value, or null with a reason when it isn't one. Term values are
    /// resolved against the vocabulary instead (<see cref="Vocabulary.Resolve"/>); a Store value is only checked here,
    /// for a name or a web link, and resolved with <see cref="StoreLink.Read"/>.
    /// </summary>
    public static (string? Value, string? Problem) Parse(MetadataField field, string text)
    {
        var tidy = MetadataText.Tidy(text);
        switch (field.Kind)
        {
            case FieldKind.Levels:
                return LevelRange.TryParse(tidy, out var range)
                    ? (range.ToString(), null)
                    : (null, $"Levels look like 3, 1-5 or n/a, from 0 to {LevelRange.Highest}.");
            case FieldKind.Players:
                return PlayerRange.TryParse(tidy, out var players)
                    ? (players.ToString(), null)
                    : (null, $"Players look like 4 or 3-5, from 1 to {PlayerRange.Highest}.");
            case FieldKind.Store:
                var (name, url, problem) = StoreLink.Split(tidy);
                return problem is not null ? (null, problem)
                    : MetadataText.Normalize(name).Length == 0 && url is null ? (null, field == MetadataFields.GetItFrom ? $"{field.Label} needs a store's name or a web link." : $"{field.Label} needs a name or a web link.")
                    : (tidy, null);
            case FieldKind.Year:
                return int.TryParse(tidy, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= EarliestYear and <= LatestYear
                    ? (year.ToString(CultureInfo.InvariantCulture), null)
                    : (null, "A year looks like 2019.");
            default:
                return MetadataText.Normalize(tidy).Length == 0 ? (null, $"{field.Label} needs some letters or numbers.") : (tidy, null);
        }
    }

    /// <summary>
    /// Splits what was typed into a multi-value field: commas or semicolons separate values. In a Store field a web link
    /// keeps its own commas and semicolons, up to the next space.
    /// </summary>
    public static IReadOnlyList<string> Split(MetadataField field, string text) =>
        !field.Multiple ? string.IsNullOrWhiteSpace(text) ? [] : [text.Trim()]
        : field.Kind == FieldKind.Store ? StoreLink.SplitList(text)
        : [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(v => v.Length > 0)];
}
