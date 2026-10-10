using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A collection as the pages show it: where it sits, and how many entries were added to it directly.</summary>
public sealed record CollectionInfo(long Id, long? ParentId, string Name, string? Description, bool Pinned, DateTime UsedUtc, int OwnCount);

/// <summary>What deleting a collection took away, for Undo: the row, the entries added to it and the sub-collections it held.</summary>
public sealed record CollectionDeletion(long Id, long? ParentId, string Name, string? Description, bool Pinned, DateTime CreatedUtc, DateTime UsedUtc,
    IReadOnlyList<(long EntryId, DateTime AddedUtc)> Items, IReadOnlyList<long> Children);

/// <summary>
/// Collections (slice 3 plan, choices 8 to 10): nested to any depth, holding entries only. Each change returns the
/// entries whose collections changed, including those that are in a moved or deleted collection through a
/// sub-collection, so the caller can project their marks.
/// </summary>
public sealed class CollectionStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<CollectionInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var counts = await db.CollectionItems.GroupBy(i => i.CollectionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return [.. (await db.Collections.AsNoTracking().ToListAsync(ct))
            .Select(c => new CollectionInfo(c.Id, c.ParentId, c.Name, c.Description, c.Pinned, c.UsedUtc, counts.GetValueOrDefault(c.Id)))];
    }

    /// <summary>A new collection, at the top or inside <paramref name="parentId"/>.</summary>
    public async Task<CollectionInfo> CreateAsync(string name, long? parentId = null, string? description = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (parentId is { } parent && !await db.Collections.AnyAsync(c => c.Id == parent, ct))
            throw new InvalidOperationException($"Collection {parent} doesn't exist.");
        var now = Now;
        var collection = new CollectionNode { Name = Name(name), ParentId = parentId, Description = Blank(description), CreatedUtc = now, UsedUtc = now };
        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct);
        return new CollectionInfo(collection.Id, collection.ParentId, collection.Name, collection.Description, false, now, 0);
    }

    /// <summary>Renames a collection and replaces its description. Returns false if it doesn't exist.</summary>
    public async Task<bool> RenameAsync(long collectionId, string name, string? description, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Collections.FindAsync([collectionId], ct) is not { } collection) return false;
        collection.Name = Name(name);
        collection.Description = Blank(description);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetPinnedAsync(long collectionId, bool pinned, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Collections.Where(c => c.Id == collectionId).ExecuteUpdateAsync(s => s.SetProperty(c => c.Pinned, pinned), ct) > 0;
    }

    /// <summary>
    /// Moves a collection into <paramref name="parentId"/>, or to the top. Refuses a move into itself or one of its own
    /// sub-collections. Returns the entries in it, sub-collections included, whose enclosing collections changed.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> MoveAsync(long collectionId, long? parentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tree = await db.Collections.Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
        if (!tree.TryGetValue(collectionId, out var current)) return [];
        if (current == parentId) return [];
        if (parentId is { } parent && (!tree.ContainsKey(parent) || Subtree(tree, collectionId).Contains(parent)))
            throw new InvalidOperationException("A collection can't go inside itself or one of its own sub-collections.");
        await db.Collections.Where(c => c.Id == collectionId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, parentId), ct);
        return await EntriesInAsync(db, Subtree(tree, collectionId), ct);
    }

    /// <summary>
    /// Deletes a collection, never its books: its sub-collections move up a level (choice 8). Returns what Undo needs,
    /// or null if it doesn't exist, and the entries whose collections changed.
    /// </summary>
    public async Task<(CollectionDeletion? Deleted, IReadOnlyList<EntryId> Changed)> DeleteAsync(long collectionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Collections.FindAsync([collectionId], ct) is not { } collection) return (null, []);
        var tree = await db.Collections.Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
        var changed = await EntriesInAsync(db, Subtree(tree, collectionId), ct);
        var items = await db.CollectionItems.AsNoTracking().Where(i => i.CollectionId == collectionId).Select(i => new { i.EntryId, i.AddedUtc }).ToListAsync(ct);
        var children = tree.Where(c => c.Value == collectionId).Select(c => c.Key).ToList();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Collections.Where(c => c.ParentId == collectionId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, collection.ParentId), ct);
        db.Collections.Remove(collection);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return (new CollectionDeletion(collection.Id, collection.ParentId, collection.Name, collection.Description, collection.Pinned, collection.CreatedUtc,
            collection.UsedUtc, [.. items.Select(i => (i.EntryId, i.AddedUtc))], children), changed);
    }

    /// <summary>
    /// Undoes <see cref="DeleteAsync"/>: the collection comes back with its id, its books (those still in the library)
    /// and the sub-collections it held that are still where it left them. Returns the entries whose collections changed.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> RestoreAsync(CollectionDeletion deleted, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var parent = deleted.ParentId is { } p && await db.Collections.AnyAsync(c => c.Id == p, ct) ? deleted.ParentId : null;
        db.Collections.Add(new CollectionNode
        {
            Id = deleted.Id,
            ParentId = parent,
            Name = deleted.Name,
            Description = deleted.Description,
            Pinned = deleted.Pinned,
            CreatedUtc = deleted.CreatedUtc,
            UsedUtc = deleted.UsedUtc,
        });
        var ids = deleted.Items.Select(i => i.EntryId).ToList();
        var existing = (await db.Entries.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        db.CollectionItems.AddRange(deleted.Items.Where(i => existing.Contains(i.EntryId))
            .Select(i => new CollectionItem { CollectionId = deleted.Id, EntryId = i.EntryId, AddedUtc = i.AddedUtc }));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        var children = deleted.Children.ToList();
        await db.Collections.Where(c => children.Contains(c.Id) && c.ParentId == deleted.ParentId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, deleted.Id), ct);
        await transaction.CommitAsync(ct);
        var tree = await db.Collections.Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
        return await EntriesInAsync(db, Subtree(tree, deleted.Id), ct);
    }

    /// <summary>Adds entries to a collection. Returns those that weren't in it already.</summary>
    public async Task<IReadOnlyList<EntryId>> AddAsync(long collectionId, IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Collections.FindAsync([collectionId], ct) is not { } collection) return [];
        var ids = entryIds.Select(e => e.Value).Distinct().ToList();
        var already = await db.CollectionItems.Where(i => i.CollectionId == collectionId && ids.Contains(i.EntryId)).Select(i => i.EntryId).ToListAsync(ct);
        var exists = await db.Entries.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
        var added = exists.Except(already).ToList();
        var now = Now;
        db.CollectionItems.AddRange(added.Select(id => new CollectionItem { CollectionId = collectionId, EntryId = id, AddedUtc = now }));
        collection.UsedUtc = now;
        await db.SaveChangesAsync(ct);
        return [.. added.Select(id => new EntryId(id))];
    }

    /// <summary>Takes entries out of a collection itself. Returns those that were in it.</summary>
    public async Task<IReadOnlyList<EntryId>> RemoveAsync(long collectionId, IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = entryIds.Select(e => e.Value).Distinct().ToList();
        var removed = await db.CollectionItems.Where(i => i.CollectionId == collectionId && ids.Contains(i.EntryId)).Select(i => i.EntryId).ToListAsync(ct);
        await db.CollectionItems.Where(i => i.CollectionId == collectionId && removed.Contains(i.EntryId)).ExecuteDeleteAsync(ct);
        return [.. removed.Select(id => new EntryId(id))];
    }

    /// <summary>The collections an entry was added to directly, for the inspector's chips.</summary>
    public async Task<IReadOnlyList<long>> GetForEntryAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.CollectionItems.Where(i => i.EntryId == entryId.Value).Select(i => i.CollectionId).ToListAsync(ct);
    }

    /// <summary>
    /// The scope rows for these entries, or for every entry when <paramref name="entryIds"/> is null: each collection
    /// an entry was added to, and every collection above it (choice 9), plus the collection itself on its own.
    /// </summary>
    public async Task<IReadOnlyList<(EntryId EntryId, string Scope)>> GetScopesAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var tree = await db.Collections.Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
        var items = db.CollectionItems.AsNoTracking();
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).ToList();
            items = items.Where(i => ids.Contains(i.EntryId));
        }
        var scopes = new HashSet<(EntryId, string)>();
        foreach (var item in await items.Select(i => new { i.CollectionId, i.EntryId }).ToListAsync(ct))
        {
            var entry = new EntryId(item.EntryId);
            scopes.Add((entry, ScopeKeys.CollectionOwn(item.CollectionId)));
            // Up the tree; the guard stops at a loop, which the moves never make but a hand-edited database could.
            long? at = item.CollectionId;
            for (var depth = 0; at is { } id && depth <= tree.Count; depth++)
            {
                scopes.Add((entry, ScopeKeys.Collection(id)));
                at = tree.GetValueOrDefault(id);
            }
        }
        return [.. scopes];
    }

    /// <summary>A collection and every collection under it.</summary>
    static HashSet<long> Subtree(Dictionary<long, long?> parents, long root)
    {
        var result = new HashSet<long> { root };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (id, parent) in parents)
                if (parent is { } p && result.Contains(p) && result.Add(id)) grew = true;
        }
        return result;
    }

    static async Task<IReadOnlyList<EntryId>> EntriesInAsync(CatalogDbContext db, IReadOnlyCollection<long> collections, CancellationToken ct)
    {
        var ids = collections.ToList();
        return [.. (await db.CollectionItems.Where(i => ids.Contains(i.CollectionId)).Select(i => i.EntryId).Distinct().ToListAsync(ct)).Select(id => new EntryId(id))];
    }

    static string Name(string name) =>
        name.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException("A collection needs a name.", nameof(name));

    static string? Blank(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
