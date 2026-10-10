using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A file the scanner saw: its path under the root and the cheap facts that say whether it changed.</summary>
public sealed record ScannedFile(string RelativePath, long SizeBytes, DateTime ModifiedUtc, bool OnlineOnly);

public sealed record ReconcileResult(int Added, int Changed, int Unchanged, int Missing);

/// <summary>
/// A location the hasher still has to read. A ZIP (<see cref="IsArchive"/>) is read for its members; a member of one has
/// its ZIP's path as <see cref="FullPath"/>'s start and its name inside it as <see cref="EntryPath"/>.
/// </summary>
public sealed record UnhashedFile(long LocationId, string FullPath, long SizeBytes, DateTime ModifiedUtc, bool OnlineOnly, string Format,
    bool IsArchive = false, string? EntryPath = null);

/// <summary>A file inside a ZIP, as the ZIP lists it. <see cref="Problem"/> says why it can't be read, if it can't.</summary>
public sealed record ArchiveMember(string EntryPath, long SizeBytes, DateTime ModifiedUtc, uint Crc32, string? Problem = null);

/// <summary>A file inside a ZIP that isn't read, and why, for Files needing attention.</summary>
public sealed record ArchiveProblem(long LocationId, string FullPath, string Problem);

/// <summary>
/// Where an indexed document can be read from right now. <see cref="FolderHint"/> is the folders above the
/// first file under its source root ("Coriolis / Adventures"), which search treats as provisional text. For a file
/// inside a ZIP, <see cref="FullPath"/> is the path File Explorer would show, and the file is read from
/// <see cref="ArchivePath"/> by <see cref="EntryPath"/>.
/// </summary>
public sealed record DocumentSource(long DocumentId, string ContentHash, string Format, string FullPath, string FolderHint, IReadOnlyList<string> AllPaths,
    string? ArchivePath = null, string? EntryPath = null)
{
    public bool InArchive => ArchivePath is not null;
}

/// <summary>
/// File and document counts. The unhashed online-only files are those still to download; <see cref="Problems"/> are
/// files inside ZIPs that can't be read.
/// </summary>
public sealed record LibraryCounts(int Files, int OnlineOnly, int Missing, int Unhashed, int Documents, int UnhashedOnlineOnly = 0, long UnhashedOnlineOnlyBytes = 0,
    int Problems = 0);

/// <summary>One place a document's file is, for the inspector. A file inside a ZIP has the ZIP's path as <see cref="ArchivePath"/>.</summary>
public sealed record DocumentLocation(string FullPath, FileLocationState State, SourceRootAvailability RootAvailability, string? ArchivePath = null)
{
    /// <summary>What File Explorer can select: the file, or the ZIP it is in.</summary>
    public string ExplorerPath => ArchivePath ?? FullPath;
}

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
    /// Files inside a ZIP follow it: missing with it, and listed again when it changes or comes back.
    /// </summary>
    public async Task<ReconcileResult> ReconcileRootAsync(
        long rootId, IReadOnlyCollection<ScannedFile> files, IReadOnlyCollection<string>? unreadFolders = null, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.FileLocations.Where(f => f.SourceRootId == rootId && f.ContainerId == null).ToListAsync(ct);
        var wasMissing = existing.Where(f => f.State == FileLocationState.Missing).Select(f => f.Id).ToHashSet();
        var byPath = existing.ToDictionary(f => f.RelativePath, StringComparer.OrdinalIgnoreCase);
        int added = 0, changed = 0, unchanged = 0;

        foreach (var file in files)
        {
            var state = file.OnlineOnly ? FileLocationState.OnlineOnly : FileLocationState.Present;
            if (byPath.Remove(file.RelativePath, out var location))
            {
                if (location.SizeBytes != file.SizeBytes || location.ModifiedUtc != file.ModifiedUtc)
                {
                    // New content at a known path. The old document keeps its other locations; the new content joins
                    // its card as a new version once it is hashed (A08).
                    location.SizeBytes = file.SizeBytes;
                    location.ModifiedUtc = file.ModifiedUtc;
                    location.ContentHash = null;
                    location.PreviousDocumentId = location.DocumentId ?? location.PreviousDocumentId;
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

        await FollowArchivesAsync(db, existing, wasMissing, ct);

        var root = await db.SourceRoots.FindAsync([rootId], ct);
        if (root is not null && root.Availability == SourceRootAvailability.Offline) root.Availability = SourceRootAvailability.Online;
        await db.SaveChangesAsync(ct);
        return new ReconcileResult(added, changed, unchanged, missing);
    }

    /// <summary>
    /// Files inside a ZIP take its state: missing when it is, online-only or present as it is. A ZIP that comes back
    /// is listed again rather than trusted, since it may hold other files now; until then its members stay missing.
    /// </summary>
    static async Task FollowArchivesAsync(CatalogDbContext db, List<FileLocation> archives, HashSet<long> wasMissing, CancellationToken ct)
    {
        var byId = archives.Where(a => SourceFormats.IsArchive(a.RelativePath)).ToDictionary(a => a.Id);
        if (byId.Count == 0) return;
        var ids = byId.Keys.ToList();
        var members = await db.FileLocations.Where(f => f.ContainerId != null && ids.Contains(f.ContainerId.Value)).ToListAsync(ct);
        foreach (var archive in byId.Values.Where(a => wasMissing.Contains(a.Id) && a.State != FileLocationState.Missing && a.ContentHash is not null))
            archive.ContentHash = null;
        foreach (var member in members)
        {
            var archive = byId[member.ContainerId!.Value];
            if (archive.State == FileLocationState.Missing) member.State = FileLocationState.Missing;
            else if (member.State != FileLocationState.Missing) member.State = archive.State;
        }
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
        // Files inside a ZIP are hashed as their ZIP is read, not on their own.
        var query = db.FileLocations.AsNoTracking()
            .Where(f => f.ContentHash == null && f.ContainerId == null && f.State != FileLocationState.Missing
                && f.SourceRoot.Availability == SourceRootAvailability.Online);
        if (!includeOnlineOnly) query = query.Where(f => f.State != FileLocationState.OnlineOnly);
        var rows = await query
            .OrderBy(f => f.State == FileLocationState.OnlineOnly).ThenBy(f => f.SizeBytes).ThenBy(f => f.Id)
            .Take(count)
            .Select(f => new { f.Id, Root = f.SourceRoot.Path, f.RelativePath, f.SizeBytes, f.ModifiedUtc, f.State })
            .ToListAsync(ct);
        return [.. rows.Select(r => new UnhashedFile(r.Id, Path.Combine(r.Root, r.RelativePath), r.SizeBytes, r.ModifiedUtc,
            r.State == FileLocationState.OnlineOnly, SourceFormats.FromFileName(r.RelativePath) ?? "unknown", SourceFormats.IsArchive(r.RelativePath)))];
    }

    /// <summary>
    /// Brings a ZIP's members in line with what it holds now (F3 plan, choices 3 and 4). A member with the same name,
    /// size and CRC keeps its hash; a changed one is hashed again and, as a file replaced at its path, becomes a new
    /// version of its book (A08); one no longer in the ZIP is missing. Returns the members to hash, or null when the
    /// ZIP changed while it was read (the next scan picks it up).
    /// </summary>
    public async Task<IReadOnlyList<UnhashedFile>?> ReconcileArchiveAsync(UnhashedFile archive, IReadOnlyList<ArchiveMember> members, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var zip = await db.FileLocations.Include(f => f.SourceRoot).FirstOrDefaultAsync(f => f.Id == archive.LocationId, ct);
        if (zip is null || zip.SizeBytes != archive.SizeBytes || zip.ModifiedUtc != archive.ModifiedUtc) return null;

        var existing = await db.FileLocations.Where(f => f.ContainerId == zip.Id).ToListAsync(ct);
        var byEntry = existing.ToDictionary(f => f.EntryPath!, StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (byEntry.Remove(member.EntryPath, out var location))
            {
                var changed = location.SizeBytes != member.SizeBytes || location.EntryCrc32 != member.Crc32 || member.Problem is not null;
                if (changed && location.ContentHash is not null)
                {
                    location.ContentHash = null;
                    location.PreviousDocumentId = location.DocumentId ?? location.PreviousDocumentId;
                    location.DocumentId = null;
                }
            }
            else
            {
                location = db.FileLocations.Add(new FileLocation
                {
                    SourceRootId = zip.SourceRootId,
                    RelativePath = ArchivePaths.MemberPath(zip.RelativePath, member.EntryPath),
                    ContainerId = zip.Id,
                    EntryPath = member.EntryPath,
                }).Entity;
            }
            location.SizeBytes = member.SizeBytes;
            location.ModifiedUtc = member.ModifiedUtc;
            location.EntryCrc32 = member.Crc32;
            location.Problem = member.Problem;
            location.State = zip.State;
            location.LastSeenUtc = now;
        }
        foreach (var gone in byEntry.Values) gone.State = FileLocationState.Missing;
        await db.SaveChangesAsync(ct);

        return [.. db.ChangeTracker.Entries<FileLocation>().Select(e => e.Entity)
            .Where(f => f.ContainerId == zip.Id && f.ContentHash == null && f.Problem == null && f.State != FileLocationState.Missing)
            .OrderBy(f => f.Id)
            .Select(f => new UnhashedFile(f.Id, Path.Combine(zip.SourceRoot.Path, f.RelativePath), f.SizeBytes, f.ModifiedUtc,
                f.State == FileLocationState.OnlineOnly, SourceFormats.FromFileName(f.EntryPath!) ?? "unknown", EntryPath: f.EntryPath))];
    }

    /// <summary>Records that a ZIP has been read for its members, unless it changed meanwhile. A ZIP has no document of its own.</summary>
    public async Task<bool> AttachArchiveHashAsync(UnhashedFile archive, ContentHash hash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var location = await db.FileLocations.FindAsync([archive.LocationId], ct);
        if (location is null || location.SizeBytes != archive.SizeBytes || location.ModifiedUtc != archive.ModifiedUtc) return false;
        location.ContentHash = hash.Hex;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Marks a file inside a ZIP as one that can't be read, such as one damaged inside it.</summary>
    public async Task SetProblemAsync(long locationId, string problem, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.FileLocations.Where(f => f.Id == locationId).ExecuteUpdateAsync(u => u.SetProperty(f => f.Problem, problem), ct);
    }

    /// <summary>The files inside ZIPs that aren't read, and why, for Files needing attention.</summary>
    public async Task<IReadOnlyList<ArchiveProblem>> GetProblemsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.FileLocations.AsNoTracking()
            .Where(f => f.Problem != null && f.State != FileLocationState.Missing && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .OrderBy(f => f.RelativePath)
            .Select(f => new { f.Id, Root = f.SourceRoot.Path, f.RelativePath, f.Problem })
            .ToListAsync(ct);
        return [.. rows.Select(r => new ArchiveProblem(r.Id, Path.Combine(r.Root, r.RelativePath), r.Problem!))];
    }

    /// <summary>
    /// Records a location's hash and links it to the document with that content, creating the document when the
    /// content is new. A new document gets a whole-document entry for its library card or, when its path held another
    /// book's file before, becomes that book's current version. Returns null when the file changed while it was being
    /// hashed (the next scan picks it up).
    /// </summary>
    public async Task<(long DocumentId, bool IsNew)?> AttachHashAsync(UnhashedFile file, ContentHash hash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var location = await db.FileLocations.FindAsync([file.LocationId], ct);
        if (location is null || location.SizeBytes != file.SizeBytes || location.ModifiedUtc != file.ModifiedUtc) return null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.ContentHash == hash.Hex, ct);
        var isNew = document is null;
        if (document is null)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            document = db.Documents.Add(new Document { ContentHash = hash.Hex, Format = file.Format, CreatedUtc = now }).Entity;
            // New content where another book's file was is a new version of that book (A08); anything else is a new card.
            if (location.PreviousDocumentId is not { } previous || await EntryStore.AddVersionAsync(db, document, previous, ct) is null)
                db.EntrySources.Add(new EntrySource { Entry = new Entry { Kind = EntryKind.Whole, CreatedUtc = now }, Document = document, IsCurrent = true });
        }
        location.ContentHash = hash.Hex;
        location.Document = document;
        location.PreviousDocumentId = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
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
    /// A readable location for a document: a present file first, then an online-only one, then a file inside a ZIP,
    /// which has to be extracted to be read. Null when every location is missing or its root is offline.
    /// </summary>
    public async Task<DocumentSource?> GetSourceAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var document = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (document is null) return null;
        var locations = await db.FileLocations.AsNoTracking()
            .Where(f => f.DocumentId == documentId && f.State != FileLocationState.Missing && f.SourceRoot.Availability == SourceRootAvailability.Online)
            .OrderBy(f => f.ContainerId != null).ThenBy(f => f.State == FileLocationState.OnlineOnly).ThenBy(f => f.Id)
            .Select(f => new { Root = f.SourceRoot.Path, f.RelativePath, Archive = f.Container != null ? f.Container.RelativePath : null, f.EntryPath })
            .ToListAsync(ct);
        if (locations.Count == 0) return null;
        var paths = locations.Select(l => Path.Combine(l.Root, l.RelativePath)).ToList();
        var first = locations[0];
        var folders = (Path.GetDirectoryName(first.RelativePath) ?? "")
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(ArchivePaths.FolderName);
        return new DocumentSource(document.Id, document.ContentHash, document.Format, paths[0], string.Join(" / ", folders), paths,
            first.Archive is null ? null : Path.Combine(first.Root, first.Archive), first.EntryPath);
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
    /// The entries the library shows: those backed by a document with a file that isn't missing, in a folder the user
    /// hasn't removed (an offline folder's books stay), and books owned elsewhere. With <paramref name="rootId"/>, only
    /// that folder's.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> GetVisibleEntryIdsAsync(long? rootId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var locations = db.FileLocations.Where(f =>
            f.DocumentId != null && f.State != FileLocationState.Missing && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser);
        if (rootId is { } id) locations = locations.Where(f => f.SourceRootId == id);
        var documents = locations.Select(f => f.DocumentId!.Value);
        var entries = await db.EntrySources.Where(s => documents.Contains(s.DocumentId)).Select(s => s.EntryId).Distinct().ToListAsync(ct);
        // A pack has no file of its own: it shows while any of its images does.
        var packs = await db.Entries
            .Where(e => entries.Contains(e.Id) && e.ParentEntryId != null && e.ParentEntry!.Kind == EntryKind.Pack)
            .Select(e => e.ParentEntryId!.Value).Distinct().ToListAsync(ct);
        // A book owned elsewhere has no file, so no folder decides whether it shows; it isn't in any one folder.
        List<long> elsewhere = rootId is null
            ? await db.Entries.Where(e => e.Kind == EntryKind.Elsewhere && e.MergedIntoEntryId == null && !e.Sources.Any()).Select(e => e.Id).ToListAsync(ct)
            : [];
        return [.. entries.Concat(packs).Concat(elsewhere).Select(e => new EntryId(e))];
    }

    /// <summary>
    /// The document with this content that has a file in a library folder, other than one gone missing; null when there
    /// is none. Check a download asks it of each file it reads (F5 plan, choice 11).
    /// </summary>
    public async Task<long?> FindDocumentAsync(ContentHash hash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FileLocations.AsNoTracking()
            .Where(f => f.ContentHash == hash.Hex && f.DocumentId != null && f.State != FileLocationState.Missing
                && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .OrderBy(f => f.DocumentId).Select(f => f.DocumentId).FirstOrDefaultAsync(ct);
    }

    /// <summary>Every place a document's file is or was, readable ones first, for the inspector.</summary>
    public async Task<IReadOnlyList<DocumentLocation>> GetLocationsAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.FileLocations.AsNoTracking()
            .Where(f => f.DocumentId == documentId && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .OrderBy(f => f.State).ThenBy(f => f.ContainerId != null).ThenBy(f => f.Id)
            .Select(f => new { Root = f.SourceRoot.Path, f.RelativePath, f.State, f.SourceRoot.Availability, Archive = f.Container != null ? f.Container.RelativePath : null })
            .ToListAsync(ct);
        return [.. rows.Select(r => new DocumentLocation(Path.Combine(r.Root, r.RelativePath), r.State, r.Availability,
            r.Archive is null ? null : Path.Combine(r.Root, r.Archive)))];
    }

    /// <summary>
    /// The library folders a document has a file in, for scanning them when it is reprocessed and for "Don't send this
    /// folder to AI". A file since gone from a folder still counts, so a book once kept from AI stays kept.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetRootIdsAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FileLocations
            .Where(f => f.DocumentId == documentId && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .Select(f => f.SourceRootId).Distinct().ToListAsync(ct);
    }

    /// <summary>The documents with a file in any of <paramref name="rootIds"/>.</summary>
    public async Task<IReadOnlyList<long>> GetDocumentIdsInRootsAsync(IReadOnlyCollection<long> rootIds, CancellationToken ct = default)
    {
        if (rootIds.Count == 0) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.FileLocations
            .Where(f => f.DocumentId != null && rootIds.Contains(f.SourceRootId) && f.State != FileLocationState.Missing)
            .Select(f => f.DocumentId!.Value).Distinct().ToListAsync(ct);
    }

    public async Task<LibraryCounts> GetCountsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var active = db.FileLocations.Where(f => f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser);
        // A file inside an online-only ZIP downloads with the ZIP, so only the ZIP counts as one to download.
        var toDownload = active.Where(f => f.State == FileLocationState.OnlineOnly && f.ContentHash == null && f.ContainerId == null);
        return new LibraryCounts(
            await active.CountAsync(f => f.State != FileLocationState.Missing, ct),
            await active.CountAsync(f => f.State == FileLocationState.OnlineOnly && f.ContainerId == null, ct),
            await active.CountAsync(f => f.State == FileLocationState.Missing, ct),
            await active.CountAsync(f => f.State != FileLocationState.Missing && f.ContentHash == null && f.Problem == null, ct),
            await db.Documents.CountAsync(ct),
            await toDownload.CountAsync(ct),
            await toDownload.SumAsync(f => (long?)f.SizeBytes, ct) ?? 0,
            await active.CountAsync(f => f.State != FileLocationState.Missing && f.Problem != null, ct));
    }
}
