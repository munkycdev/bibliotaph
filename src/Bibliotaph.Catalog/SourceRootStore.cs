using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>The folders the user has added to the library. Nothing here touches the folders themselves.</summary>
public sealed class SourceRootStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Roots the user has not removed, in the order they were added.</summary>
    public async Task<IReadOnlyList<SourceRoot>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SourceRoots.AsNoTracking()
            .Where(r => r.Availability != SourceRootAvailability.RemovedByUser)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
    }

    /// <summary>Adds a folder, or brings back one that was removed. Paths compare case-insensitively, as on Windows.</summary>
    public async Task<SourceRoot> AddAsync(string path, CancellationToken ct = default)
    {
        var normalized = Normalize(path);
        await using var db = await contexts.CreateDbContextAsync(ct);
        // A library has a handful of roots, so compare in memory with Windows path rules
        // rather than relying on SQLite's ASCII-only NOCASE.
        var existing = (await db.SourceRoots.ToListAsync(ct))
            .FirstOrDefault(r => string.Equals(r.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Availability = SourceRootAvailability.Online;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var root = new SourceRoot { Path = normalized, Availability = SourceRootAvailability.Online, AddedUtc = _clock.GetUtcNow().UtcDateTime };
        db.SourceRoots.Add(root);
        await db.SaveChangesAsync(ct);
        return root;
    }

    /// <summary>Stops using a folder. Its catalog rows and the user's work on them are kept.</summary>
    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var root = await db.SourceRoots.FindAsync([id], ct);
        if (root is null) return;
        root.Availability = SourceRootAvailability.RemovedByUser;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Points a folder at the place it moved to for good (slice 4g plan, choice 6). Its files keep their rows, matched
    /// by their paths under it at its next scan, so each keeps its hash and document while its size and modified time
    /// are the same, and every card, collection, session and note on it stays. It stays offline until that scan, and its
    /// volume serial and its files' IDs are read again there, since it may be on another disk now. False when the folder is gone or removed, or another
    /// folder, even one removed from the library, has that path.
    /// </summary>
    public async Task<bool> RelocateAsync(long id, string path, CancellationToken ct = default)
    {
        var normalized = Normalize(path);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var all = await db.SourceRoots.ToListAsync(ct);
        var root = all.FirstOrDefault(r => r.Id == id);
        if (root is null || root.Availability == SourceRootAvailability.RemovedByUser) return false;
        if (all.Any(r => r.Id != id && string.Equals(r.Path, normalized, StringComparison.OrdinalIgnoreCase))) return false;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        root.Path = normalized;
        root.VolumeSerial = null;
        await db.SaveChangesAsync(ct);
        await db.FileLocations.Where(f => f.SourceRootId == id).ExecuteUpdateAsync(u => u.SetProperty(f => f.NtfsFileId, (string?)null), ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
