namespace Bibliotaph.Api.Contracts;

/// <summary>
/// What the Library page asks for, as GET /api/library's query string: the search, which results tab, the order,
/// whether a pack's files get cards of their own, and the filter panel's menus. Every menu's empty value is "any";
/// the names of <see cref="Sort"/>, <see cref="Kind"/>, <see cref="Ai"/>, <see cref="Adventures"/> and <see
/// cref="Collections"/> are those in <see cref="LibraryChoices"/>.
/// </summary>
public sealed record LibraryQuery
{
    public string? Q { get; init; }

    /// <summary>"pages" for the Inside documents tab while searching; anything else is the Documents tab.</summary>
    public string? Tab { get; init; }

    public string? Sort { get; init; }

    public bool EachFile { get; init; }

    public string? System { get; init; }

    public string? Type { get; init; }

    public int? Level { get; init; }

    public bool UnknownLevels { get; init; }

    public int? Players { get; init; }

    public string? Style { get; init; }

    public string? Duration { get; init; }

    public long? Folder { get; init; }

    public string? Own { get; init; }

    public string? Store { get; init; }

    public string? Kind { get; init; }

    public string? Ai { get; init; }

    public bool Copies { get; init; }

    public string? Adventures { get; init; }

    public string? Collections { get; init; }

    /// <summary>
    /// The values picked in the type-ahead filters, as JSON: an object of arrays by field key,
    /// <c>{"setting":["the-marches"]}</c>, as a saved search keeps them.
    /// </summary>
    public string? Picked { get; init; }

    /// <summary>Only the books marked with a heart: the Library as Favorites.</summary>
    public bool Favorites { get; init; }

    /// <summary>The collection shown, its books and its sub-collections' books: the Library scoped to it.</summary>
    public long? Collection { get; init; }

    /// <summary>Only the books added to <see cref="Collection"/> itself: its "Only books added here".</summary>
    public bool AddedHere { get; init; }

    /// <summary>The binder whose books are shown, whole or for some of their pages: Show these books in the Library.</summary>
    public long? Binder { get; init; }

    public bool IsSearching => !string.IsNullOrWhiteSpace(Q);

    public bool IsPagesTab => IsSearching && Tab == "pages";
}

/// <summary>
/// A card in the library's grid or list, with the words the Windows app shows on it. <see cref="Id"/> tells cards
/// apart (a file in a pack shown on its own card has its own); <see cref="EntryId"/> is the book whose details it
/// opens. <see cref="Cover"/> and each of <see cref="Mosaic"/> are names for /api/library/covers/.
/// </summary>
public sealed record BookCard(
    string Id, long EntryId, long DocumentId, string Title, string Meta, string Byline, string Size, string Format,
    string System, string Kind, string Levels, string Players, string Copies, string? Cover, IReadOnlyList<string> Mosaic,
    string Initial, bool Favorite, string? AiTip, string TextMark, string? TextMarkTip, bool Elsewhere, string Badge,
    bool Part, bool FoundInBook, string Availability, string? AvailabilityTip);

/// <summary>
/// A page that matched. <see cref="Snippet"/> has its hits between <see cref="LibraryChoices.HitStart"/> and <see
/// cref="LibraryChoices.HitEnd"/>.
/// </summary>
public sealed record PageMatch(int PdfPage, string Where, string Snippet, string Source);

/// <summary>A book's best matching pages, and "3 more pages match" when more matched than are shown.</summary>
public sealed record BookMatches(BookCard Book, IReadOnlyList<PageMatch> Pages, string More);

/// <summary>A choice in a menu: its value (empty for "any") and its label, counted where the Windows app counts it.</summary>
public sealed record FilterChoice(string Value, string Label);

/// <summary>
/// The filter panel's counted menus, each counted without its own choice so picking a system still shows how many
/// books the other systems have. The Show flags hide a menu the library has nothing for, as the Windows app does.
/// </summary>
public sealed record LibraryMenus(
    IReadOnlyList<FilterChoice> Systems, IReadOnlyList<FilterChoice> Types, IReadOnlyList<FilterChoice> Styles,
    IReadOnlyList<FilterChoice> Durations, IReadOnlyList<FilterChoice> Folders, IReadOnlyList<FilterChoice> Owns,
    IReadOnlyList<FilterChoice> Stores, bool ShowStyle, bool ShowDuration, bool ShowOwn, bool ShowStore, bool ShowAi, bool ShowCopies,
    bool ShowAdventures, bool ShowCollections, IReadOnlyList<FilterBox> Boxes);

/// <summary>
/// A type-ahead filter, for a field with too many values for a menu: its label and hint, whether the books shown have
/// any value for it, and the values picked, with their names. What to offer for what was typed comes from
/// /api/library/values/{key}.
/// </summary>
public sealed record FilterBox(string Key, string Label, string Hint, bool Offered, IReadOnlyList<FilterChoice> Picked);

/// <summary>A value a type-ahead filter offers: "The Marches", with how many books it would show.</summary>
public sealed record ValueSuggestion(string Value, string Label, string Count, string Spoken);

/// <summary>
/// An empty page: what to say, and the one thing to do about it. <see cref="Action"/> is one of clear-filters,
/// clear-search, clear-all, add-folder or show-documents.
/// </summary>
public sealed record EmptyLibrary(string Title, string Message, string Action, string ActionText);

/// <summary>
/// The Library page's results: the cards, or on the Inside documents tab the matching pages; the count line, the tabs'
/// labels while searching, the query's problem if it has one, and how much of the library is searchable yet.
/// </summary>
public sealed record LibraryResults(
    IReadOnlyList<BookCard> Books, IReadOnlyList<BookMatches> Matches, string CountText, string CountLabel, string DocumentsTab,
    string PagesTab, string? Issue, string? Coverage, EmptyLibrary? Empty, LibraryMenus Menus, ScopeText? Scope = null);

/// <summary>
/// The part of the library shown, such as Favorites, as the page names it: its heading, its subtitle, and its chip
/// above the books, whose × shows the whole library. A collection's come with its own page.
/// </summary>
public sealed record ScopeText(string Heading, string Subtitle, string Chip);

/// <summary>The Library's fixed menus, in the Windows app's words and order. Each value is the name the API takes.</summary>
public static class LibraryChoices
{
    /// <summary>Marks the start of a hit in a snippet: a control character, so it can't clash with page text.</summary>
    public const char HitStart = '\u0002';

    public const char HitEnd = '\u0003';

    public static IReadOnlyList<FilterChoice> SearchSorts { get; } =
        [new("Relevance", "Best match"), new("RecentlyAdded", "Recently added"), new("RecentlyOpened", "Recently opened"), new("Title", "Title A–Z"), new("Publisher", "Publisher A–Z")];

    public static IReadOnlyList<FilterChoice> BrowseSorts { get; } = [.. SearchSorts.Skip(1)];

    public static IReadOnlyList<FilterChoice> Kinds { get; } =
        [new("", "All kinds"), new("Books", "Books"), new("Images", "Images"), new("Packs", "Packs"), new("Here", "In your storage"), new("Elsewhere", "Owned elsewhere")];

    public static IReadOnlyList<FilterChoice> Levels { get; } =
        [new("", "Any level"), .. Enumerable.Range(1, 20).Select(l => new FilterChoice(l.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Level {l}"))];

    public static IReadOnlyList<FilterChoice> Players { get; } =
        [new("", "Any number"), new("1", "1 player"), .. Enumerable.Range(2, 9).Select(n => new FilterChoice(n.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{n} players"))];

    public static IReadOnlyList<FilterChoice> Ai { get; } = [new("", "All books"), new("Read", "Read by AI"), new("NotRead", "Not read by AI")];

    public static IReadOnlyList<FilterChoice> Copies { get; } = [new("", "All books"), new("true", "Books with copies")];

    public static IReadOnlyList<FilterChoice> Adventures { get; } = [new("", "Inside their book"), new("Cards", "Show as cards"), new("Hidden", "Hide")];

    public static IReadOnlyList<FilterChoice> Collections { get; } = [new("", "All books"), new("InOne", "In a collection"), new("NotInOne", "Not in a collection")];
}

/// <summary>
/// Home's rows, as the Windows app's: the books opened last and added last, a cover each, and what the library is
/// doing, for the note shown before any book has been read.
/// </summary>
public sealed record HomeShelves(IReadOnlyList<BookCard> Opened, IReadOnlyList<BookCard> Added, string Status, BinderCard? Preparing = null);
