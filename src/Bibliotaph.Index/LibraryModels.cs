using System.Text.Json;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Index;

/// <summary>
/// A library card as the grid and search results show it: an entry, through the document it shows (<see
/// cref="DocumentId"/>, which opens). <see cref="Title"/> is its effective title, or the one from its file name. <see
/// cref="System"/> ("D&amp;D 5e"), <see cref="Kind"/> ("Adventure") and <see cref="Levels"/> ("Levels 1–5") read as
/// cards show them; <see cref="Suggested"/> means some of it is unconfirmed. <see cref="AiModel"/> is the model that
/// last read it, if one has. <see cref="Copies"/> counts the files of the book it has. A pack (<see cref="EntryKind"/>)
/// shows its first file, counts its <see cref="Members"/> (and <see cref="PdfMembers"/>, the PDFs among them), and has
/// the covers of its first four files for its mosaic (<see cref="MemberCovers"/>), unless one of its files was chosen
/// as its cover (<see cref="CoverMember"/>). A pack found by one of its files'
/// names carries that file (<see cref="MatchedMember"/>, <see cref="MatchedDocumentId"/>), which opening it shows. A book owned elsewhere (<see
/// cref="IsElsewhere"/>) has no file: its <see cref="DocumentId"/> is 0 and its <see cref="Format"/> empty, and <see
/// cref="AlsoOwn"/> says where it is owned ("Print · Foundry VTT"), as any card can. <see cref="Favorite"/> is a heart
/// on it, and <see cref="OpenedUtc"/> when it was last opened, if it has been. <see cref="TextAccess"/> says when its
/// text won't be read by itself: it is locked, protected or forgotten. An adventure inside a file (<see
/// cref="IsPart"/>) opens its file's document at its first page; its cover is that page and its page count its own
/// pages.
/// </summary>
public sealed record LibraryEntry(
    EntryId EntryId, long DocumentId, string Title, string Format, int? PageCount, string? Cover, string? FolderHint, DateTime AddedUtc, bool Searchable,
    string? System = null, string? Kind = null, string? Publisher = null, string? Levels = null, bool NeedsReview = false, bool Suggested = false,
    string? AiModel = null, int Copies = 1, EntryKind EntryKind = EntryKind.Whole, int Members = 0, string? MemberCovers = null,
    string? MatchedMember = null, long? MatchedDocumentId = null, string? AlsoOwn = null, bool Favorite = false, DateTime? OpenedUtc = null,
    TextAccess TextAccess = TextAccess.Readable)
{
    public bool IsPack => EntryKind == EntryKind.Pack;

    /// <summary>A book owned elsewhere, with no file in the library: nothing to open.</summary>
    public bool IsElsewhere => EntryKind == EntryKind.Elsewhere;

    /// <summary>
    /// How many of a pack's files are PDFs, packed by hand with its images: a pack with none is an image pack ("120
    /// images"), one with some a pack of files ("6 files").
    /// </summary>
    public int PdfMembers { get; init; }

    /// <summary>
    /// The file chosen as a pack's cover, while it is in the pack and its cover is drawn: the card shows that file's
    /// cover (<see cref="Cover"/>) and has no mosaic.
    /// </summary>
    public EntryId? CoverMember { get; init; }

    /// <summary>The cover file names of a pack's first four files that have one; none when <see cref="CoverMember"/> is set.</summary>
    public IReadOnlyList<string> MosaicCovers => MemberCovers is null ? [] : JsonSerializer.Deserialize<List<string>>(MemberCovers) ?? [];

    /// <summary>An adventure inside a file: a card of its own for some of the file's pages.</summary>
    public bool IsPart => EntryKind == EntryKind.Part;

    /// <summary>
    /// A file in a pack, shown on a card of its own while the Library shows each file (<see
    /// cref="LibraryFilter.PackFiles"/>): the file's own entry. <see cref="EntryId"/> stays its pack's, whose metadata,
    /// marks and details the card has, while <see cref="DocumentId"/>, the title, cover and format are the file's.
    /// </summary>
    public EntryId? PackFile { get; init; }

    public bool IsPackFile => PackFile is not null;

    /// <summary>For a file in a pack on a card of its own, the pack's title.</summary>
    public string? PackTitle { get; init; }

    /// <summary>What tells cards apart: the entry, or for a file in a pack on a card of its own, the file's entry.</summary>
    public EntryId CardId => PackFile ?? EntryId;

    /// <summary>For an adventure inside a file, the file's entry.</summary>
    public EntryId? ParentEntryId { get; init; }

    /// <summary>For an adventure inside a file, the title of the file it is in.</summary>
    public string? ParentTitle { get; init; }

    /// <summary>For an adventure inside a file, its first and last PDF pages in <see cref="DocumentId"/>.</summary>
    public PartPages? Pages { get; init; }

    /// <summary>For an adventure inside a file, the printed labels of its first and last pages, when the file has them.</summary>
    public string? FirstLabel { get; init; }

    public string? LastLabel { get; init; }

    /// <summary>For a file, how many adventures are marked in it: "5 adventures".</summary>
    public int Parts { get; init; }

    /// <summary>
    /// The book's page where the user owns it online, from its first Also own place with a link ("D&amp;D Beyond"): a
    /// card owned elsewhere opens it, and any card offers "Open on D&amp;D Beyond".
    /// </summary>
    public string? OnlineLink { get; init; }

    /// <summary>The place <see cref="OnlineLink"/> is on: "D&amp;D Beyond".</summary>
    public string? OnlineLabel { get; init; }

    /// <summary>
    /// An adventure a search found while adventures stay inside their books: its card shows its file's cover (<see
    /// cref="Cover"/>) with an Adventure ribbon, and stands in for its file's card.
    /// </summary>
    public bool FoundInBook { get; init; }

    /// <summary>How many players it is written for, as cards show it ("4–6 players"), when someone has said.</summary>
    public string? Players { get; init; }

    /// <summary>"in The Big Compilation · pp. 34–52", for an adventure's card; null for any other.</summary>
    public string? PartLine => Pages is { } pages ? $"in {ParentTitle ?? "its file"} · {pages.Describe(FirstLabel, LastLabel)}" : null;
}

/// <summary>
/// A file in a pack, an image or a PDF, in file name order: the document it shows, its own entry, its name, its format
/// and its cover.
/// </summary>
public sealed record PackImage(long DocumentId, EntryId MemberEntryId, string Name, string Format, string? Cover);

public enum LibrarySort
{
    /// <summary>Best match first; for a library with no search, the same as <see cref="RecentlyAdded"/>.</summary>
    Relevance,
    RecentlyAdded,
    Title,
    /// <summary>By publisher, documents without one last, then by title.</summary>
    Publisher,
    /// <summary>The last opened first, then those never opened, newest first.</summary>
    RecentlyOpened,
}

/// <summary>Kind choices in the filter panel. The format: field gives finer control.</summary>
public enum KindFilter
{
    All,
    /// <summary>PDFs.</summary>
    Books,
    /// <summary>Single images, not those in packs.</summary>
    Images,
    Packs,
    /// <summary>Books owned elsewhere, with no file.</summary>
    Elsewhere,
    /// <summary>Everything with a file on this PC: books, images and packs, but no book owned only elsewhere.</summary>
    Here,
}

/// <summary>The AI choice in the filter panel: books a model has read, or those it hasn't yet.</summary>
public enum AiFilter
{
    All,
    Read,
    NotRead,
}

/// <summary>The Collection choice in the filter panel: books in some collection, or in none yet.</summary>
public enum CollectionFilter
{
    All,
    InOne,
    NotInOne,
}

/// <summary>
/// What the library shows: the entries in scope (from catalog.db: with a file present in a folder the user hasn't
/// removed, optionally one folder), a format, game systems and document types (any of those chosen; unknown is <see
/// cref="SearchQuery.Unknown"/>), a level, and an order. A level matches books whose range contains it; books with
/// unknown levels match only with <see cref="IncludeUnknownLevel"/>. <see cref="Ai"/> keeps books a model has or hasn't
/// read, <see cref="OnlyWithCopies"/> those with more than one file, and <see cref="Owns"/> those owned in any of those
/// places (Also own term keys; unknown is none). <see cref="Group"/> keeps the entries in one group of entry_scope,
/// such as the favourites, and <see cref="OnlyOpened"/> those that have been opened. <see cref="Styles"/> and <see
/// cref="Durations"/> keep books with any of those terms (unknown is none), and <see cref="Players"/> books written for
/// that many players; as for levels, books nobody has counted players for don't match. <see cref="Stores"/> keeps books
/// to get from any of those stores. <see cref="HideParts"/> leaves out the adventures inside files ("Adventures inside
/// files: Hide"), whose page hits then go with their files. <see cref="Collections"/> keeps books in some collection, or
/// those in none yet. <see cref="Picked"/> holds the values picked in the type-ahead filters (settings, themes,
/// publishers): books with any of a field's values, in every field with some. <see cref="PartsInBooks"/> keeps the
/// adventures inside files in their books instead of hiding them: the library without a search leaves them out, as
/// their files' cards and details list them, while a search shows those it finds, each on its file's cover and in
/// place of its file's card when both are found. <see cref="PackFiles"/> shows each file in a pack on a card of its own
/// instead of the pack's card; a file is filtered and found as its pack is, and by its own name. <see cref="Found"/>
/// keeps books found by any of those catalogues (<see cref="FoundOnline"/>; unknown is found by none).
/// </summary>
public sealed record LibraryFilter(
    IReadOnlyCollection<EntryId>? Scope = null,
    KindFilter Kind = KindFilter.All,
    LibrarySort Sort = LibrarySort.Relevance,
    IReadOnlyCollection<string>? Systems = null,
    IReadOnlyCollection<string>? Types = null,
    int? Level = null,
    bool IncludeUnknownLevel = false,
    AiFilter Ai = AiFilter.All,
    bool OnlyWithCopies = false,
    IReadOnlyCollection<string>? Owns = null,
    string? Group = null,
    bool OnlyOpened = false,
    IReadOnlyCollection<string>? Styles = null,
    IReadOnlyCollection<string>? Durations = null,
    int? Players = null,
    IReadOnlyCollection<string>? Stores = null,
    bool HideParts = false,
    CollectionFilter Collections = CollectionFilter.All,
    PickedValues? Picked = null,
    bool PartsInBooks = false,
    bool PackFiles = false,
    IReadOnlyCollection<string>? Found = null);

/// <summary>How many entries have a value, for the filter panel. <see cref="Value"/> is <see cref="SearchQuery.Unknown"/> for those with none.</summary>
public sealed record FacetCount(string Value, string Label, long Count);

/// <summary>
/// A value a type-ahead filter offers, with how many entries have it, and every other name it goes by in comparison
/// form, joined with " · " (a term's aliases), so typing "5e" finds the 5th edition.
/// </summary>
public sealed record ValueCount(string Value, string Label, long Count, string Names);

/// <summary>A page that matched, with its indexed text quoted around the hits. Hits sit between <see cref="LibraryQueries.HitStart"/> and <see cref="LibraryQueries.HitEnd"/>.</summary>
public sealed record PageHit(EntryId EntryId, long DocumentId, int PdfPage, string? Label, string Snippet, bool FromOcr);

/// <summary>A page ranked outside the database, for <see cref="LibraryQueries.RankedPagesAsync"/>, with the snippet to show for it.</summary>
public sealed record RankedPageHit(long PageId, string Snippet);

public sealed record EntryHits(LibraryEntry Entry, IReadOnlyList<PageHit> Pages, long MatchingPages);

/// <summary>Inside-documents results: the best pages of the best books, and how many matched in all.</summary>
public sealed record PageResults(IReadOnlyList<EntryHits> Entries, long MatchingEntries, long MatchingPages)
{
    public static PageResults None { get; } = new([], 0, 0);
}

public sealed record StageState(Stage Stage, StageStatus Status, string? Reason);

/// <summary>What the inspector shows from index.db.</summary>
public sealed record DocumentDetails(
    LibraryEntry Entry, bool Encrypted, bool CanCopy, string? MetaTitle, string? MetaAuthor, string? MetaSubject,
    int? WidthPx, int? HeightPx, long OcrPages, long PagesAwaitingOcr, IReadOnlyList<StageState> Stages)
{
    /// <summary>The ISBNs and product codes the file prints about itself, in the order found.</summary>
    public IReadOnlyList<PrintedIdentifier> Identifiers { get; init; } = [];
}

/// <summary>
/// An ISBN or product code as the book prints it, and the page it is on as the book numbers it (its printed label, else
/// its page number from 1), or null when it came from an e-book's package.
/// </summary>
public sealed record PrintedIdentifier(IdentifierKind Kind, string Printed, string? Page);
