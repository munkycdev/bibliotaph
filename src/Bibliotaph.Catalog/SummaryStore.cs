using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>How many of the user's own things there are, for the Summary page.</summary>
public sealed record UserWorkCounts(long Collections, long Binders, long BinderItems, long Notes, long SavedSearches);

/// <summary>The Summary page's figures that only catalog.db knows: folder sizes and the user's own things.</summary>
public sealed class SummaryStore(IDbContextFactory<CatalogDbContext> contexts)
{
    /// <summary>
    /// The bytes of the files in each library folder that are still there (cloud placeholders too, as they are owned),
    /// by folder id. A ZIP counts once, as the file on the disk: the files read from inside it are part of it.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, long>> GetFolderBytesAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var sizes = await db.FileLocations
            .Where(f => f.ContainerId == null && f.State != FileLocationState.Missing && f.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .GroupBy(f => f.SourceRootId)
            .Select(g => new { Root = g.Key, Bytes = g.Sum(f => f.SizeBytes) })
            .ToListAsync(ct);
        return sizes.ToDictionary(s => s.Root, s => s.Bytes);
    }

    public async Task<UserWorkCounts> GetUserWorkAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return new UserWorkCounts(
            await db.Collections.LongCountAsync(ct),
            await db.SessionPacks.LongCountAsync(ct),
            await db.SessionItems.LongCountAsync(ct),
            await db.Notes.LongCountAsync(ct),
            await db.SmartViews.LongCountAsync(ct));
    }
}
