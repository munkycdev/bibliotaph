using Bibliotaph.Core;

namespace Bibliotaph.Catalog.Entities;

// Persistence shapes for catalog.db. Slice 0 creates the core tables only; collections, smart views,
// session packs and notes arrive by migration in slice 3. Domain behaviour lives in Core as it is built.

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
    public required string Field { get; set; }
    public required string ValueJson { get; set; }
    public AssertionOrigin Origin { get; set; }
    /// <summary>PDF page indexes, as a JSON array.</summary>
    public string? EvidencePagesJson { get; set; }
    /// <summary>The classification run that produced it, for AI and rule origins.</summary>
    public string? RunId { get; set; }
    public AssertionState State { get; set; }
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
