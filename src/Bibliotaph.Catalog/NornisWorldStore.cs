using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A live Nornis world with its collection.</summary>
public sealed record NornisWorldInfo(string WorldId, string Name, long CollectionId);

/// <summary>
/// The Nornis worlds the library knows, for showing them: each world's collection and whether the world is gone. The
/// web library hears of them from Nornis; the Windows app has them from library sync.
/// </summary>
public sealed class NornisWorldStore(IDbContextFactory<CatalogDbContext> contexts)
{
    /// <summary>Each Nornis world's collection, and whether the world is gone.</summary>
    public async Task<IReadOnlyDictionary<long, bool>> CollectionsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await db.NornisWorlds.AsNoTracking().Where(w => w.CollectionId != null).Select(w => new { w.CollectionId, w.GoneUtc }).ToListAsync(ct))
            .GroupBy(w => w.CollectionId!.Value).ToDictionary(g => g.Key, g => g.All(w => w.GoneUtc != null));
    }

    /// <summary>
    /// The worlds that aren't gone and still have their collection, by name: the collection's, which is the world's
    /// unless the user renamed it, and the one name every device has.
    /// </summary>
    public async Task<IReadOnlyList<NornisWorldInfo>> LiveAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var worlds = await db.NornisWorlds.AsNoTracking().Where(w => w.GoneUtc == null && w.CollectionId != null)
            .Join(db.Collections, w => w.CollectionId, c => c.Id, (w, c) => new { w.WorldId, c.Name, CollectionId = c.Id }).ToListAsync(ct);
        return [.. worlds.Select(w => new NornisWorldInfo(w.WorldId, w.Name, w.CollectionId)).OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The collection of a world that isn't gone, or null.</summary>
    public async Task<long?> CollectionOfAsync(string worldId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.NornisWorlds.AsNoTracking().Where(w => w.WorldId == worldId && w.GoneUtc == null).Select(w => w.CollectionId).SingleOrDefaultAsync(ct);
    }
}
