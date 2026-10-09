using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A file the scanner saw: its path under the root and the cheap facts that say whether it changed.</summary>
public sealed record ScannedFile(string RelativePath, long SizeBytes, DateTime ModifiedUtc, bool OnlineOnly);

public sealed record ReconcileResult(int Added, int Changed, int Unchanged, int Missing);

/// <summary>A location the hasher still has to read.</summary>
public sealed record UnhashedFile(long LocationId, string FullPath, long SizeBytes, DateTime ModifiedUtc, bool OnlineOnly, string Format);

/// <summary>
/// Where an indexed document can be read from right now. <see cref="FolderHint"/> is the folders above the
/// first file under its source root ("Coriolis / Adventures"), which search treats as provisional text.
/// </summary>
public sealed record DocumentSource(long DocumentId, string ContentHash, string Format, string FullPath, string FolderHint, IReadOnlyList<string> AllPaths);

/// <summary>File and document counts. The unhashed online-only files are those still to download.</summary>
public sealed record LibraryCounts(int Files, int OnlineOnly, int Missing, int Unhashed, int Documents, int UnhashedOnlineOnly = 0, long UnhashedOnlineOnlyBytes = 0);

/// <summary>One place a document's file is, for the inspector.</summary>
public sealed record DocumentLocation(string FullPath, FileLocationState State, SourceRootAvailability RootAvailability);

/// <summary>
/// File locations and documents in catalog.db: the scanner's reconciliation and the hasher's results.
/// A document is its content hash; locations come and go. Nothing here reads or writes a source file.
/// </summary>
public sealed class LibraryStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Brings a root's locations in line with a completed scan of it. Unchanged files keep their hash; a file whose
    /// size or modified time changed loses it and is hashed again; files not seen are marked missing. Only call this
    /// with a full listing of a reachable root: an offline root must never mark its files missing (A07). Files under
    /// <paramref name="unreadFolders"/>, folders the scan couldn't list ("." for the root), keep their state for the same reason.
    /// </summary>
    public async Task<ReconcileResult> ReconcileRootAsync(
        long rootId, IReadOnlyCollection<ScannedFile> files, IReadOnlyCollection<string>? unreadFolders = null, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.FileLocations.Where(f => f.SourceRootId == rootId).ToListAsync(ct);
        var byPath = existing.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0, unchanged = 0;

        foreach (var file in files)
        {
            var state = file.OnlineOnly ? FileLocationState.OnlineOnly : FileLocationState.Present;
            if (byPath.Remove(file.RelativePath, out var location))
            {
                if (location.SizeBytes != file.SizeBytes || location.ModifiedUtc != file.ModifiedUtc)
                {
                    // New content at a known path. The old document keeps its other locations and the user's work;
                    // linking the new version to it as a revision is slice 4.
                    location.SizeBytes = file.SizeBytes;
                    location.ModifiedUtc = file.ModifiedUtc;
                    location.ContentHash = null;
                    location.DocumentId = null;
                    changed++;
                }
                else unchanged++;
                location.RelativePath = file.RelativePath;
                location.State = state;
                location.LastSeenUtc = now;
            }
            else
            {
                db.FileLocations.Add(new FileLocation
                {
                    SourceRootId = rootId,
                    RelativePath = file.RelativePath,
                    SizeBytes = file.SizeBytes,
                    ModifiedUtc = file.ModifiedUtc,
                    State = state,
                    LastSeenUtc = now,
                });
                added++;
            }
        }

        var missing = 0;
        foreach (var gone in byPath.Values.Where(f => f.State != FileLocationState.Missing && !IsUnder(f.RelativePath, unreadFolders)))
        {
            gone.State = FileLocationState.Missing;
            missing++;
        }

        var root = await db.SourceRoots.FindAsync([rootId], ct);
        if (root is not null && root.Availability == SourceRootAvailability.Offline) root.Availability = SourceRootAvailability.Online;
        await db.SaveChangesAsync(ct);
        return new ReconcileResult(added, changed, unchanged, missing);
    }

    static bool IsUnder(string relativePath, IReadOnlyCollection<string>? folders) =>
        folders is not null && folders.Any(folder => folder == "." || relativePath.StartsWith(
            Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>Marks a root offline (unreachable) or online again. Its files' states are left as they were.</summary>
    public async Task SetRootAvailabilityAsync(long rootId, SourceRootAvailability availability, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var root = await db.SourceRoots.FindAsync([rootId], ct);
        if (root is null || root.Availability == SourceRootAvailability.RemovedByUser) return;
        root.Availability = availability;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Locations that need hashing, local files first and online-only files after them (each online-only read
    /// downloads the file), smallest first within each group so the library fills quickly.
    /// </summary>
    public async Task<IReadOnlyList<UnhashedFile>> NextUnhashedAsync(int count, bool includeOnlineOnly, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.FileLocations.AsNoTracking()
            .Where(f => f.ContentHash == null && f.State != FileLocationState.Missing
                && f.SourceRoot.Availability == SourceRootAvailability.Online);
        if (!includeOnlineOnly) query = query.Where(f => f.State != FileLocationState.OnlineOnly);
        var rows = await query
            .OrderBy(f => f.State == FileLocationState.OnlineOnly).ThenBy(f => f.SizeBytes).ThenBy(f => f.Id)
            .Take(count)
            .Select(f => new { f.Id, Root = f.SourceRoot.Path, f.RelativePath, f.SizeBytes, f.ModifiedUtc, f.State })
            .ToListAsync(ct);
        return [.. rows.Select(r => new UnhashedFile(r.Id, Path.Combine(r.Root, r.RelativePath), r.SizeBytes, r.ModifiedUtc,
            r.State == FileLocationState.OnlineOnly, SourceFormats.FromFileName(r.RelativePath) ?? "unknown"))];
    }

    /// <summary>
    /// Records a location's hash and links it to the document with that content, creating the document when the
    /// content is new. Returns null when the file changed while it was being hashed (the next scan picks it up).
    /// </summary>
    public async Task<(long DocumentId, bool IsNew)?> AttachHashAsync(UnhashedFile file, ContentHash hash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var location = await db.FileLocations.FindAsync([file.LocationId], ct);
        if (location is null || location.SizeBytes != file.SizeBytes || location.ModifiedUtc != file.ModifiedUtc) return null;

        var document = await db.Documents.FirstOrDefaultAsync(d => d.ContentHash == hash.Hex, ct);
        var isNew = document is null;
        document ??= db.Documents.Add(new Document { ContentHash = hash.Hex, Format = file.Format, CreatedUtc = _clock.GetUtcNow().UtcDateTime }).Entity;
        location.ContentHash = hash.Hex;
        location.Document = document;
        await db.SaveChangesAsync(ct);
        return (document.Id, isNew);
    }

    /// <summary>Stores what Probe learned that belongs to the catalog: page count, protection and capabilities.</summary>
    public async Task SetProbeResultAsync(long documentId, int? pageCount, ProtectionType protection, string capabilitiesJson, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var document = await db.Documents.FindAsync([documentId], ct);
        if (document is null) return;
        document.PageCount = pageCount;
        document.Protection = protection;
        document.CapabilitiesJson = capabilitiesJson;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A readable location for a document: a present file first, then an online-only one. Null when every
    /// location is missing or its root is offline.
    /// </summary>
    public async Task<DocumentSource?> GetSourceAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return null;
        var locations = await db.FileLocations.AsNoTracking()
            .Where(f => f.DocumentId == documentId && f.State != FileLocationState.Missing && f.SourceRoot.Availability == SourceRootAvailability.Online)
            .OrderBy(f => f.State == FileLocationState.OnlineOnly).ThenBy(f => f.Id)
            .Select(f => new { Root = f.SourceRoot.Path, f.RelativePath })
            .ToListAsync(ct);
        if (locations.Count == 0) return null;
        var paths = locations.Select(l => Path.Combine(l.Root, l.RelativePath)).ToList();
        var folders = (Path.GetDirectoryName(locations[0].RelativePath) ?? "")
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        return new DocumentSource(document.Id, document.ContentHash, document.Format, paths[0], string.Join(" / ", folders), paths);
    }

    /// <summary>
    /// Where a document's file sits under its source folder ("D&amp;D 5e/Adventures/Tomb.pdf"), for hints from names:
    /// a present file first, then any other it still has, whether or not its folder is reachable right now. Null when
    /// every location is gone.
    /// </summary>
    public async Task<string?> GetRelativePathAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FileLocations.AsNoTracking()
            .Where(f => f.DocumentId == documentId && f.State != FileLocationState.Missing && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .OrderBy(f => f.State == FileLocationState.OnlineOnly).ThenBy(f => f.Id)
            .Select(f => f.RelativePath)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The relative path of every file the library shows, with its document, for the folder label list.</summary>
    public async Task<IReadOnlyList<(long DocumentId, string RelativePath)>> GetRelativePathsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.FileLocations.AsNoTracking()
            .Where(f => f.DocumentId != null && f.State != FileLocationState.Missing && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .Select(f => new { DocumentId = f.DocumentId!.Value, f.RelativePath })
            .ToListAsync(ct);
        return [.. rows.Select(r => (r.DocumentId, r.RelativePath))];
    }

    /// <summary>Those of <paramref name="documentIds"/> that <see cref="GetSourceAsync"/> would find a file for now.</summary>
    public async Task<IReadOnlyList<long>> GetReadableAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FileLocations
            .Where(f => f.DocumentId != null && documentIds.Contains(f.DocumentId.Value)
                && f.State != FileLocationState.Missing && f.SourceRoot.Availability == SourceRootAvailability.Online)
            .Select(f => f.DocumentId!.Value).Distinct().ToListAsync(ct);
    }

    /// <summary>
    /// The documents the library shows: those with a file that isn't missing, in a folder the user hasn't removed
    /// (an offline folder's books stay). With <paramref name="rootId"/>, only that folder's.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetVisibleDocumentIdsAsync(long? rootId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var locations = db.FileLocations.Where(f =>
            f.DocumentId != null && f.State != FileLocationState.Missing && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser);
        if (rootId is { } id) locations = locations.Where(f => f.SourceRootId == id);
        return await locations.Select(f => f.DocumentId!.Value).Distinct().ToListAsync(ct);
    }

    /// <summary>Every place a document's file is or was, readable ones first, for the inspector.</summary>
    public async Task<IReadOnlyList<DocumentLocation>> GetLocationsAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.FileLocations.AsNoTracking()
            .Where(f => f.DocumentId == documentId && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .OrderBy(f => f.State).ThenBy(f => f.Id)
            .Select(f => new { Root = f.SourceRoot.Path, f.RelativePath, f.State, f.SourceRoot.Availability })
            .ToListAsync(ct);
        return [.. rows.Select(r => new DocumentLocation(Path.Combine(r.Root, r.RelativePath), r.State, r.Availability))];
    }

    /// <summary>The library folders a document has a file in, for scanning them when it is reprocessed.</summary>
    public async Task<IReadOnlyList<long>> GetRootIdsAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FileLocations
            .Where(f => f.DocumentId == documentId && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .Select(f => f.SourceRootId).Distinct().ToListAsync(ct);
    }

    public async Task<LibraryCounts> GetCountsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var active = db.FileLocations.Where(f => f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser);
        var toDownload = active.Where(f => f.State == FileLocationState.OnlineOnly && f.ContentHash == null);
        return new LibraryCounts(
            await active.CountAsync(f => f.State != FileLocationState.Missing, ct),
            await active.CountAsync(f => f.State == FileLocationState.OnlineOnly, ct),
            await active.CountAsync(f => f.State == FileLocationState.Missing, ct),
            await active.CountAsync(f => f.State != FileLocationState.Missing && f.ContentHash == null, ct),
            await db.Documents.CountAsync(ct),
            await toDownload.CountAsync(ct),
            await toDownload.SumAsync(f => (long?)f.SizeBytes, ct) ?? 0);
    }
}
