using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>What points at a page reference.</summary>
public enum PlaceKind
{
    SessionItem,
    PageNote,
}

/// <summary>
/// A place in a book that user work points at (slice 4h): a session item's pages or a page note's, with the book it
/// belongs to. <see cref="Text"/> is the item's label (null shows the book's title) or the note's text, and
/// <see cref="PackTitle"/> the item's session.
/// </summary>
public sealed record PlacedPages(PlaceKind Kind, long OwnerId, EntryId EntryId, PageRange Range, string? Text, string? PackTitle = null);

/// <summary>
/// Page references as places in books (slice 4h plan, choices 2 and 4): session items' and page notes' pages alike,
/// with what was found when they were looked for in a new version of their book, kept so a session or a note shows it
/// without looking again and Needs review can list the places that need a look.
/// </summary>
public sealed class PageRefStore(IDbContextFactory<CatalogDbContext> contexts)
{
    /// <summary>The places on an entry's pages: its session items with pages, and its page notes.</summary>
    public async Task<IReadOnlyList<PlacedPages>> GetForEntryAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await PlacesAsync(db, db.PageRefs.AsNoTracking(), entryId, ct);
    }

    /// <summary>
    /// Every place whose pages have been looked for in another version of their book, for Needs review; with
    /// <paramref name="needingALook"/>, only those whose pages weren't found.
    /// </summary>
    public async Task<IReadOnlyList<PlacedPages>> GetCheckedAsync(bool needingALook = false, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var refs = db.PageRefs.AsNoTracking().Where(r => needingALook ? r.Check == PageCheck.NeedsLook : r.Check != null);
        return await PlacesAsync(db, refs, null, ct);
    }

    static async Task<IReadOnlyList<PlacedPages>> PlacesAsync(CatalogDbContext db, IQueryable<PageRef> refs, EntryId? entryId, CancellationToken ct)
    {
        var entry = entryId?.Value;
        var items = await db.SessionItems.AsNoTracking()
            .Where(i => i.PageRefId != null && (entry == null || i.EntryId == entry))
            .Join(refs, i => i.PageRefId!.Value, r => r.Id, (i, r) => new { i.Id, i.EntryId, i.Label, i.PackId, Ref = r })
            .Join(db.SessionPacks.AsNoTracking(), i => i.PackId, p => p.Id, (i, p) => new { i.Id, i.EntryId, i.Label, i.Ref, p.Title })
            .ToListAsync(ct);
        var notes = await db.Notes.AsNoTracking()
            .Where(n => n.PageRefId != null && (entry == null || n.EntryId == entry))
            .Join(refs, n => n.PageRefId!.Value, r => r.Id, (n, r) => new { n.Id, n.EntryId, n.Text, Ref = r })
            .ToListAsync(ct);
        return
        [
            .. items.Select(i => new PlacedPages(PlaceKind.SessionItem, i.Id, new EntryId(i.EntryId), ToRange(i.Ref), i.Label, i.Title)),
            .. notes.Select(n => new PlacedPages(PlaceKind.PageNote, n.Id, new EntryId(n.EntryId), ToRange(n.Ref), n.Text)),
        ];
    }

    /// <summary>
    /// Keeps what was found for each page reference, or clears it (null). A reference the user has since pointed at that
    /// version, or checked there, keeps what they chose. Returns how many were saved.
    /// </summary>
    public async Task<int> SaveChecksAsync(IReadOnlyCollection<(long RefId, PageRefCheck? Check)> checks, CancellationToken ct = default)
    {
        if (checks.Count == 0) return 0;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var saved = 0;
        // In batches, under SQLite's limit on bound values.
        foreach (var batch in checks.Chunk(500))
        {
            var ids = batch.Select(c => c.RefId).ToList();
            var refs = await db.PageRefs.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var (refId, check) in batch)
            {
                if (!refs.TryGetValue(refId, out var pages)) continue;
                // Looked for while the user chose its page: their choice stands.
                if (check is not null && (pages.DocumentId == check.DocumentId
                    || pages is { Check: PageCheck.Checked } && pages.CheckDocumentId == check.DocumentId)) continue;
                SetCheck(pages, check);
                saved++;
            }
            await db.SaveChangesAsync(ct);
        }
        return saved;
    }

    /// <summary>
    /// "Use this page" from Needs review (slice 4h plan, choice 3): the place points at these pages from now on,
    /// checked. Returns the pages as they were, for Undo, or null when the place is gone.
    /// </summary>
    public async Task<PageRange?> UseAsync(long refId, PageRange range, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.PageRefs.FindAsync([refId], ct) is not { } pages) return null;
        var before = ToRange(pages);
        Use(pages, range);
        await db.SaveChangesAsync(ct);
        return before;
    }

    /// <summary>Undoes <see cref="UseAsync"/>: the place points at the pages it had, with what was found for them.</summary>
    public async Task RestoreAsync(PageRange before, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.PageRefs.FindAsync([before.RefId], ct) is not { } pages) return;
        Apply(pages, before);
        SetCheck(pages, before.Check);
        await db.SaveChangesAsync(ct);
    }

    internal static PageRange ToRange(PageRef r) => new(r.DocumentId, r.FirstPdfPage, r.LastPdfPage, r.FirstLabel, r.LastLabel, r.FirstFingerprint, r.LastFingerprint)
    {
        RefId = r.Id,
        Check = r is { Check: { } outcome, CheckDocumentId: { } document, CheckFirstPdfPage: { } first, CheckLastPdfPage: { } last }
            ? new PageRefCheck(outcome, document, first, last)
            : null,
    };

    /// <summary>A new page reference for <paramref name="range"/>, with what was found for it, if anything.</summary>
    internal static PageRef ToPageRef(PageRange range)
    {
        var pages = new PageRef();
        Apply(pages, range);
        SetCheck(pages, range.Check);
        return pages;
    }

    /// <summary>Points a page reference at <paramref name="range"/>, which the user chose: it is checked from now on.</summary>
    internal static PageRef Use(PageRef pages, PageRange range)
    {
        Apply(pages, range);
        SetCheck(pages, new PageRefCheck(PageCheck.Checked, range.DocumentId, pages.FirstPdfPage, pages.LastPdfPage));
        return pages;
    }

    static void Apply(PageRef pages, PageRange range)
    {
        pages.DocumentId = range.DocumentId;
        pages.FirstPdfPage = Math.Min(range.FirstPdfPage, range.LastPdfPage);
        pages.LastPdfPage = Math.Max(range.FirstPdfPage, range.LastPdfPage);
        pages.FirstLabel = range.FirstLabel;
        pages.LastLabel = range.LastLabel;
        pages.FirstFingerprint = range.FirstFingerprint;
        pages.LastFingerprint = range.LastFingerprint;
    }

    static void SetCheck(PageRef pages, PageRefCheck? check)
    {
        pages.Check = check?.Outcome;
        pages.CheckDocumentId = check?.DocumentId;
        pages.CheckFirstPdfPage = check is null ? null : Math.Min(check.FirstPdfPage, check.LastPdfPage);
        pages.CheckLastPdfPage = check is null ? null : Math.Max(check.FirstPdfPage, check.LastPdfPage);
    }
}
