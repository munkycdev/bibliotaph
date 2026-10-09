using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Search;

/// <summary>What the search box's field guide offers where the cursor is.</summary>
public enum GuideMode
{
    /// <summary>Field names: the box is empty, or the word at the cursor starts one ("sy" offers system:).</summary>
    Fields,

    /// <summary>The values in the library of a field with a closed list, after its colon (<c>system:</c>).</summary>
    Values,

    /// <summary>Just the example, after the colon of a free-text field (<c>title:</c>), whose values are anything.</summary>
    Example,
}

/// <summary>
/// Where the field guide is in the search box's text. A pick replaces <see cref="Start"/> to <see cref="End"/>;
/// <see cref="Typed"/> is the part of it before the cursor, which narrows the list. In <see cref="GuideMode.Fields"/>,
/// <see cref="Fields"/> are the names on offer; otherwise <see cref="Field"/> is the field whose value is being typed.
/// </summary>
public sealed record GuideContext(GuideMode Mode, int Start, int End, string Typed, IReadOnlyList<SearchFieldInfo> Fields, SearchFieldInfo? Field = null)
{
    public bool Equals(GuideContext? other) =>
        other is not null && (Mode, Start, End, Typed, Field) == (other.Mode, other.Start, other.End, other.Typed, other.Field) && Fields.SequenceEqual(other.Fields);

    public override int GetHashCode() => HashCode.Combine(Mode, Start, End, Typed, Field);
}

/// <summary>A value of a field in the library, with how many documents have it. <see cref="Value"/> is what a search uses.</summary>
public sealed record GuideValue(string Value, string Label, long Count);

/// <summary>The search box's text and cursor after a pick.</summary>
public readonly record struct GuideEdit(string Text, int Caret);

/// <summary>
/// The logic behind the search box's field guide, kept here so it can be tested without a window: which fields or
/// values to offer at the cursor, and what picking one does to the text.
/// </summary>
public static class SearchGuide
{
    /// <summary>
    /// What to offer at <paramref name="caret"/> in <paramref name="text"/>, or null to offer nothing. With the cursor
    /// in empty space, that is every field when the box is empty or the guide was asked for (<paramref name="requested"/>,
    /// Ctrl+Space), and nothing otherwise, so the guide doesn't cover the results each time a word ends. Asked for in
    /// the middle of a word that names no field, it offers every field to add after that word.
    /// </summary>
    public static GuideContext? At(string text, int caret, bool requested = false)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var start = caret;
        while (start > 0 && !IsBreak(text[start - 1])) start--;
        var end = caret;
        while (end < text.Length && !IsBreak(text[end])) end++;
        // An exclusion's minus stays where it is: -sy offers system: and gives -system:.
        if (start < caret && text[start] == '-') start++;
        var typed = text[start..caret];
        var word = text[start..end];

        var colon = typed.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0 && !HasQuote(typed[..colon]))
        {
            if (SearchFields.Find(typed[..colon]) is not { } field) return requested ? After(end) : null;
            var value = typed[(colon + 1)..];
            if (HasQuote(value)) return null; // a phrase is free text; no list helps with it
            return new GuideContext(field.ListsValues ? GuideMode.Values : GuideMode.Example, start + colon + 1, end, value, [], field);
        }

        // Only a whole word at the cursor is narrowed on; a cursor inside a word, or in a quote, isn't naming a field.
        if (end > caret || word.Contains(':', StringComparison.Ordinal) || HasQuote(word)) return requested ? After(end) : null;
        if (typed.Length == 0)
            return requested || string.IsNullOrWhiteSpace(text) ? new GuideContext(GuideMode.Fields, caret, caret, "", SearchFields.All) : null;
        var matches = Matching(typed);
        if (matches.Count > 0) return new GuideContext(GuideMode.Fields, start, end, typed, matches);
        return requested ? After(end) : null;
    }

    /// <summary>The fields whose name or an alias starts with <paramref name="typed"/>, ignoring case, in list order.</summary>
    public static IReadOnlyList<SearchFieldInfo> Matching(string typed) =>
        [.. SearchFields.All.Where(f => f.Names.Any(n => n.StartsWith(typed, StringComparison.OrdinalIgnoreCase)))];

    /// <summary>
    /// The values that fit what has been typed after the colon: those whose search value, label, or any word of the
    /// label starts with it. The order is kept (most common first), and nothing typed keeps them all.
    /// </summary>
    public static IReadOnlyList<GuideValue> Values(IEnumerable<GuideValue> values, string typed)
    {
        var wanted = MetadataText.Normalize(typed.TrimEnd('*'));
        if (wanted.Length == 0) return [.. values];
        return [.. values.Where(v => v.Value.StartsWith(typed.TrimEnd('*'), StringComparison.OrdinalIgnoreCase) || Fits(MetadataText.Normalize(v.Label)))];

        bool Fits(string label) => label.StartsWith(wanted, StringComparison.Ordinal) || MetadataText.Words(label).Any(w => w.StartsWith(wanted, StringComparison.Ordinal));
    }

    /// <summary>
    /// The formats to offer after <c>format:</c>: those in the library, most first, then <c>image</c> for either picture
    /// format when the library has both.
    /// </summary>
    public static IReadOnlyList<GuideValue> FormatValues(IEnumerable<GuideValue> formats)
    {
        var values = formats.ToList();
        var pictures = values.Where(v => SourceFormats.IsImage(v.Value)).ToList();
        if (pictures.Count > 1) values.Add(new GuideValue("image", "Any picture", pictures.Sum(p => p.Count)));
        return values;
    }

    /// <summary>
    /// Puts <paramref name="field"/>'s name and colon where the guide is, with the cursor after the colon so the
    /// value can follow (and, for a closed list, the guide can offer it).
    /// </summary>
    public static GuideEdit InsertField(string text, GuideContext at, SearchFieldInfo field)
    {
        var insert = (StartsWord(text, at.Start) ? "" : " ") + field.Prefix;
        return new GuideEdit(text[..at.Start] + insert + text[at.End..], at.Start + insert.Length);
    }

    /// <summary>
    /// Puts <paramref name="value"/> after the field's colon, in quotes if it needs them, and moves the cursor past a
    /// space after it, ready for the next part of the search.
    /// </summary>
    public static GuideEdit InsertValue(string text, GuideContext at, string value)
    {
        var insert = Quote(value);
        var rest = text[at.End..];
        if (rest.Length == 0 || (!char.IsWhiteSpace(rest[0]) && rest[0] != ')')) rest = " " + rest;
        var caret = at.Start + insert.Length + (char.IsWhiteSpace(rest[0]) ? 1 : 0);
        return new GuideEdit(text[..at.Start] + insert + rest, caret);
    }

    /// <summary>A value as the search box needs it: as is when it is one plain word, otherwise in quotes.</summary>
    public static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '&' or '+' or '/') && value[0] != '-'
            ? value
            : "\"" + value.Replace("\"", "", StringComparison.Ordinal) + "\"";

    /// <summary>Every field, added after the word that ends at <paramref name="end"/>.</summary>
    static GuideContext After(int end) => new(GuideMode.Fields, end, end, "", SearchFields.All);

    /// <summary>Where a word ends in the search box, as the parser splits them.</summary>
    static bool IsBreak(char c) => char.IsWhiteSpace(c) || c is '(' or ')';

    /// <summary>True when <paramref name="at"/> is where a word starts, or just after the minus that starts one.</summary>
    static bool StartsWord(string text, int at) =>
        at == 0 || IsBreak(text[at - 1]) || (text[at - 1] == '-' && (at == 1 || IsBreak(text[at - 2])));

    static bool HasQuote(string s) => s.AsSpan().IndexOfAny('"', '“', '”') >= 0;
}
