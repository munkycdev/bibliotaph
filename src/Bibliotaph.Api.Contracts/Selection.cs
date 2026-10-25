namespace Bibliotaph.Api.Contracts;

/// <summary>The books ticked in Select mode, and how many of them the results don't show.</summary>
public sealed record SelectionQuery(IReadOnlyList<long> EntryIds, int NotShown);

/// <summary>
/// What the Select bar says, the ticked books as a change's note names them ("3 books"), and which of Same book, Pack
/// together and Remove adventures fit them.
/// </summary>
public sealed record SelectionInfo(string Summary, string Books, bool CanJoin, bool CanPack, bool CanRemoveAdventures);

/// <summary>The ticked books, for an action on them all.</summary>
public sealed record SelectedBooks(IReadOnlyList<long> EntryIds);

/// <summary>One file in the Same book dialog: the card it is on now, its name, and its pages and when it was added.</summary>
public sealed record SameBookChoice(long DocumentId, string Card, string Name, string Detail);

/// <summary>The Same book dialog: its heading, what happens to a book owned elsewhere, and the file that opens unless another is picked.</summary>
public sealed record SameBookPreview(string Heading, string? Elsewhere, IReadOnlyList<SameBookChoice> Files, long Opens);

public sealed record SameBookJoin(IReadOnlyList<long> EntryIds, long Opens);

/// <summary>The Pack together dialog: what it makes, and the pack's name to start from.</summary>
public sealed record PackTogetherPreview(string Eyebrow, string Heading, string Explanation, string Name);

public sealed record PackTogetherRequest(IReadOnlyList<long> EntryIds, string Name);

/// <summary>Export these: the CSV, the name to save it as, and what the note says once it is saved.</summary>
public sealed record ExportedCsv(string Name, string Csv, string Message);
