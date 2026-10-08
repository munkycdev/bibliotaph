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

    static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
