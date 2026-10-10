using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// When each book was last opened and where its reader left it (slice 3 plan, choice 6), for Home's Recently opened,
/// the Library's Recently opened order, and opening a book where it was left.
/// </summary>
public sealed class ReadingStore(IDbContextFactory<CatalogDbContext> contexts, EntryStore entries, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Records that a document was opened, on the card it shows under. The saved page stays while the same document
    /// is read; another copy takes <paramref name="carriedPage"/>, the page that matches where the last one was left
    /// (slice 4h plan, choice 5). Returns the card, or null for a document the catalog doesn't know.
    /// </summary>
    public async Task<EntryId?> RecordOpenAsync(long documentId, int carriedPage = 0, CancellationToken ct = default)
    {
        if (await entries.GetCardAsync(documentId, ct) is not { } card) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var state = await db.ReadingStates.SingleOrDefaultAsync(r => r.EntryId == card.Value, ct);
        if (state is null) db.ReadingStates.Add(state = new ReadingState { EntryId = card.Value, DocumentId = documentId });
        else if (state.DocumentId != documentId)
        {
            state.DocumentId = documentId;
            state.PageIndex = Math.Max(0, carriedPage);
        }
        state.OpenedUtc = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return card;
    }

    /// <summary>Remembers the page a reader left a document at, after an ordinary open (not from a search hit).</summary>
    public async Task SavePositionAsync(long documentId, int pageIndex, CancellationToken ct = default)
    {
        if (await entries.GetCardAsync(documentId, ct) is not { } card) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.ReadingStates.Where(r => r.EntryId == card.Value && r.DocumentId == documentId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.PageIndex, Math.Max(0, pageIndex)), ct);
    }

    /// <summary>The page a reader left this document at, or 0 when it hasn't been read here, or another copy was read last.</summary>
    public async Task<int> GetPositionAsync(long documentId, CancellationToken ct = default) =>
        await GetPlaceAsync(documentId, ct) is { } place && place.DocumentId == documentId ? place.PageIndex : 0;

    /// <summary>
    /// Where the book a document shows under was left: the copy read last and its page. Null when the book hasn't been
    /// opened, or the catalog doesn't know the document.
    /// </summary>
    public async Task<(long DocumentId, int PageIndex)?> GetPlaceAsync(long documentId, CancellationToken ct = default)
    {
        if (await entries.GetCardAsync(documentId, ct) is not { } card) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var state = await db.ReadingStates.AsNoTracking().Where(r => r.EntryId == card.Value).Select(r => new { r.DocumentId, r.PageIndex }).FirstOrDefaultAsync(ct);
        return state is null ? null : (state.DocumentId, state.PageIndex);
    }

    /// <summary>
    /// Moves where a book was left from one copy to the matching page of another, as when a new version of it becomes
    /// the one it opens (slice 4h plan, choice 5). Nothing changes when the book was last read in another copy.
    /// </summary>
    public async Task<bool> MoveAsync(long fromDocumentId, long toDocumentId, int pageIndex, CancellationToken ct = default)
    {
        if (await entries.GetCardAsync(toDocumentId, ct) is not { } card) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ReadingStates.Where(r => r.EntryId == card.Value && r.DocumentId == fromDocumentId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.DocumentId, toDocumentId).SetProperty(r => r.PageIndex, Math.Max(0, pageIndex)), ct) > 0;
    }

    /// <summary>When these entries were last opened, or every opened entry when <paramref name="entryIds"/> is null.</summary>
    public async Task<IReadOnlyDictionary<EntryId, DateTime>> GetOpenedAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var states = db.ReadingStates.AsNoTracking();
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).ToList();
            states = states.Where(r => ids.Contains(r.EntryId));
        }
        return await states.ToDictionaryAsync(r => new EntryId(r.EntryId), r => r.OpenedUtc, ct);
    }
}
