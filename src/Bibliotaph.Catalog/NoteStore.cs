using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A note on pages of a book: the pages, as a session item keeps them, and the text.</summary>
public sealed record PageNoteInfo(long Id, EntryId EntryId, PageRange Range, string Text, DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>
/// Notes (slice 3 plan, choice 18): one plain-text note per entry, and notes on pages or page ranges of its documents.
/// Blank text is no note: saving it deletes the note.
/// </summary>
public sealed class NoteStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The entry's own note, or null.</summary>
    public async Task<string?> GetEntryNoteAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Notes.Where(n => n.EntryId == entryId.Value && n.PageRefId == null).Select(n => n.Text).SingleOrDefaultAsync(ct);
    }

    /// <summary>The own notes of these entries, or of every entry when <paramref name="entryIds"/> is null, for search.</summary>
    public async Task<IReadOnlyDictionary<EntryId, string>> GetEntryNotesAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.Notes.AsNoTracking().Where(n => n.PageRefId == null);
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).ToList();
            query = query.Where(n => ids.Contains(n.EntryId));
        }
        return (await query.Select(n => new { n.EntryId, n.Text }).ToListAsync(ct)).ToDictionary(n => new EntryId(n.EntryId), n => n.Text);
    }

    /// <summary>Saves the entry's own note as typed; blank deletes it. Returns whether anything changed.</summary>
    public async Task<bool> SetEntryNoteAsync(EntryId entryId, string? text, CancellationToken ct = default)
    {
        var clean = Clean(text);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var note = await db.Notes.SingleOrDefaultAsync(n => n.EntryId == entryId.Value && n.PageRefId == null, ct);
        if (note is null)
        {
            if (clean is null || !await db.Entries.AnyAsync(e => e.Id == entryId.Value, ct)) return false;
            var now = Now;
            db.Notes.Add(new Note { EntryId = entryId.Value, Text = clean, CreatedUtc = now, UpdatedUtc = now });
        }
        else if (clean is null) db.Notes.Remove(note);
        else if (note.Text == clean) return false;
        else
        {
            note.Text = clean;
            note.UpdatedUtc = Now;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The entry's page notes, in page order.</summary>
    public async Task<IReadOnlyList<PageNoteInfo>> GetPageNotesAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var notes = await db.Notes.AsNoTracking().Include(n => n.PageRef).Where(n => n.EntryId == entryId.Value && n.PageRef != null).ToListAsync(ct);
        return [.. notes.Select(Info).OrderBy(n => n.Range.FirstPdfPage).ThenBy(n => n.Range.LastPdfPage).ThenBy(n => n.Id)];
    }

    /// <summary>A note on pages of one of the entry's documents. Null when the entry or the document is gone, or the text is blank.</summary>
    public async Task<PageNoteInfo?> AddPageNoteAsync(EntryId entryId, PageRange range, string text, CancellationToken ct = default)
    {
        if (Clean(text) is not { } clean) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.Entries.AnyAsync(e => e.Id == entryId.Value, ct) || !await db.Documents.AnyAsync(d => d.Id == range.DocumentId, ct)) return null;
        var now = Now;
        var note = new Note { EntryId = entryId.Value, PageRef = ToPageRef(range), Text = clean, CreatedUtc = now, UpdatedUtc = now };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return Info(note);
    }

    /// <summary>New text, and maybe new pages, for a page note. Returns it as it is now, or null if it's gone.</summary>
    public async Task<PageNoteInfo?> UpdatePageNoteAsync(long noteId, string text, PageRange? range = null, CancellationToken ct = default)
    {
        if (Clean(text) is not { } clean) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Notes.Include(n => n.PageRef).SingleOrDefaultAsync(n => n.Id == noteId && n.PageRefId != null, ct) is not { } note) return null;
        note.Text = clean;
        note.UpdatedUtc = Now;
        if (range is not null && note.PageRef is { } kept && kept.CheckDocumentId == range.DocumentId)
        {
            // Moved within the new version it was looked for in: the user has checked it there (slice 4h plan, choice 3).
            PageRefStore.Use(kept, range);
        }
        else if (range is not null)
        {
            // The note moves to its new range before the old one goes, so the cascade from the old range can't take it.
            var old = note.PageRef;
            note.PageRef = ToPageRef(range);
            db.ChangeTracker.DetectChanges();
            if (old is not null) db.PageRefs.Remove(old);
        }
        await db.SaveChangesAsync(ct);
        return Info(note);
    }

    /// <summary>
    /// "Use this page" on a note whose page changed (slice 4h plan, choice 2): the note is on these pages from now on,
    /// checked by the user. Returns it as it is now, or null if it's gone.
    /// </summary>
    public async Task<PageNoteInfo?> RepointAsync(long noteId, PageRange range, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Notes.Include(n => n.PageRef).SingleOrDefaultAsync(n => n.Id == noteId && n.PageRefId != null, ct) is not { PageRef: { } pages } note) return null;
        PageRefStore.Use(pages, range);
        await db.SaveChangesAsync(ct);
        return Info(note);
    }

    /// <summary>Deletes a page note and its page reference. Returns it, for Undo, or null if it wasn't there.</summary>
    public async Task<PageNoteInfo?> DeletePageNoteAsync(long noteId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Notes.Include(n => n.PageRef).SingleOrDefaultAsync(n => n.Id == noteId && n.PageRefId != null, ct) is not { } note) return null;
        var info = Info(note);
        db.Notes.Remove(note);
        if (note.PageRef is { } pages) db.PageRefs.Remove(pages);
        await db.SaveChangesAsync(ct);
        return info;
    }

    /// <summary>Undo after Delete: the note again, on its pages, as it was. Null if its book or document has gone since.</summary>
    public async Task<PageNoteInfo?> RestorePageNoteAsync(PageNoteInfo deleted, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.Entries.AnyAsync(e => e.Id == deleted.EntryId.Value, ct) || !await db.Documents.AnyAsync(d => d.Id == deleted.Range.DocumentId, ct)) return null;
        var note = new Note
        {
            EntryId = deleted.EntryId.Value,
            PageRef = ToPageRef(deleted.Range),
            Text = deleted.Text,
            CreatedUtc = deleted.CreatedUtc,
            UpdatedUtc = deleted.UpdatedUtc,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return Info(note);
    }

    static PageNoteInfo Info(Note n) => new(n.Id, new EntryId(n.EntryId), PageRefStore.ToRange(n.PageRef!), n.Text, n.CreatedUtc, n.UpdatedUtc);

    static PageRef ToPageRef(PageRange range) => PageRefStore.ToPageRef(range);

    /// <summary>The text as typed, without the blank lines and spaces around it; null for none.</summary>
    static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
