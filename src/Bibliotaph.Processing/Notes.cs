using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// Notes (slice 3 plan, choice 18): each book's own note, which search finds, and notes on pages of a book, which the
/// reader lists and run mode shows. A book's note is projected to index.db after each save; page notes aren't searched yet.
/// A page note opens where its pages are in the book's current copy, found as a session item's are (slice 4h plan, choice 2).
/// </summary>
public sealed class NotesService(NoteStore notes, EntryStore entries, MetadataProjector projector, PagePlaces places)
{
    /// <summary>Raised after a page note was added, changed or deleted, with the book it is on, on the thread that changed it.</summary>
    public event EventHandler<EntryId>? PageNotesChanged;

    public Task<string?> GetEntryNoteAsync(EntryId entryId, CancellationToken ct = default) => notes.GetEntryNoteAsync(entryId, ct);

    /// <summary>The book's own note, saved as it is typed; blank deletes it. Search finds it once it is projected.</summary>
    public async Task SetEntryNoteAsync(EntryId entryId, string? text, CancellationToken ct = default)
    {
        if (await notes.SetEntryNoteAsync(entryId, text, ct)) await projector.ProjectMarksAsync([entryId], ct);
    }

    public Task<IReadOnlyList<PageNoteInfo>> GetPageNotesAsync(EntryId entryId, CancellationToken ct = default) => notes.GetPageNotesAsync(entryId, ct);

    /// <summary>The book a document shows under, whose page notes the reader lists.</summary>
    public async Task<EntryId?> GetEntryAsync(long documentId, CancellationToken ct = default) => (await entries.GetEntryAsync(documentId, ct))?.EntryId;

    /// <summary>The page notes of the book a document belongs to, from all its copies.</summary>
    public async Task<IReadOnlyList<PageNoteInfo>> GetPageNotesForDocumentAsync(long documentId, CancellationToken ct = default) =>
        await entries.GetEntryAsync(documentId, ct) is { } entry ? await notes.GetPageNotesAsync(entry.EntryId, ct) : [];

    /// <summary>A note on pages of the document the reader has open, on the book it shows under.</summary>
    public async Task<PageNoteInfo?> AddPageNoteAsync(long documentId, int firstPage, int lastPage, string text, CancellationToken ct = default)
    {
        if (await entries.GetEntryAsync(documentId, ct) is not { } entry) return null;
        var added = await notes.AddPageNoteAsync(entry.EntryId, await MakeRangeAsync(documentId, firstPage, lastPage, ct), text, ct);
        if (added is not null) PageNotesChanged?.Invoke(this, entry.EntryId);
        return added;
    }

    /// <summary>
    /// New text for a page note and, when they changed, new pages: of <paramref name="documentId"/>, the document the
    /// reader has the note open in, which can be another copy of the book than the note's.
    /// </summary>
    public async Task<PageNoteInfo?> UpdatePageNoteAsync(PageNoteInfo note, string text, long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        var same = documentId == note.Range.DocumentId && firstPage == note.Range.FirstPdfPage && lastPage == note.Range.LastPdfPage;
        var range = same ? null : await MakeRangeAsync(documentId, firstPage, lastPage, ct);
        var updated = await notes.UpdatePageNoteAsync(note.Id, text, range, ct);
        if (updated is not null) PageNotesChanged?.Invoke(this, note.EntryId);
        return updated;
    }

    /// <summary>Where each page note opens now, by note: in the book's current copy, as a session item would (choice 2).</summary>
    public async Task<IReadOnlyDictionary<long, PagePlace>> ResolveAsync(IReadOnlyList<PageNoteInfo> pageNotes, CancellationToken ct = default)
    {
        var resolved = await places.ResolveAsync([.. pageNotes.Select(n => (n.EntryId, n.Range))], ct);
        var result = new Dictionary<long, PagePlace>(pageNotes.Count);
        for (var i = 0; i < pageNotes.Count; i++) result[pageNotes[i].Id] = resolved[i];
        return result;
    }

    /// <summary>"Use this page" on a note whose page changed (choice 2): the note is on these pages of the open document from now on.</summary>
    public async Task<PageNoteInfo?> UsePageAsync(long noteId, long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        var used = await notes.RepointAsync(noteId, await MakeRangeAsync(documentId, firstPage, lastPage, ct), ct);
        if (used is not null) PageNotesChanged?.Invoke(this, used.EntryId);
        return used;
    }

    public async Task<PageNoteInfo?> DeletePageNoteAsync(long noteId, CancellationToken ct = default)
    {
        var deleted = await notes.DeletePageNoteAsync(noteId, ct);
        if (deleted is not null) PageNotesChanged?.Invoke(this, deleted.EntryId);
        return deleted;
    }

    public async Task<PageNoteInfo?> RestorePageNoteAsync(PageNoteInfo deleted, CancellationToken ct = default)
    {
        var restored = await notes.RestorePageNoteAsync(deleted, ct);
        if (restored is not null) PageNotesChanged?.Invoke(this, deleted.EntryId);
        return restored;
    }

    Task<PageRange> MakeRangeAsync(long documentId, int firstPage, int lastPage, CancellationToken ct) => places.MakeRangeAsync(documentId, firstPage, lastPage, ct);
}
