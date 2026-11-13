namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A book's details, as GET /api/library/books/{entryId} gives them: what the Windows app's details drawer shows, in
/// its words and order. Lists the book has nothing for are empty, and the client leaves their sections out.
/// <see cref="HasChosenCover"/> is a book owned elsewhere showing a picture the user chose, which can be taken away.
/// <see cref="CanAsk"/> offers Ask this book: a PDF or e-book whose text can be read, while AI is on.
/// <see cref="FromRpgGeek"/> is a book some of whose details came from RPGGeek, which the details credit to BoardGameGeek.
/// <see cref="RpgGeek"/> is the record there the book uses, and <see cref="RpgGeekOffer"/> the label of the action that
/// gives it one by its page's address ("Look up from an RPGGeek link…"), null where it can't be given one.
/// <see cref="Lookup"/> is what its last online lookup searched for and what came of it, under
/// <see cref="LookupHeading"/>; none for a book not looked up.
/// </summary>
public sealed record BookDetails(
    BookCard Card, string Eyebrow, string Publisher, string Pages, bool CanOpen, IReadOnlyList<DetailField> Fields, IReadOnlyList<DetailPart> Parts,
    string PartsHeading, bool CanMarkParts, string? PartOf, long? PartOfEntryId, IReadOnlyList<PackFile> PackFiles, string PackFilesHeading,
    string FactsHeading, IReadOnlyList<DetailFact> Facts, bool ShowsLocations, IReadOnlyList<DetailLocation> Locations, IReadOnlyList<DetailCopy> Copies,
    string CopiesHeading, IReadOnlyList<DetailFact> Stages, bool Reprocessing, string NotesLabel, string FirstPage, string PartFrom, string PartTo,
    string SplitPackLabel, string SplitPackTip, IReadOnlyList<CollectionChip> Collections, IReadOnlyList<DetailChapter> Chapters,
    bool CanAsk, bool HasChosenCover = false, string? OcrEveryPageQuestion = null, bool FromRpgGeek = false,
    DetailRpgGeek? RpgGeek = null, string? RpgGeekOffer = null, string RpgGeekHint = "", IReadOnlyList<DetailFact>? Lookup = null,
    string LookupHeading = "", DetailGoesWith? GoesWith = null);

/// <summary>
/// Goes with: the book a map, handout, character sheet or pack goes with (<see cref="Book"/>), the books proposed for
/// it, and, on a book, the cards that go with it. <see cref="CanGoWith"/> says whether the card can go with a book.
/// </summary>
public sealed record DetailGoesWith(bool CanGoWith, DetailSourceBook? Book, IReadOnlyList<BookChoice> Proposed, IReadOnlyList<DetailCompanion> Companions,
    string CompanionsHeading);

/// <summary>The book a card goes with: its title, how Bibliotaph knew, and whether the user set or kept it.</summary>
public sealed record DetailSourceBook(long EntryId, string Title, string How, bool Confirmed);

/// <summary>A card that goes with a book, as the book's details list it.</summary>
public sealed record DetailCompanion(long EntryId, string Title, string How);

/// <summary>A book a card can go with, as the picker and the proposals list it.</summary>
public sealed record BookChoice(long EntryId, string Title, string Detail);

/// <summary>The picker's search: the words typed, and the cards it never offers (those that would go with the book).</summary>
public sealed record BookSearch(string Text, IReadOnlyList<long> Not);

/// <summary>The book a card goes with now, chosen by the user.</summary>
public sealed record SourceBookEdit(long Book);

/// <summary>The book's record on RPGGeek: <see cref="Text"/> says what it's called there, and <see cref="Url"/> is its page.</summary>
public sealed record DetailRpgGeek(string Text, string Url);

/// <summary>
/// A chapter under Found inside the book: its title and pages as the file numbers them, one line per field the AI
/// found something for ("Creatures: Goblin, Ogre"), or why there is nothing.
/// </summary>
public sealed record DetailChapter(string Title, string Pages, IReadOnlyList<string> Lines, string? Note);

/// <summary>
/// One field under Details: its values as they read, where each came from and the other suggestions, and how it is
/// edited. <see cref="Primary"/> fields show even when nothing is known; the rest wait behind More fields. <see
/// cref="Choices"/> are a term field's vocabulary, to pick from.
/// </summary>
public sealed record DetailField(
    string Key, string Label, bool Primary, string Display, bool Known, bool Suggested, bool NeedsReview, string EvidenceLabel, bool CanReset,
    IReadOnlyList<DetailValue> Values, IReadOnlyList<DetailValue> Alternatives, bool Multiple, string EditHint, string ChoicesLabel,
    IReadOnlyList<string> Choices);

/// <summary>A value with its sources: "From the folder name “D&amp;D 5e”", "You set this". <see cref="Url"/> is a store's link.</summary>
public sealed record DetailValue(string Text, string Value, string Normalized, bool Confirmed, string? Url, string? LinkText, IReadOnlyList<string> Sources);

public sealed record DetailFact(string Label, string Value);

/// <summary>
/// Where a file is: the storage service it's kept in ("OneDrive"), the account and its folders, and what stops it being
/// read ("No longer here"), if anything.
/// </summary>
public sealed record DetailLocation(string Path, string State, string? Service = null);

/// <summary>One of a book's files when it has more than one: "Current copy", its pages and when it was added, and where it is.</summary>
public sealed record DetailCopy(long DocumentId, string Heading, string Detail, string? Path, bool IsCurrent, string? Service = null);

/// <summary>An adventure marked in a file: its own card, and its pages there, the first and last as the file numbers them.</summary>
public sealed record DetailPart(long EntryId, string Title, string Pages, string From, string To);

/// <summary>A collection the book was added to itself, as a chip: its name, and its path for the chip's tip.</summary>
public sealed record CollectionChip(long Id, string Name, string Path);

/// <summary>A file in a pack, with its cover's name for /api/library/covers/.</summary>
public sealed record PackFile(long EntryId, string Name, string? Cover, bool IsPdf, bool IsCover);
