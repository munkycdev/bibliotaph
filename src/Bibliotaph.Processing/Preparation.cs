using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// Hearts on books (slice 3 plan, choice 5): each change goes to catalog.db, then the marks are projected, so the
/// Library's Favourites and <c>favorite:yes</c> follow at once.
/// </summary>
public sealed class FavoritesService(FavoriteStore favorites, EntryStore entries, MetadataProjector projector)
{
    /// <summary>Marks or unmarks these books. Returns those that changed.</summary>
    public async Task<IReadOnlyList<EntryId>> SetAsync(IReadOnlyCollection<EntryId> entryIds, bool favorite, CancellationToken ct = default)
    {
        var changed = await favorites.SetAsync(entryIds, favorite, ct);
        await projector.ProjectMarksAsync(changed, ct);
        return changed;
    }

    public Task<bool> IsFavoriteAsync(EntryId entryId, CancellationToken ct = default) => favorites.IsFavoriteAsync(entryId, ct);

    /// <summary>The card a document shows under, for the reader's heart: its own, or its pack's.</summary>
    public Task<EntryId?> GetCardAsync(long documentId, CancellationToken ct = default) => entries.GetCardAsync(documentId, ct);
}

/// <summary>
/// What the reader leaves behind (choice 6): when each book was opened, for Home and the Recently opened order, and
/// the page it was left at after an ordinary open, where it opens next time. Left in one copy, the book opens another
/// at the matching page (slice 4h plan, choice 5).
/// </summary>
public sealed class ReadingService(ReadingStore reading, MetadataProjector projector, PagePlaces places)
{
    /// <summary>Records that a document was opened and projects it. Returns the card it shows under.</summary>
    public async Task<EntryId?> RecordOpenAsync(long documentId, CancellationToken ct = default)
    {
        if (await reading.RecordOpenAsync(documentId, await CarriedPageAsync(documentId, ct), ct) is not { } card) return null;
        await projector.ProjectMarksAsync([card], ct);
        return card;
    }

    public Task SavePositionAsync(long documentId, int pageIndex, CancellationToken ct = default) => reading.SavePositionAsync(documentId, pageIndex, ct);

    /// <summary>The page a reader left this document at, or the matching page when another copy was read last; 0 when the book hasn't been read.</summary>
    public async Task<int> GetPositionAsync(long documentId, CancellationToken ct = default) =>
        await reading.GetPlaceAsync(documentId, ct) is not { } place ? 0
        : place.DocumentId == documentId ? place.PageIndex
        : await places.MapPageAsync(place.DocumentId, place.PageIndex, documentId, ct);

    /// <summary>The page of this document that matches where another copy was left, which an open of it carries over.</summary>
    async Task<int> CarriedPageAsync(long documentId, CancellationToken ct) =>
        await reading.GetPlaceAsync(documentId, ct) is { } place && place.DocumentId != documentId
            ? await places.MapPageAsync(place.DocumentId, place.PageIndex, documentId, ct)
            : 0;
}

/// <summary>
/// Collections (slice 3 plan, choices 8 to 10): each change goes to catalog.db, then the collections' names and the
/// changed entries' marks are projected, so a scoped Library and <c>collection:"name"</c> follow at once.
/// </summary>
public sealed class CollectionsService(CollectionStore collections, MetadataProjector projector)
{
    /// <summary>Raised after any collection changed, on the thread that changed it.</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<CollectionInfo>> ListAsync(CancellationToken ct = default) => collections.ListAsync(ct);

    public Task<IReadOnlyList<long>> GetForEntryAsync(EntryId entryId, CancellationToken ct = default) => collections.GetForEntryAsync(entryId, ct);

    public async Task<CollectionInfo> CreateAsync(string name, long? parentId = null, string? description = null, CancellationToken ct = default)
    {
        var created = await collections.CreateAsync(name, parentId, description, ct);
        await ProjectAsync([], ct);
        return created;
    }

    public async Task RenameAsync(long collectionId, string name, string? description, CancellationToken ct = default)
    {
        if (await collections.RenameAsync(collectionId, name, description, ct)) await ProjectAsync([], ct);
    }

    public async Task SetPinnedAsync(long collectionId, bool pinned, CancellationToken ct = default)
    {
        if (await collections.SetPinnedAsync(collectionId, pinned, ct)) await ProjectAsync([], ct);
    }

    public async Task MoveAsync(long collectionId, long? parentId, CancellationToken ct = default) =>
        await ProjectAsync(await collections.MoveAsync(collectionId, parentId, ct), ct);

    public async Task<CollectionDeletion?> DeleteAsync(long collectionId, CancellationToken ct = default)
    {
        var (deleted, changed) = await collections.DeleteAsync(collectionId, ct);
        if (deleted is not null) await ProjectAsync(changed, ct);
        return deleted;
    }

    public async Task RestoreAsync(CollectionDeletion deleted, CancellationToken ct = default) =>
        await ProjectAsync(await collections.RestoreAsync(deleted, ct), ct);

    /// <summary>Adds books to a collection. Returns those that weren't in it already, for Undo.</summary>
    public async Task<IReadOnlyList<EntryId>> AddAsync(long collectionId, IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        var added = await collections.AddAsync(collectionId, entryIds, ct);
        // Even when every book was in it already: the collection was used, so it heads the recent ones.
        await ProjectAsync(added, ct);
        return added;
    }

    /// <summary>Takes books out of a collection. Returns those that were in it, for Undo.</summary>
    public async Task<IReadOnlyList<EntryId>> RemoveAsync(long collectionId, IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        var removed = await collections.RemoveAsync(collectionId, entryIds, ct);
        if (removed.Count > 0) await ProjectAsync(removed, ct);
        return removed;
    }

    async Task ProjectAsync(IReadOnlyCollection<EntryId> changed, CancellationToken ct)
    {
        await projector.ProjectCollectionsAsync(changed, ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
