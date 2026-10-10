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
    /// is read; another copy starts again from its first page. Returns the card, or null for a document the catalog
    /// doesn't know.
    /// </summary>
    public async Task<EntryId?> RecordOpenAsync(long documentId, CancellationToken ct = default)
    {
        if (await entries.GetCardAsync(documentId, ct) is not { } card) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var state = await db.ReadingStates.SingleOrDefaultAsync(r => r.EntryId == card.Value, ct);
        if (state is null) db.ReadingStates.Add(state = new ReadingState { EntryId = card.Value, DocumentId = documentId });
        else if (state.DocumentId != documentId)
        {
            state.DocumentId = documentId;
            state.PageIndex = 0;
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
    public async Task<int> GetPositionAsync(long documentId, CancellationToken ct = default)
    {
        if (await entries.GetCardAsync(documentId, ct) is not { } card) return 0;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ReadingStates.AsNoTracking().Where(r => r.EntryId == card.Value && r.DocumentId == documentId)
            .Select(r => r.PageIndex).FirstOrDefaultAsync(ct);
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
