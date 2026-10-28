using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Search;

/// <summary>What a search field's value is, which decides what the search box's field guide offers after the colon.</summary>
public enum SearchFieldKind
{
    /// <summary>Words, found anywhere in the value: <c>title:dragon</c>. The guide shows the example.</summary>
    Text,

    /// <summary>A vocabulary term by any of its names: <c>system:5e</c>. The guide lists the values in the library.</summary>
    Term,

    /// <summary>One of a fixed list: <c>format:pdf</c>. The guide lists the formats in the library.</summary>
    Format,

    /// <summary>A level, a range, none or unknown: <c>level:1-5</c>. The guide shows the example.</summary>
    Level,

    /// <summary>A number of players, a range or unknown: <c>players:4</c>. The guide shows the example.</summary>
    Players,
}

/// <summary>
/// A field the search box understands: its name and the other names it accepts (<see cref="Aliases"/>, such as
/// <c>authors:</c>), what it matches in one line, and an example. <see cref="Metadata"/> is the catalog field behind
/// it, when there is one; a Term field's values are that field's vocabulary.
/// </summary>
public sealed record SearchFieldInfo(
    SearchField Field, string Name, IReadOnlyList<string> Aliases, SearchFieldKind Kind, string Description, string Example,
    MetadataField? Metadata = null)
{
    /// <summary>The name and every alias.</summary>
    public IEnumerable<string> Names => [Name, .. Aliases];

    /// <summary>What the search box inserts: the name and its colon.</summary>
    public string Prefix => Name + ":";

    /// <summary>True when the values come from a closed list the guide can offer: game systems, types, formats.</summary>
    public bool ListsValues => Kind is SearchFieldKind.Term or SearchFieldKind.Format;

    public override string ToString() => Prefix;
}

/// <summary>
/// Every search field, in one list that the parser, its messages and the search box's field guide all read, so a
/// field added here is searchable and offered without another copy to keep in step.
/// </summary>
public static class SearchFields
{
    public static readonly SearchFieldInfo Title = new(SearchField.Title, "title", [], SearchFieldKind.Text,
        "the document's title", "title:dragon", MetadataFields.Title);

    public static readonly SearchFieldInfo System = new(SearchField.System, "system", [], SearchFieldKind.Term,
        "the game system, or an edition by any of its names", "system:5e", MetadataFields.System);

    public static readonly SearchFieldInfo Edition = new(SearchField.Edition, "edition", [], SearchFieldKind.Term,
        "an edition of a game system", "edition:pf2e", MetadataFields.Edition);

    public static readonly SearchFieldInfo Type = new(SearchField.Type, "type", [], SearchFieldKind.Term,
        "what kind of document it is", "type:adventure", MetadataFields.Types);

    public static readonly SearchFieldInfo Level = new(SearchField.Level, "level", ["levels"], SearchFieldKind.Level,
        "a character level or range, none, or unknown", "level:3", MetadataFields.Levels);

    public static readonly SearchFieldInfo Players = new(SearchField.Players, "players", ["player"], SearchFieldKind.Players,
        "how many players it is for: a number, a range, or unknown", "players:4", MetadataFields.Players);

    public static readonly SearchFieldInfo Duration = new(SearchField.Duration, "duration", [], SearchFieldKind.Term,
        "how long it takes to play: one-shot, short, long or campaign", "duration:one-shot", MetadataFields.Duration);

    public static readonly SearchFieldInfo Publisher = new(SearchField.Publisher, "publisher", [], SearchFieldKind.Text,
        "words in the publisher's name", "publisher:kobold", MetadataFields.Publisher);

    public static readonly SearchFieldInfo Author = new(SearchField.Author, "author", ["authors"], SearchFieldKind.Text,
        "words in an author's name", "author:doe", MetadataFields.Authors);

    public static readonly SearchFieldInfo Series = new(SearchField.Series, "series", [], SearchFieldKind.Text,
        "words in the series name", "series:chronicles", MetadataFields.Series);

    public static readonly SearchFieldInfo Setting = new(SearchField.Setting, "setting", [], SearchFieldKind.Term,
        "a campaign setting", "setting:eberron", MetadataFields.Settings);

    public static readonly SearchFieldInfo Style = new(SearchField.Style, "style", [], SearchFieldKind.Term,
        "the kind of adventure, such as heist or dungeon crawl", "style:heist", MetadataFields.Styles);

    public static readonly SearchFieldInfo Theme = new(SearchField.Theme, "theme", [], SearchFieldKind.Term,
        "a theme or tone, such as horror or fey", "theme:horror", MetadataFields.Themes);

    public static readonly SearchFieldInfo Environment = new(SearchField.Environment, "environment", ["env"], SearchFieldKind.Term,
        "where it happens", "environment:urban", MetadataFields.Environments);

    /// <summary>Free text with no vocabulary: the guide lists the creatures found in the library's chapters.</summary>
    public static readonly SearchFieldInfo Creature = new(SearchField.Creature, "creature", ["creatures", "monster"], SearchFieldKind.Term,
        "a creature found in one of its chapters by the AI", "creature:dragon");

    public static readonly SearchFieldInfo Own = new(SearchField.Own, "own", [], SearchFieldKind.Term,
        "where else you own it: in print, on a VTT", "own:foundry", MetadataFields.AlsoOwn);

    public static readonly SearchFieldInfo Store = new(SearchField.Store, "store", ["from"], SearchFieldKind.Term,
        "where to get it, such as kickstarter or drivethrurpg", "store:kickstarter", MetadataFields.GetItFrom);

    public static readonly SearchFieldInfo Tag = new(SearchField.Tag, "tag", ["tags"], SearchFieldKind.Text,
        "one of your tags", "tag:prep", MetadataFields.Tags);

    public static readonly SearchFieldInfo Favorite = new(SearchField.Favorite, "favorite", ["favourite", "fav"], SearchFieldKind.Text,
        "yes for the books you marked with a heart, no for the rest", "favorite:yes");

    public static readonly SearchFieldInfo Where = new(SearchField.Where, "where", [], SearchFieldKind.Text,
        "here for books with a file on this PC, elsewhere for books you own only somewhere else", "where:elsewhere");

    public static readonly SearchFieldInfo Collection = new(SearchField.Collection, "collection", ["collections"], SearchFieldKind.Text,
        "a collection's whole name, in quotes if it has spaces", "collection:\"Winter campaign\"");

    public static readonly SearchFieldInfo Format = new(SearchField.Format, "format", [], SearchFieldKind.Format,
        "pdf, epub, mobi, azw3, jpg, png, webp, image for any picture format, ebook for any e-book, or kindle for any Kindle book", "format:pdf");

    public static readonly SearchFieldInfo Folder = new(SearchField.Folder, "folder", [], SearchFieldKind.Text,
        "part of a folder's name", "folder:maps");

    public static readonly SearchFieldInfo Isbn = new(SearchField.Isbn, "isbn", [], SearchFieldKind.Text,
        "an ISBN printed in it, with or without hyphens, or unknown", "isbn:9781234567897");

    /// <summary>Every field, in the order the field guide lists them: the inspector's order, then the file's own.</summary>
    public static IReadOnlyList<SearchFieldInfo> All { get; } =
        [Title, System, Edition, Type, Level, Players, Duration, Publisher, Author, Series, Setting, Style, Theme, Environment, Creature, Own, Store, Tag,
            Favorite, Where, Collection, Format, Folder, Isbn];

    /// <summary>Fields that arrive with later metadata. Typing one says so rather than searching for the word.</summary>
    public static IReadOnlyList<string> Coming { get; } = ["length"];

    /// <summary>The rest of the search syntax, in one line, for the foot of the field guide.</summary>
    public const string Syntax = "\"quotes\" for a phrase, -word to exclude, OR, word* for a prefix";

    static readonly Dictionary<string, SearchFieldInfo> ByName =
        All.SelectMany(f => f.Names.Select(name => (name, f))).ToDictionary(p => p.name, p => p.f, StringComparer.OrdinalIgnoreCase);

    static readonly Dictionary<SearchField, SearchFieldInfo> ByField = All.ToDictionary(f => f.Field);

    /// <summary>The field a name or alias names, ignoring case, or null.</summary>
    public static SearchFieldInfo? Find(string name) => ByName.GetValueOrDefault(name);

    public static SearchFieldInfo Get(SearchField field) => ByField[field];

    /// <summary>True for a name that is coming but not searchable yet, such as <c>length</c>.</summary>
    public static bool IsComing(string name) => Coming.Contains(name, StringComparer.OrdinalIgnoreCase);
}
