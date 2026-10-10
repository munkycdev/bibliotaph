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
/// the page it was left at after an ordinary open, where it opens next time.
/// </summary>
public sealed class ReadingService(ReadingStore reading, MetadataProjector projector)
{
    /// <summary>Records that a document was opened and projects it. Returns the card it shows under.</summary>
    public async Task<EntryId?> RecordOpenAsync(long documentId, CancellationToken ct = default)
    {
        if (await reading.RecordOpenAsync(documentId, ct) is not { } card) return null;
        await projector.ProjectMarksAsync([card], ct);
        return card;
    }

    public Task SavePositionAsync(long documentId, int pageIndex, CancellationToken ct = default) => reading.SavePositionAsync(documentId, pageIndex, ct);

    public Task<int> GetPositionAsync(long documentId, CancellationToken ct = default) => reading.GetPositionAsync(documentId, ct);
}
