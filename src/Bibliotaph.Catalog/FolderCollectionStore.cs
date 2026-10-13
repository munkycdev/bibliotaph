using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A folder's answer about making a collection from it, with the collection made, if it is still there.</summary>
public sealed record FolderCollectionInfo(long Id, FolderKey Folder, FolderCollectionState State, long? CollectionId)
{
    /// <summary>The collection follows the folder: books that land in it later join.</summary>
    public bool Follows => State == FolderCollectionState.Linked && CollectionId is not null;
}

/// <summary>
/// The answers about making collections from folders, in catalog.db: which folders became collections that follow
/// them, which collections stopped following theirs after a change by hand, and which folders the user said Never to.
/// A folder with an answer isn't asked about again. These stay on this PC, since a folder is one PC's; the collections
/// travel in the library sync file as any others do.
/// </summary>
public sealed class FolderCollectionStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Every folder answered, in a library folder the user hasn't removed.</summary>
    public async Task<IReadOnlyList<FolderCollectionInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.CollectionFolders.AsNoTracking()
            .Join(db.SourceRoots.Where(s => s.Availability != SourceRootAvailability.RemovedByUser), f => f.SourceRootId, s => s.Id, (f, _) => f)
            .ToListAsync(ct);
        return [.. rows.Select(f => new FolderCollectionInfo(f.Id, new FolderKey(f.SourceRootId, f.Folder), f.State, f.CollectionId))];
    }

    /// <summary>
    /// Records the answer for a folder: a collection made from it (<paramref name="collectionId"/>, linked) or Never
    /// (null, declined). Replaces an earlier answer for the same folder. Returns the row's id, for <see cref="ForgetAsync"/>.
    /// </summary>
    public async Task<long> AnswerAsync(FolderKey folder, long? collectionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.CollectionFolders.Where(f => f.SourceRootId == folder.RootId).ToListAsync(ct);
        var row = rows.FirstOrDefault(f => new FolderKey(f.SourceRootId, f.Folder) == folder);
        if (row is null) db.CollectionFolders.Add(row = new CollectionFolder { SourceRootId = folder.RootId, Folder = folder.Folder });
        row.State = collectionId is null ? FolderCollectionState.Declined : FolderCollectionState.Linked;
        row.CollectionId = collectionId;
        row.DecidedUtc = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    /// <summary>Undo for <see cref="AnswerAsync"/>: the folder is unanswered again, so it can be asked about.</summary>
    public async Task ForgetAsync(long id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.CollectionFolders.Where(f => f.Id == id).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// The user changed which books a collection holds by hand, so it stops following its folder, if it did. Returns
    /// whether it did.
    /// </summary>
    public async Task<bool> UnlinkAsync(long collectionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        return await db.CollectionFolders
            .Where(f => f.CollectionId == collectionId && f.State == FolderCollectionState.Linked)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.State, FolderCollectionState.Unlinked).SetProperty(f => f.DecidedUtc, now), ct) > 0;
    }
}
