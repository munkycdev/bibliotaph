using Bibliotaph.Core;

namespace Bibliotaph.Catalog.Entities;

// Persistence shapes for catalog.db. Slice 0 created the core tables; slice 2 adds vocabulary, rejections and
// classification runs; the foundation slice adds entries, which user work keys on; slice 3 adds favourites and reading
// positions, then collections, smart views, session packs and notes, each by its own migration; pins come with Pinned Views.
// Domain behaviour lives in Core (Bibliotaph.Core.Metadata for metadata).

/// <summary>A folder the user added. Its files are never marked missing while it is offline.</summary>
public sealed class SourceRoot
{
    public long Id { get; set; }
    public required string Path { get; set; }
    public string? VolumeSerial { get; set; }
    public SourceRootAvailability Availability { get; set; }
    public DateTime AddedUtc { get; set; }
    public List<FileLocation> Files { get; set; } = [];
}

/// <summary>Where some content was last seen. A path is not an identity; the content hash is.</summary>
public sealed class FileLocation
{
    public long Id { get; set; }
    public long SourceRootId { get; set; }
    public SourceRoot SourceRoot { get; set; } = null!;
    public required string RelativePath { get; set; }
    public long SizeBytes { get; set; }
    public DateTime ModifiedUtc { get; set; }
    /// <summary>NTFS file ID, to spot moves without rehashing.</summary>
    public string? NtfsFileId { get; set; }
    /// <summary>Null until the Fingerprint stage has hashed the file.</summary>
    public string? ContentHash { get; set; }
    public long? DocumentId { get; set; }
    public Document? Document { get; set; }
    /// <summary>
    /// The document this path held before its content changed, until the new content is hashed. New content at the
    /// same path is a new version of the same book (A08), so it joins that document's entry.
    /// </summary>
    public long? PreviousDocumentId { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public FileLocationState State { get; set; }

    /// <summary>
    /// For a file inside a ZIP, the ZIP's own location (F3). Its <see cref="RelativePath"/> is then the ZIP's path and
    /// the entry's, as File Explorer shows it, and its state follows the ZIP's. A ZIP's own location has a hash but no document.
    /// </summary>
    public long? ContainerId { get; set; }
    public FileLocation? Container { get; set; }

    /// <summary>The member's name inside its ZIP, exactly as stored there ("Maps/Harbor.jpg").</summary>
    public string? EntryPath { get; set; }

    /// <summary>The member's CRC-32 from the ZIP, so a ZIP saved again keeps the hashes of members that didn't change.</summary>
    public long? EntryCrc32 { get; set; }

    /// <summary>Why a member isn't read (a ZIP inside the ZIP, a password, too big, damaged), for Files needing attention.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// The file was online-only when Bibliotaph read it, so reading it downloaded it: once nothing left needs its
    /// content, it goes back to online-only (Dale's review, row 2). Cleared when it has gone back, or when it changes.
    /// </summary>
    public bool ReturnToOnlineOnly { get; set; }
}

/// <summary>One row per content version.</summary>
public sealed class Document
{
    public long Id { get; set; }
    public required string ContentHash { get; set; }
    /// <summary>pdf, jpg, png and so on.</summary>
    public required string Format { get; set; }
    public int? PageCount { get; set; }
    /// <summary>Capabilities with reasons, as JSON (searchable, copyable, needs OCR, ...).</summary>
    public string? CapabilitiesJson { get; set; }
    public ProtectionType Protection { get; set; }
    /// <summary>
    /// When the user chose "Forget its text" (slice 4i plan, choice 6): its pages, search text, cover and AI results
    /// were removed, and the pipeline leaves it alone until "Read it again" clears this. Kept here, not in index.db,
    /// because it is the user's decision and must survive index.db being rebuilt.
    /// </summary>
    public DateTime? TextForgottenUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<FileLocation> Locations { get; set; } = [];
    public List<EntrySource> Sources { get; set; } = [];
    public List<PageRef> PageRefs { get; set; } = [];
}

/// <summary>
/// A library card (catalog entry design, choice 1). What the user decides about a book (metadata, rejections, review,
/// collections) is about its entry, not about whichever copy of the file they found it in. The documents behind it
/// are its <see cref="Sources"/>; an entry owned elsewhere has none.
/// </summary>
public sealed class Entry
{
    public long Id { get; set; }
    public EntryKind Kind { get; set; }
    /// <summary>
    /// For a part, the compilation's entry. For an image in a pack, the pack: the image keeps its own entry, with its
    /// sources and anything set on it, but shows only inside the pack until the pack is split (F4 plan, choice 5).
    /// </summary>
    public long? ParentEntryId { get; set; }
    public Entry? ParentEntry { get; set; }
    /// <summary>
    /// Set when this entry's copy joined another entry: the entry it joined. It has no sources then and shows nowhere,
    /// but keeps its id and anything that stayed behind, so "Not the same book" can bring it back.
    /// </summary>
    public long? MergedIntoEntryId { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>
    /// Names a book owned elsewhere in the library sync file (library sync plan, choice 4), which has no content hash to
    /// be named by; a book with files is named by its files' hashes instead. Made once, and the same on every PC it reaches.
    /// </summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();
    public List<EntrySource> Sources { get; set; } = [];
    public List<Assertion> Assertions { get; set; } = [];
    public List<Rejection> Rejections { get; set; } = [];
}

/// <summary>
/// A document behind an entry: one copy or version of a book, or, for a part, a page range of one copy of its
/// compilation. One source per entry is current: it opens, and supplies the cover, page count and page hits.
/// </summary>
public sealed class EntrySource
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public Entry Entry { get; set; } = null!;
    public long DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    /// <summary>A part's first PDF page in this document; null for the whole document.</summary>
    public int? FirstPdfPage { get; set; }
    public int? LastPdfPage { get; set; }
    /// <summary>
    /// A part's first and last page-text fingerprints (<see cref="Core.PageFingerprints"/>) in this document, so its
    /// pages can be found in another copy or a new version of the file (slice 5 plan, choice 12); null for the whole
    /// document, or for a page with too little text.
    /// </summary>
    public string? FirstFingerprint { get; set; }
    public string? LastFingerprint { get; set; }
    public bool IsCurrent { get; set; }
}

/// <summary>
/// A copy that joined an entry because its text matched another copy page for page (catalog entry design, choice 5),
/// or because the user said so with Same book, with what moved across, so "Not the same book" can put everything back.
/// A pack merged into another by Pack together (multi-select plan, choice 9) is recorded the same way, with no files.
/// </summary>
public sealed class EntryJoin
{
    public long Id { get; set; }
    /// <summary>The entry the copy joined.</summary>
    public long EntryId { get; set; }
    /// <summary>The copy's own entry before the join, kept with <see cref="Entry.MergedIntoEntryId"/> set.</summary>
    public long JoinedEntryId { get; set; }
    /// <summary>The copy that joined; null for a pack that merged into another.</summary>
    public long? DocumentId { get; set; }
    /// <summary>The copy it matched; null for a pack that merged into another.</summary>
    public long? MatchedDocumentId { get; set; }
    /// <summary>The ids of the assertions, rejections and runs that moved, and of the values set aside, as JSON.</summary>
    public required string MovedJson { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A folder or ZIP whose images were made one pack card, or split again (F4 plan, choices 2 to 5). A ZIP counts as a
/// whole, subfolders included; a folder only for the images directly in it. Remembered by path, so a rescan neither
/// packs a split folder again nor loses the pack's card.
/// </summary>
public sealed class PackDecision
{
    public long Id { get; set; }
    public long SourceRootId { get; set; }
    /// <summary>The folder's path in its root, or the ZIP's ("Tokens\Undead", "Maps\Harbor Set.zip").</summary>
    public required string FolderPath { get; set; }
    public bool IsArchive { get; set; }
    public PackAnswer Answer { get; set; }
    /// <summary>The pack's entry. A split pack keeps it, with anything set on it, in case the folder is packed again.</summary>
    public long EntryId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A file whose title and publisher match a book the user owns elsewhere (F5 plan, choice 6): a Needs review card,
/// "You own X elsewhere. Is this its file?", while <see cref="Answer"/> is null. "Same book" joins the file to the book
/// and the row goes; "Separate book" stays, so the pair is never asked about again.
/// </summary>
public sealed class ElsewhereMatch
{
    public long Id { get; set; }
    /// <summary>The book owned elsewhere.</summary>
    public long EntryId { get; set; }
    /// <summary>The file that may be its.</summary>
    public long DocumentId { get; set; }
    public ElsewhereAnswer? Answer { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// The adventures a file seems to hold (slice 5 plan, choices 5 to 7), proposed from the copy with this content hash:
/// one Needs review card for the file while <see cref="Answer"/> is null. The answer is remembered by the hash, so the
/// same file is never proposed again, whatever reads it later.
/// </summary>
public sealed class PartProposal
{
    public long Id { get; set; }
    public required string ContentHash { get; set; }
    public PartProposalSource Source { get; set; }
    /// <summary>The adventures, in page order, as JSON: title, first and last PDF page, levels and the words that said so.</summary>
    public required string PartsJson { get; set; }
    public PartProposalAnswer? Answer { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
}

/// <summary>
/// The user's answer about a pair of files, by content hash, so Match never asks or joins again: "Not the same book"
/// keeps them on separate cards.
/// </summary>
public sealed class CopyDecision
{
    public long Id { get; set; }
    /// <summary>The lower of the two content hashes.</summary>
    public required string FirstHash { get; set; }
    /// <summary>The higher of the two content hashes.</summary>
    public required string SecondHash { get; set; }
    public CopyAnswer Answer { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A "new version?" card in Needs review (F2 plan, choice 4): a file that looks like a revision of a book already
/// in the library, but isn't the same text page for page. One per file, for its best match; it goes once answered.
/// </summary>
public sealed class VersionProposal
{
    public long Id { get; set; }
    /// <summary>The newer file, which would join the book's card.</summary>
    public long DocumentId { get; set; }
    /// <summary>The file of the book it looks like a new version of.</summary>
    public long MatchedDocumentId { get; set; }
    public VersionEvidence Evidence { get; set; }
    /// <summary>For <see cref="VersionEvidence.SharedPages"/>: how many of the smaller file's fingerprinted pages the other has.</summary>
    public int SharedPages { get; set; }
    /// <summary>For <see cref="VersionEvidence.SharedPages"/>: the smaller file's fingerprinted pages.</summary>
    public int ComparedPages { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// One claimed value for one metadata field, with where it came from and its evidence. The effective value
/// of a field is derived from these rows; nothing overwrites a value in place.
/// </summary>
public sealed class Assertion
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public Entry Entry { get; set; } = null!;
    /// <summary>A <see cref="Core.Metadata.MetadataField"/> key.</summary>
    public required string Field { get; set; }
    /// <summary>The value as a JSON string: text as written, a term's key, a level range ("1-5"), a year.</summary>
    public required string ValueJson { get; set; }
    /// <summary>The value's comparison form, which rejections and duplicate checks match on.</summary>
    public required string NormalizedValue { get; set; }
    public AssertionOrigin Origin { get; set; }
    /// <summary>
    /// The content hash of the copy the value was read from, whose pages <see cref="EvidencePagesJson"/> counts in, so
    /// evidence still checks against the right pages when an entry has several copies. Null for the user's own values.
    /// </summary>
    public string? ContentHash { get; set; }
    /// <summary>PDF page indexes, as a JSON array.</summary>
    public string? EvidencePagesJson { get; set; }
    /// <summary>What the value was read from: a quote from a cited page, a folder name, the PDF's own title.</summary>
    public string? EvidenceQuote { get; set; }
    /// <summary>The value was inferred from sampled pages rather than the whole book.</summary>
    public bool FromSampling { get; set; }
    /// <summary>The classification run that produced it, for AI origins.</summary>
    public string? RunId { get; set; }
    public AssertionState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>When the user confirmed or rejected it, or a later decision superseded it.</summary>
    public DateTime? DecidedUtc { get; set; }
}

/// <summary>
/// A value the user said is wrong for an entry. It suppresses that value from every source, including any later
/// model, so a rejected suggestion never comes back (A04).
/// </summary>
public sealed class Rejection
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public Entry Entry { get; set; } = null!;
    public required string Field { get; set; }
    public required string NormalizedValue { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A term in a vocabulary (game systems, editions, document types, settings, themes, environments, publishers).
/// Starter terms are seeded by version and never overwritten, so the user's renames and aliases stay.
/// </summary>
public sealed class VocabularyTerm
{
    public long Id { get; set; }
    /// <summary>"system", "edition", "type", "duration", "setting", "style", "theme", "environment", "own", "store" or "publisher".</summary>
    public required string Vocabulary { get; set; }
    /// <summary>Stable key that assertions store, such as "dnd-5e". Labels can change; keys don't.</summary>
    public required string Key { get; set; }
    public required string Label { get; set; }
    public string? ShortLabel { get; set; }
    /// <summary>For an edition, its system's key.</summary>
    public string? ParentKey { get; set; }
    public TermOrigin Origin { get; set; }
    public TermState State { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<VocabularyAlias> Aliases { get; set; } = [];
}

/// <summary>Another name for a term: "5e" and "D&amp;D 5e" for the 5th edition.</summary>
public sealed class VocabularyAlias
{
    public long Id { get; set; }
    public long TermId { get; set; }
    public VocabularyTerm Term { get; set; } = null!;
    public required string Text { get; set; }
    public required string Normalized { get; set; }
}

/// <summary>
/// One classifier run over one copy of an entry: which model, which prompt and schema, and which pages of that copy
/// it read. Kept in catalog.db with the assertions it produced, so an index.db rebuild neither loses their provenance
/// nor re-runs the model (slice 2 plan, choice 5).
/// </summary>
public sealed class ClassificationRun
{
    public required string Id { get; set; }
    public long EntryId { get; set; }
    public Entry Entry { get; set; } = null!;
    /// <summary>The copy the model read.</summary>
    public required string ContentHash { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public int PromptVersion { get; set; }
    public int SchemaVersion { get; set; }
    /// <summary>PDF page indexes the model read, as a JSON array.</summary>
    public string? PagesJson { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Null while running; otherwise "complete" or why it failed.</summary>
    public string? Outcome { get; set; }
}

/// <summary>
/// "You said this book was cataloged wrong" (scenario review, row 8): the user opened the editor with Cataloged wrong…
/// and closed it without changing or confirming anything, so Needs review keeps a card that opens it again.
/// </summary>
public sealed class CorrectionRequest
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A folder rule (scenario review, row 8): files in one folder of a library folder, and its subfolders, get a field's
/// value as a suggestion. Proposed after the user set that value on several books from the folder; each answer is
/// kept, so a declined rule isn't proposed again.
/// </summary>
public sealed class FolderRule
{
    public long Id { get; set; }
    public long SourceRootId { get; set; }
    /// <summary>The folder's path under the library folder, as the file locations spell it; "" for the library folder itself.</summary>
    public required string Folder { get; set; }
    public required string Field { get; set; }
    /// <summary>The value in stored form: a term's key, "1-5", text as written.</summary>
    public required string Value { get; set; }
    public FolderRuleState State { get; set; }
    /// <summary>How many books from the folder the user had set it on when it was proposed.</summary>
    public int Books { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
}

/// <summary>A folder label the user switched off in Settings > Library: that folder name no longer suggests that term.</summary>
public sealed class IgnoredFolderLabel
{
    public long Id { get; set; }
    /// <summary>The folder name in comparison form.</summary>
    public required string Folder { get; set; }
    public required string Vocabulary { get; set; }
    public required string TermKey { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// A page or page range that user work points at (session pack items; page notes in 3f), in one document. The printed
/// labels and the page-text fingerprints of its first and last pages are kept from when it was made, so it can be
/// found again in another copy or version of the book, and checked when the book changes (slice 3 plan, choice 14).
/// </summary>
public sealed class PageRef
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    /// <summary>Zero-based PDF page indexes, inclusive.</summary>
    public int FirstPdfPage { get; set; }
    public int LastPdfPage { get; set; }
    /// <summary>The printed labels of the first and last pages, when the file had its own ("42").</summary>
    public string? FirstLabel { get; set; }
    public string? LastLabel { get; set; }
    /// <summary>Page-text fingerprints (<see cref="Core.PageFingerprints"/>); null for a page with too little text.</summary>
    public string? FirstFingerprint { get; set; }
    public string? LastFingerprint { get; set; }

    /// <summary>
    /// What was found when the pages were looked for in <see cref="CheckDocumentId"/>, the version of the book that opens
    /// instead of <see cref="DocumentId"/> (slice 4h plan, choice 4); null until they have been looked for.
    /// </summary>
    public PageCheck? Check { get; set; }
    public long? CheckDocumentId { get; set; }
    /// <summary>Where the pages are in that version, or the same printed pages there when they weren't found.</summary>
    public int? CheckFirstPdfPage { get; set; }
    public int? CheckLastPdfPage { get; set; }
}

/// <summary>
/// A collection (slice 3 plan, choice 8): a named group of entries, inside another collection or at the top. A book can
/// be in any number of collections; deleting one removes only the grouping.
/// </summary>
public sealed class CollectionNode
{
    public long Id { get; set; }
    /// <summary>The collection this one sits in, or null at the top.</summary>
    public long? ParentId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>When a book was last added to it or it was made: the menu's recent collections.</summary>
    public DateTime UsedUtc { get; set; }
    /// <summary>Names it in the library sync file (library sync plan, choice 4): made once, on the PC that made it, and the same on every PC it reaches.</summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();
}

/// <summary>
/// A saved Smart View (slice 3 plan, choice 17): a name and what the Library showed, as JSON (version, search text,
/// filters, scope, order, layout and tab). Never a list of books: opening it runs the search again.
/// </summary>
public sealed class SmartView
{
    public long Id { get; set; }
    public required string Name { get; set; }
    public required string Definition { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Names it in the library sync file (library sync plan, choice 4): made once, on the PC that made it, and the same on every PC it reaches.</summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();
}

/// <summary>
/// A pin in the sidebar's Pinned group (Pinned Views plan, choice 3), in the order the user put them. Each kind but
/// Favorites points at its target through its own column, so deleting the target deletes the pin.
/// </summary>
public sealed class Pin
{
    public long Id { get; set; }
    public PinKind Kind { get; set; }
    /// <summary>Its place in the list, lowest first. Gaps are fine; ties go by id.</summary>
    public int Position { get; set; }
    public long? EntryId { get; set; }
    public long? CollectionId { get; set; }
    /// <summary>The binder's session pack.</summary>
    public long? PackId { get; set; }
    public long? SmartViewId { get; set; }
}

/// <summary>
/// A note (slice 3 plan, choice 18): plain text on an entry, or with a page reference, on pages of one of its
/// documents. An entry has at most one note of its own; page notes are any number.
/// </summary>
public sealed class Note
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    /// <summary>The pages the note is on; null for the entry's own note.</summary>
    public long? PageRefId { get; set; }
    public PageRef? PageRef { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>Names it in the library sync file (library sync plan, choice 4): made once, on the PC that made it, and the same on every PC it reaches.</summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();
}

/// <summary>An entry added to a collection (choice 10).</summary>
public sealed class CollectionItem
{
    public long CollectionId { get; set; }
    public long EntryId { get; set; }
    public DateTime AddedUtc { get; set; }
}

/// <summary>A book marked with a heart (slice 3 plan, choice 5).</summary>
public sealed class Favorite
{
    public long EntryId { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>
/// When a book was last opened, and where its reader left it (choice 6). The page belongs to the document that was
/// read, so a book whose current copy changed opens that copy at its start.
/// </summary>
public sealed class ReadingState
{
    public long EntryId { get; set; }
    public long DocumentId { get; set; }
    /// <summary>The zero-based PDF page in view when the reader last closed the book from an ordinary open.</summary>
    public int PageIndex { get; set; }
    public DateTime OpenedUtc { get; set; }
}

/// <summary>
/// A session pack (slice 3 plan, choice 11): what a game master gathers for one session, in their order, with notes.
/// The pack touched last is the current one, which Add to session adds to.
/// </summary>
public sealed class SessionPack
{
    public long Id { get; set; }
    public required string Title { get; set; }
    /// <summary>When the session is played, if set: the Sessions page lists upcoming ones by it.</summary>
    public DateOnly? Date { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; }
    /// <summary>When it was last opened or changed.</summary>
    public DateTime TouchedUtc { get; set; }
    /// <summary>A binder of rules and references to look up mid-game (Binders plan, choice 9): At hand offers its pages.</summary>
    public bool KeepAtHand { get; set; }
    /// <summary>The game system its references are for, as a system vocabulary key; null for any system.</summary>
    public string? GameSystem { get; set; }
    /// <summary>Names it in the library sync file (library sync plan, choice 4): made once, on the PC that made it, and the same on every PC it reaches.</summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();
}

/// <summary>A heading in a pack ("Maps"), in the pack's order. Items without one come first.</summary>
public sealed class SessionSection
{
    public long Id { get; set; }
    public long PackId { get; set; }
    public required string Name { get; set; }
    public int Position { get; set; }
    /// <summary>Names it in the library sync file (library sync plan, choice 4): made once, on the PC that made it, and the same on every PC it reaches.</summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();
}

/// <summary>
/// One item in a pack: a whole book, or with <see cref="PageRefId"/> a page range of it, with the game master's own
/// label and a one-line note. The same book can be in a pack any number of times.
/// </summary>
public sealed class SessionItem
{
    public long Id { get; set; }
    public long PackId { get; set; }
    /// <summary>Its section, or null for the items before the first section.</summary>
    public long? SectionId { get; set; }
    /// <summary>Its place among the items of its section.</summary>
    public int Position { get; set; }
    public long EntryId { get; set; }
    public long? PageRefId { get; set; }
    public PageRef? PageRef { get; set; }
    /// <summary>"Warehouse ambush"; null shows the book's title.</summary>
    public string? Label { get; set; }
    public string? Note { get; set; }
    public DateTime AddedUtc { get; set; }
    /// <summary>Names it in the library sync file (library sync plan, choice 4): made once, on the PC that made it, and the same on every PC it reaches.</summary>
    public Guid SyncId { get; set; } = Guid.NewGuid();
}

/// <summary>
/// What this PC knows of one record of the library sync file (library sync plan, choice 3): each field's value as it
/// was last seen or merged and when it changed, a tombstone if it was deleted, and whether it came from another PC but
/// can't be used here yet (its book isn't in this library, say). The catalog's own tables stay as they are; comparing
/// them with these rows is how a change made on this PC is noticed and timed, whichever page made it.
/// </summary>
public sealed class SyncState
{
    /// <summary>The record's key (<see cref="Core.Sync.SyncKeys"/>).</summary>
    public required string Key { get; set; }

    /// <summary>The fields: name to value (compact JSON, or null when cleared) and change time, as JSON.</summary>
    public required string FieldsJson { get; set; }

    /// <summary>A book's other keys, as a JSON array: the hashes of its other copies.</summary>
    public string? AliasesJson { get; set; }

    public DateTime? DeletedUtc { get; set; }

    /// <summary>From another PC, and not in this catalog: kept, and used once what it needs shows up here.</summary>
    public bool Pending { get; set; }
}

public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

/// <summary>
/// A question asked of a book and its answer (Ask plan, choice 10): one conversation per book, kept on its entry. The
/// answer is kept as the reader saw it, checked, in <see cref="Parts"/> (JSON), so its citations stay links.
/// </summary>
public sealed class AskTurn
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public DateTime AskedUtc { get; set; }
    /// <summary>As the reader typed it.</summary>
    public required string Question { get; set; }
    /// <summary>A follow-up as the model rewrote it to stand on its own, which found the pages; null when it wasn't rewritten.</summary>
    public string? Rewritten { get; set; }
    /// <summary>The answer's text as shown, citations included.</summary>
    public required string Answer { get; set; }
    /// <summary>The answer's checked pieces, as JSON.</summary>
    public required string Parts { get; set; }
    /// <summary>The PDF pages it cites, as a JSON array.</summary>
    public required string CitedPages { get; set; }
    public required string Model { get; set; }
    /// <summary>The model said the pages it read don't answer the question.</summary>
    public bool NotAnswered { get; set; }
}
