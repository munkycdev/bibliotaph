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
        var decomposed = text.Normalize(NormalizationForm.FormKD);
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

/// <summary>Turning what someone typed, or a source proposed, into a field's stored and comparison forms.</summary>
public static class MetadataValues
{
    public const int EarliestYear = 1970;
    public const int LatestYear = 2100;

    /// <summary>The comparison form of a stored value: a term's key as is, everything else normalised.</summary>
    public static string Normalize(MetadataField field, string value) => field.Kind switch
    {
        FieldKind.Term => value,
        FieldKind.Levels => LevelRange.TryParse(value, out var range) ? range.ToString() : MetadataText.Normalize(value),
        _ => MetadataText.Normalize(value),
    };

    /// <summary>
    /// The stored form of a Text, Levels or Year value, or null with a reason when it isn't one. Term values are
    /// resolved against the vocabulary instead (<see cref="Vocabulary.Resolve"/>).
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
            case FieldKind.Year:
                return int.TryParse(tidy, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= EarliestYear and <= LatestYear
                    ? (year.ToString(CultureInfo.InvariantCulture), null)
                    : (null, "A year looks like 2019.");
            default:
                return MetadataText.Normalize(tidy).Length == 0 ? (null, $"{field.Label} needs some letters or numbers.") : (tidy, null);
        }
    }

    /// <summary>Splits what was typed into a multi-value field: commas or semicolons separate values.</summary>
    public static IReadOnlyList<string> Split(MetadataField field, string text) =>
        field.Multiple
            ? [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(v => v.Length > 0)]
            : string.IsNullOrWhiteSpace(text) ? [] : [text.Trim()];
}
