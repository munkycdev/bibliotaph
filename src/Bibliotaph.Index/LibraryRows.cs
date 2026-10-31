using System.Globalization;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Index;

/// <summary>The rows index.db gives for cards, page hits and OCR words, and the cards made from them.</summary>
internal static class LibraryRows
{
    /// <summary>
    /// The cards of these rows. With <paramref name="partsInBooks"/>, an adventure found shows its file's cover rather
    /// than its own first page, so it reads as part of that book.
    /// </summary>
    public static List<LibraryEntry> ToEntries(IEnumerable<EntryRow> rows, bool partsInBooks = false) =>
        [.. rows.Select(r => new LibraryEntry(new EntryId(r.EntryId), r.DocumentId, r.Title, r.Format, (int?)r.PageCount,
            partsInBooks && r.EntryKind == nameof(EntryKind.Part) ? r.FileCover ?? r.Cover : r.Cover, r.FolderHint,
            r.AddedUtc.Length == 0 ? DateTime.MinValue : Utc(r.AddedUtc),
            r.Searchable != 0, r.SystemLabel, r.KindLabel, r.Publisher, DescribeLevels(r.LevelState, r.LevelMin, r.LevelMax),
            r.NeedsReview != 0, r.Suggested != 0, r.AiModel, (int)r.Copies, Enum.Parse<EntryKind>(r.EntryKind), (int)r.Members, r.MemberCovers,
            r.MatchedMember, r.MatchedDocumentId, r.AlsoOwn, r.Favorite != 0, r.OpenedUtc is { Length: > 0 } opened ? Utc(opened) : null,
            // A pack shows its first file, whose lock or protection is that file's, not the pack's.
            r.EntryKind == nameof(Core.EntryKind.Pack) ? TextAccess.Readable : Access(r))
        {
            PdfMembers = (int)r.PdfMembers,
            CoverMember = r.CoverMember is { } coverMember ? new EntryId(coverMember) : null,
            ParentEntryId = r.ParentId is { } parent ? new EntryId(parent) : null,
            ParentTitle = r.ParentTitle,
            Pages = r.FirstPage is { } first && r.LastPage is { } last ? new PartPages((int)first, (int)last) : null,
            FirstLabel = r.FirstLabel,
            LastLabel = r.LastLabel,
            Parts = (int)r.Parts,
            OnlineLink = r.OnlineLink,
            OnlineLabel = r.OnlineLabel,
            FoundInBook = partsInBooks && r.EntryKind == nameof(EntryKind.Part),
            Players = r.PlayersMin is { } fewest && r.PlayersMax is { } most ? new PlayerRange((int)fewest, (int)most).Describe() : null,
            PackFile = r.PackFile is { } file ? new EntryId(file) : null,
            PackTitle = r.PackTitle,
        })];

    /// <summary>
    /// Whether a card's text is locked, protected or forgotten, from how its document's Probe stage stopped, or its Text
    /// stage for a book whose password went after Probe had read it.
    /// </summary>
    public static TextAccess Access(EntryRow r) =>
        TextAccessReasons.Of(Status(r.ProbeStatus), r.ProbeReason) is var probe and not TextAccess.Readable ? probe
        : TextAccessReasons.Of(Status(r.TextStatus), r.TextReason);

    public static StageStatus? Status(string? status) => status is null ? null : Enum.Parse<StageStatus>(status);

    public static DateTime Utc(string timestamp) => DateTime.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public static string? DescribeLevels(string state, long? min, long? max) => state switch
    {
        "na" => LevelRange.None.Describe(),
        "known" when min is { } low && max is { } high => new LevelRange((int)low, (int)high).Describe(),
        _ => null,
    };

    // Settable properties rather than constructors: SQLite reports no type for an expression column in an empty
    // result, and Dapper's constructor matching then fails where property mapping converts.

    public sealed class WordRow
    {
        public string Text { get; init; } = "";
        public double LeftPt { get; init; }
        public double TopPt { get; init; }
        public double RightPt { get; init; }
        public double BottomPt { get; init; }
    }

    public sealed class EntryRow
    {
        public long EntryId { get; init; }
        public long DocumentId { get; init; }
        public string Title { get; init; } = "";
        public string Format { get; init; } = "";
        public long? PageCount { get; init; }
        public string? Cover { get; init; }
        public string? FolderHint { get; init; }
        public string AddedUtc { get; init; } = "";
        public long Searchable { get; init; }
        public string? TextStatus { get; init; }
        public string? TextReason { get; init; }
        public string? ProbeStatus { get; init; }
        public string? ProbeReason { get; init; }
        public string? SystemLabel { get; init; }
        public string? KindLabel { get; init; }
        public string? Publisher { get; init; }
        public long? LevelMin { get; init; }
        public long? LevelMax { get; init; }
        public string LevelState { get; init; } = "unknown";
        public long? PlayersMin { get; init; }
        public long? PlayersMax { get; init; }
        public long NeedsReview { get; init; }
        public long Suggested { get; init; }
        public string? AiModel { get; init; }
        public long Copies { get; init; } = 1;
        public string EntryKind { get; init; } = nameof(Core.EntryKind.Whole);
        public long Members { get; init; }
        public string? MemberCovers { get; init; }
        public long PdfMembers { get; init; }
        public long? CoverMember { get; init; }
        public string? MatchedMember { get; init; }
        public long? MatchedDocumentId { get; init; }
        public string? AlsoOwn { get; init; }
        public string? OnlineLink { get; init; }
        public string? OnlineLabel { get; init; }
        public long Favorite { get; init; }
        public string? OpenedUtc { get; init; }
        public long? ParentId { get; init; }
        public long? FirstPage { get; init; }
        public long? LastPage { get; init; }
        public long Parts { get; init; }
        public string? ParentTitle { get; init; }
        public string? FirstLabel { get; init; }
        public string? LastLabel { get; init; }
        public string? FileCover { get; init; }
        public long? PackFile { get; init; }
        public string? PackTitle { get; init; }
    }

    public sealed class HitRow
    {
        public long PageId { get; init; }
        public long DocumentId { get; init; }
        public long PdfPage { get; init; }
        public string? Label { get; init; }
        public string Source { get; init; } = "";
        public string Snippet { get; init; } = "";
    }
}
