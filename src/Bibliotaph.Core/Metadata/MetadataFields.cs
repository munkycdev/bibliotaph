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

    /// <summary>How many players it is written for: "4", "4-6" (the intended party).</summary>
    Players,

    /// <summary>
    /// A term with an optional web link after a space: where to get it ("drivethrurpg https://…") or where else it is
    /// owned ("dnd-beyond https://…"). See <see cref="StoreLink"/>.
    /// </summary>
    Store,
}

/// <summary>
/// One metadata field. <see cref="Multiple"/> fields hold a set of values, each suggested, confirmed or rejected on
/// its own; single fields hold one winner. <see cref="Vocabulary"/> names the term list a Term field draws from, or
/// the alias list a Text field is normalised through (publishers).
/// </summary>
public sealed record MetadataField(string Key, string Label, FieldKind Kind, bool Multiple = false, string? Vocabulary = null)
{
    /// <summary>
    /// Closed fields have a fixed set of meanings, so sources that disagree about them need a look. Free text never
    /// conflicts by itself: the best source wins.
    /// </summary>
    public bool IsClosed => Kind is FieldKind.Term or FieldKind.Levels or FieldKind.Players;

    public override string ToString() => Key;
}

/// <summary>
/// The fields Bibliotaph catalogs. Map properties and content features come later.
/// </summary>
public static class MetadataFields
{
    public static readonly MetadataField Title = new("title", "Title", FieldKind.Text);
    public static readonly MetadataField Publisher = new("publisher", "Publisher", FieldKind.Text, Vocabulary: "publisher");
    public static readonly MetadataField Authors = new("authors", "Authors", FieldKind.Text, Multiple: true);

    /// <summary>Who drew it, as a catalogue credits them (RPGGeek's artists); the AI isn't asked, since credits pages list far more.</summary>
    public static readonly MetadataField Artists = new("artists", "Artists", FieldKind.Text, Multiple: true);
    public static readonly MetadataField Series = new("series", "Series", FieldKind.Text);
    public static readonly MetadataField Year = new("year", "Year", FieldKind.Year);
    public static readonly MetadataField System = new("system", "Game system", FieldKind.Term, Vocabulary: "system");
    public static readonly MetadataField Edition = new("edition", "Edition", FieldKind.Term, Vocabulary: "edition");
    public static readonly MetadataField Types = new("type", "Type", FieldKind.Term, Multiple: true, Vocabulary: "type");
    public static readonly MetadataField Levels = new("levels", "Levels", FieldKind.Levels);
    public static readonly MetadataField Settings = new("setting", "Setting", FieldKind.Term, Multiple: true, Vocabulary: "setting");
    public static readonly MetadataField Players = new("players", "Players", FieldKind.Players);

    /// <summary>
    /// What comes with an adventure for the table: pre-generated characters, handouts, battle maps, or that it can be
    /// played solo. A fixed list, as the types are, filled by a catalogue that says so and by the user; the AI isn't asked.
    /// </summary>
    public static readonly MetadataField Includes = new("includes", "Includes", FieldKind.Term, Multiple: true, Vocabulary: "includes");

    /// <summary>
    /// How long it takes to play, in sessions rather than hours (never assume every one-shot lasts three
    /// hours): one-shot, short, long or campaign.
    /// </summary>
    public static readonly MetadataField Duration = new("duration", "Duration", FieldKind.Term, Vocabulary: "duration");

    /// <summary>The kind of adventure it is: a heist, a dungeon crawl, a hexcrawl. Themes keep tone and subject.</summary>
    public static readonly MetadataField Styles = new("style", "Style", FieldKind.Term, Multiple: true, Vocabulary: "style");

    /// <summary>Tone and subject: horror, intrigue, fey. The kind of adventure is <see cref="Styles"/>.</summary>
    public static readonly MetadataField Themes = new("theme", "Themes", FieldKind.Term, Multiple: true, Vocabulary: "theme");
    public static readonly MetadataField Environments = new("environment", "Environments", FieldKind.Term, Multiple: true, Vocabulary: "environment");

    /// <summary>
    /// The rules it uses, in a catalogue's words ("Dice (d20)", "Class-based"): free text rather than a list to choose
    /// from, since they mostly describe the game system rather than the book, and the AI isn't asked.
    /// </summary>
    public static readonly MetadataField Mechanics = new("mechanics", "Mechanics", FieldKind.Text, Multiple: true);

    /// <summary>
    /// The monsters it pits the party against, its bosses first, in a catalogue's words: free text, found by plain
    /// search ("owlbear"), and the AI isn't asked.
    /// </summary>
    public static readonly MetadataField Monsters = new("monsters", "Monsters", FieldKind.Text, Multiple: true);

    /// <summary>The magic items and treasure it names, in a catalogue's words: free text, found by plain search.</summary>
    public static readonly MetadataField Items = new("items", "Items", FieldKind.Text, Multiple: true);
    public static readonly MetadataField Tags = new("tags", "Your tags", FieldKind.Text, Multiple: true);

    /// <summary>
    /// Where else the user owns the book: in print, on a VTT, with the book's page there when the place is online:
    /// "dnd-beyond https://www.dndbeyond.com/sources/…". Only the user sets it; no hint or model suggests it. An entry
    /// owned elsewhere, with no file here, says so first, and opens its link.
    /// </summary>
    public static readonly MetadataField AlsoOwn = new("own", "Also own", FieldKind.Store, Multiple: true, Vocabulary: "own");

    /// <summary>
    /// Where the book can be got again: a store, with a link to its page there when someone has one. Rules suggest the
    /// store from folder and file names, the PDF's producer and a watermark's store name; the buyer's name and order
    /// number in a watermark are never kept. No model suggests it.
    /// </summary>
    public static readonly MetadataField GetItFrom = new("store", "Get it from", FieldKind.Store, Multiple: true, Vocabulary: "store");

    /// <summary>Every field, in the order the inspector shows them.</summary>
    public static IReadOnlyList<MetadataField> All { get; } =
        [Title, System, Edition, Types, Levels, Players, Includes, Duration, Publisher, Authors, Artists, Series, Year, Settings, Styles, Themes, Environments,
            Monsters, Items, Mechanics, AlsoOwn, GetItFrom, Tags];

    static readonly Dictionary<string, MetadataField> ByKey = All.ToDictionary(f => f.Key, StringComparer.Ordinal);

    public static MetadataField? Find(string key) => ByKey.GetValueOrDefault(key);

    public static MetadataField Get(string key) => ByKey.TryGetValue(key, out var field) ? field : throw new ArgumentException($"No metadata field {key}.", nameof(key));

    /// <summary>The field a vocabulary's terms fill: Types for "type". Publishers are free text, so "publisher" gives null.</summary>
    public static MetadataField? ForVocabulary(string vocabulary) =>
        All.FirstOrDefault(f => f.Kind is FieldKind.Term or FieldKind.Store && f.Vocabulary == vocabulary);

    /// <summary>The vocabularies with terms, which Term and Store fields draw from.</summary>
    public static IReadOnlyList<string> TermVocabularies { get; } = [.. All.Where(f => f.Kind is FieldKind.Term or FieldKind.Store).Select(f => f.Vocabulary!)];
}
