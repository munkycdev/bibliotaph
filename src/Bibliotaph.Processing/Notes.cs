using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// Notes (slice 3 plan, choice 18): each book's own note, which search finds, and notes on pages of a book, which the
/// reader lists and run mode shows. A book's note is projected to index.db after each save; page notes aren't searched yet.
/// </summary>
public sealed class NotesService(NoteStore notes, EntryStore entries, IndexQueries index, MetadataProjector projector)
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

    /// <summary>New text for a page note and, when they changed, new pages of the same document.</summary>
    public async Task<PageNoteInfo?> UpdatePageNoteAsync(PageNoteInfo note, string text, int firstPage, int lastPage, CancellationToken ct = default)
    {
        var range = firstPage == note.Range.FirstPdfPage && lastPage == note.Range.LastPdfPage ? null : await MakeRangeAsync(note.Range.DocumentId, firstPage, lastPage, ct);
        var updated = await notes.UpdatePageNoteAsync(note.Id, text, range, ct);
        if (updated is not null) PageNotesChanged?.Invoke(this, note.EntryId);
        return updated;
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

    /// <summary>A page range with the printed labels and fingerprints of its first and last pages, as index.db has them now.</summary>
    async Task<PageRange> MakeRangeAsync(long documentId, int firstPage, int lastPage, CancellationToken ct)
    {
        var (first, last) = (Math.Min(firstPage, lastPage), Math.Max(firstPage, lastPage));
        var a = await index.GetPageMarkAsync(documentId, first, ct);
        var b = first == last ? a : await index.GetPageMarkAsync(documentId, last, ct);
        return new PageRange(documentId, first, last, a?.Label, b?.Label, a?.Fingerprint, b?.Fingerprint);
    }
}
