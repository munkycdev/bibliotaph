namespace Bibliotaph.Api.Contracts;

/// <summary>
/// One of Nornis's world events, as POST /api/nornis/v1/worlds/events takes them, up to 100 at a time: a world the user
/// created was created, renamed or deleted, or its files changed. <see cref="OccurredAt"/> orders a world's events.
/// </summary>
public sealed record NornisWorldEvent(Guid EventId, string Kind, Guid WorldId, string WorldName, DateTimeOffset OccurredAt)
{
    public const string Created = "created";
    public const string Renamed = "renamed";
    public const string Deleted = "deleted";
    public const string FilesChanged = "files-changed";

    public const int MostInBatch = 100;
    public const int LongestName = 200;

    /// <summary>Whether it says everything an event must: known kind, IDs, a name of 1 to 200 characters and a time.</summary>
    public bool IsComplete =>
        EventId != Guid.Empty && WorldId != Guid.Empty && Kind is Created or Renamed or Deleted or FilesChanged
        && WorldName is { Length: > 0 and <= LongestName } && !string.IsNullOrWhiteSpace(WorldName) && OccurredAt != default;
}

/// <summary>
/// A book in a world's collection, as GET /api/nornis/v1/worlds/{worldId}/books lists it: the document the reader
/// opens, its type and system as vocabulary keys, and its cover by name. <see cref="NornisFileId"/> is set when the
/// file is one uploaded to Nornis.
/// </summary>
public sealed record NornisBook(long EntryId, long DocumentId, string Title, string? Subtitle, string? Type, string? System, int? PageCount,
    string Format, string? CoverName, string? NornisFileId = null);

/// <summary>The books in a world's collection and the collections inside it.</summary>
public sealed record NornisBooks(IReadOnlyList<NornisBook> Books);

/// <summary>
/// A question whose pages Nornis wants: at most 1,000 characters, 1 to 10 pages (10 if not said), only these books if
/// given, and ask (for the Loremaster, nothing when the signals are weak) or search (the GM choosing excerpts, the
/// closest pages anyway).
/// </summary>
public sealed record NornisPassageQuestion(string Question, int? Limit = null, IReadOnlyList<long>? EntryIds = null, string? Mode = null)
{
    public const string Ask = "ask";
    public const string Search = "search";
    public const int LongestQuestion = 1_000;
    public const int MostPages = 10;
}

/// <summary>A page found for a question: its book, its zero-based page and label as the reader shows it, its text and score.</summary>
public sealed record NornisPassage(long EntryId, long DocumentId, string Title, int PageIndex, string PageLabel, string Text, double Score);

/// <summary>
/// The pages found: whether they answer it (false, with none, when ask's signals were weak), whether meaning was used
/// as well as words, and the pages best first.
/// </summary>
public sealed record NornisPassages(bool Covered, bool MeaningUsed, IReadOnlyList<NornisPassage> Pages);

/// <summary>A page's indexed text, OCR text for a scanned page, with its zero-based index and its label as the reader shows it.</summary>
public sealed record NornisPageText(int PageIndex, string PageLabel, string Text);

/// <summary>Pages of one document, at most 40 at a time.</summary>
public sealed record NornisPageTexts(long DocumentId, IReadOnlyList<NornisPageText> Pages)
{
    public const int MostPages = 40;
}
