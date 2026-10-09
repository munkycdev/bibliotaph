namespace Bibliotaph.Core.Metadata;

/// <summary>What kind of value a field holds, which decides how it is normalised, edited and searched.</summary>
public enum FieldKind
{
    /// <summary>Free text: a title, a publisher, an author.</summary>
    Text,

    /// <summary>A term from a vocabulary (a game system, a document type), stored by its key.</summary>
    Term,

    /// <summary>A level range for the document's own game system: "3", "1-5", or not applicable.</summary>
    Levels,

    /// <summary>A publication year.</summary>
    Year,
}

/// <summary>
/// One metadata field. <see cref="Multiple"/> fields hold a set of values, each suggested, confirmed or rejected on
/// its own; single fields hold one winner. <see cref="Vocabulary"/> names the term list a Term field draws from, or
/// the alias list a Text field is normalised through (publishers).
/// </summary>
public sealed record MetadataField(string Key, string Label, FieldKind Kind, bool Multiple = false, string? Vocabulary = null)
{
    /// <summary>
    /// Closed fields have a fixed set of meanings, so sources that disagree about them need a look (choice 4 of the
    /// slice 2 plan). Free text never conflicts by itself: the best source wins.
    /// </summary>
    public bool IsClosed => Kind is FieldKind.Term or FieldKind.Levels;

    public override string ToString() => Key;
}

/// <summary>The fields slice 2 catalogs. Party size, duration, map properties and content features come later.</summary>
public static class MetadataFields
{
    public static readonly MetadataField Title = new("title", "Title", FieldKind.Text);
    public static readonly MetadataField Publisher = new("publisher", "Publisher", FieldKind.Text, Vocabulary: "publisher");
    public static readonly MetadataField Authors = new("authors", "Authors", FieldKind.Text, Multiple: true);
    public static readonly MetadataField Series = new("series", "Series", FieldKind.Text);
    public static readonly MetadataField Year = new("year", "Year", FieldKind.Year);
    public static readonly MetadataField System = new("system", "Game system", FieldKind.Term, Vocabulary: "system");
    public static readonly MetadataField Edition = new("edition", "Edition", FieldKind.Term, Vocabulary: "edition");
    public static readonly MetadataField Types = new("type", "Type", FieldKind.Term, Multiple: true, Vocabulary: "type");
    public static readonly MetadataField Levels = new("levels", "Levels", FieldKind.Levels);
    public static readonly MetadataField Settings = new("setting", "Setting", FieldKind.Term, Multiple: true, Vocabulary: "setting");
    public static readonly MetadataField Themes = new("theme", "Themes", FieldKind.Term, Multiple: true, Vocabulary: "theme");
    public static readonly MetadataField Environments = new("environment", "Environments", FieldKind.Term, Multiple: true, Vocabulary: "environment");
    public static readonly MetadataField Tags = new("tags", "Your tags", FieldKind.Text, Multiple: true);

    /// <summary>Every field, in the order the inspector shows them.</summary>
    public static IReadOnlyList<MetadataField> All { get; } =
        [Title, System, Edition, Types, Levels, Publisher, Authors, Series, Year, Settings, Themes, Environments, Tags];

    static readonly Dictionary<string, MetadataField> ByKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

    public static MetadataField? Find(string key) => ByKey.GetValueOrDefault(key);

    public static MetadataField Get(string key) => ByKey.TryGetValue(key, out var field) ? field : throw new ArgumentException($"No metadata field {key}.", nameof(key));

    /// <summary>The field a vocabulary's terms fill: Types for "type". Publishers are free text, so "publisher" gives null.</summary>
    public static MetadataField? ForVocabulary(string vocabulary) =>
        All.FirstOrDefault(f => f.Kind == FieldKind.Term && f.Vocabulary == vocabulary);

    /// <summary>The vocabularies with terms, which Term fields draw from.</summary>
    public static IReadOnlyList<string> TermVocabularies { get; } = [.. All.Where(f => f.Kind == FieldKind.Term).Select(f => f.Vocabulary!)];
}
