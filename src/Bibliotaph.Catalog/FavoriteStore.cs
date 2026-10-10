using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>The books marked with a heart (slice 3 plan, choice 5), on cards, in the inspector and in the reader.</summary>
public sealed class FavoriteStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Marks or unmarks these entries. Returns those that changed.</summary>
    public async Task<IReadOnlyList<EntryId>> SetAsync(IReadOnlyCollection<EntryId> entryIds, bool favorite, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return [];
        var ids = entryIds.Select(e => e.Value).Distinct().ToList();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var marked = await db.Favorites.Where(f => ids.Contains(f.EntryId)).Select(f => f.EntryId).ToListAsync(ct);
        List<long> changed;
        if (favorite)
        {
            var exists = await db.Entries.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
            changed = [.. exists.Except(marked)];
            var now = _clock.GetUtcNow().UtcDateTime;
            db.Favorites.AddRange(changed.Select(id => new Favorite { EntryId = id, CreatedUtc = now }));
            await db.SaveChangesAsync(ct);
        }
        else
        {
            changed = marked;
            await db.Favorites.Where(f => changed.Contains(f.EntryId)).ExecuteDeleteAsync(ct);
        }
        return [.. changed.Select(id => new EntryId(id))];
    }

    public async Task<bool> IsFavoriteAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Favorites.AnyAsync(f => f.EntryId == entryId.Value, ct);
    }

    /// <summary>Which of these entries are marked, or every marked entry when <paramref name="entryIds"/> is null.</summary>
    public async Task<IReadOnlySet<EntryId>> GetAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var favorites = db.Favorites.AsNoTracking();
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).ToList();
            favorites = favorites.Where(f => ids.Contains(f.EntryId));
        }
        return (await favorites.Select(f => f.EntryId).ToListAsync(ct)).Select(id => new EntryId(id)).ToHashSet();
    }
}
