namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A book's details, as GET /api/library/books/{entryId} gives them: what the Windows app's details drawer shows, in
/// its words and order. Lists the book has nothing for are empty, and the client leaves their sections out.
/// <see cref="HasChosenCover"/> is a book owned elsewhere showing a picture the user chose, which can be taken away.
/// </summary>
public sealed record BookDetails(
    BookCard Card, string Eyebrow, string Publisher, string Pages, bool CanOpen, IReadOnlyList<DetailField> Fields, IReadOnlyList<DetailPart> Parts,
    string PartsHeading, bool CanMarkParts, string? PartOf, long? PartOfEntryId, IReadOnlyList<PackFile> PackFiles, string PackFilesHeading,
    string FactsHeading, IReadOnlyList<DetailFact> Facts, bool ShowsLocations, IReadOnlyList<DetailLocation> Locations, IReadOnlyList<DetailCopy> Copies,
    string CopiesHeading, IReadOnlyList<DetailFact> Stages, bool Reprocessing, string NotesLabel, string FirstPage, string PartFrom, string PartTo,
    string SplitPackLabel, string SplitPackTip, IReadOnlyList<CollectionChip> Collections, IReadOnlyList<DetailChapter> Chapters,
    bool HasChosenCover = false);

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

/// <summary>Where a file is: the drive and its folders, and what stops it being read ("No longer here"), if anything.</summary>
public sealed record DetailLocation(string Path, string State);

/// <summary>One of a book's files when it has more than one: "Current copy", its pages and when it was added, and where it is.</summary>
public sealed record DetailCopy(long DocumentId, string Heading, string Detail, string? Path, bool IsCurrent);

/// <summary>An adventure marked in a file: its own card, and its pages there, the first and last as the file numbers them.</summary>
public sealed record DetailPart(long EntryId, string Title, string Pages, string From, string To);

/// <summary>A collection the book was added to itself, as a chip: its name, and its path for the chip's tip.</summary>
public sealed record CollectionChip(long Id, string Name, string Path);

/// <summary>A file in a pack, with its cover's name for /api/library/covers/.</summary>
public sealed record PackFile(long EntryId, string Name, string? Cover, bool IsPdf, bool IsCover);
