using Bibliotaph.Core;

namespace Bibliotaph.Catalog.Entities;

// Persistence shapes for catalog.db. Slice 0 created the core tables; slice 2 adds vocabulary, rejections and
// classification runs; collections, smart views, session packs and notes arrive by migration in slice 3.
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
    public DateTime LastSeenUtc { get; set; }
    public FileLocationState State { get; set; }
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
    /// <summary>The earlier version when a path's content was replaced.</summary>
    public long? PreviousVersionId { get; set; }
    public Document? PreviousVersion { get; set; }
    public DateTime CreatedUtc { get; set; }
    public List<FileLocation> Locations { get; set; } = [];
    public List<Assertion> Assertions { get; set; } = [];
    public List<Rejection> Rejections { get; set; } = [];
    public List<PageRef> PageRefs { get; set; } = [];
}

/// <summary>
/// One claimed value for one metadata field, with where it came from and its evidence. The effective value
/// of a field is derived from these rows; nothing overwrites a value in place.
/// </summary>
public sealed class Assertion
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    /// <summary>A <see cref="Core.Metadata.MetadataField"/> key.</summary>
    public required string Field { get; set; }
    /// <summary>The value as a JSON string: text as written, a term's key, a level range ("1-5"), a year.</summary>
    public required string ValueJson { get; set; }
    /// <summary>The value's comparison form, which rejections and duplicate checks match on.</summary>
    public required string NormalizedValue { get; set; }
    public AssertionOrigin Origin { get; set; }
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
/// A value the user said is wrong for a document. It suppresses that value from every source, including any later
/// model, so a rejected suggestion never comes back (A04).
/// </summary>
public sealed class Rejection
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public Document Document { get; set; } = null!;
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
    /// <summary>"system", "edition", "type", "setting", "theme", "environment" or "publisher".</summary>
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
/// One classifier run over one document: which model, which prompt and schema, and which pages it read. Kept in
/// catalog.db with the assertions it produced, so an index.db rebuild neither loses their provenance nor re-runs
/// the model (slice 2 plan, choice 5).
/// </summary>
public sealed class ClassificationRun
{
    public required string Id { get; set; }
    public long DocumentId { get; set; }
    public Document Document { get; set; } = null!;
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

/// <summary>A folder label the user switched off in Settings > Library folders: that folder name no longer suggests that term.</summary>
public sealed class IgnoredFolderLabel
{
    public long Id { get; set; }
    /// <summary>The folder name in comparison form.</summary>
    public required string Folder { get; set; }
    public required string Vocabulary { get; set; }
    public required string TermKey { get; set; }
    public DateTime CreatedUtc { get; set; }
}

/// <summary>A page or page range that user work (session pack items, page notes) points at.</summary>
public sealed class PageRef
{
    public long Id { get; set; }
    public long DocumentId { get; set; }
    public Document Document { get; set; } = null!;
    public int FirstPdfPage { get; set; }
    public int LastPdfPage { get; set; }
    /// <summary>Printed labels at the time the reference was made, for display if the document changes.</summary>
    public string? PrintedLabels { get; set; }
    /// <summary>Page-text fingerprint, to suggest the matching page in a new edition.</summary>
    public string? TextFingerprint { get; set; }
    public string? Label { get; set; }
    public bool IsStale { get; set; }
}

public sealed class Setting
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}
