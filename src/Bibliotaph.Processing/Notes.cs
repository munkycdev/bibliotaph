using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// A page note as a list shows it: where it opens now, and the pages it is on in the copy the list is about, with that
/// copy's printed page numbers. <see cref="Found"/> is false for a note made in another copy whose pages weren't found
/// in this one, so these are the same printed page there, or else the same page, which may not be the right one.
/// </summary>
public sealed record PageNotePages(PagePlace Place, PageRange Pages, bool Found = true);

/// <summary>
/// Notes: each book's own note, and notes on pages of a book, which the reader lists and a binder's reader shows.
/// Search finds a book by the words of either: they are projected to index.db after each change. A page note opens
/// where its pages are in the book's current copy, found as a session item's are, and lists at the pages it opens
/// at, or in a reader at its pages in the copy open there.
/// </summary>
public sealed class NotesService(NoteStore notes, EntryStore entries, MetadataProjector projector, PagePlaces places)
{
    EventHandler<EntryId>? _pageNotesChanged;

    /// <summary>
    /// Raised after a page note was added, changed or deleted, with the book it is on, on the thread that changed it;
    /// also after Use this page on a Needs review card moved one (<see cref="PagePlaces.PageNoteMoved"/>).
    /// </summary>
    public event EventHandler<EntryId>? PageNotesChanged
    {
        add
        {
            _pageNotesChanged += value;
            places.PageNoteMoved += value;
        }
        remove
        {
            _pageNotesChanged -= value;
            places.PageNoteMoved -= value;
        }
    }

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
        if (added is not null) await ChangedAsync(entry.EntryId, ct);
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
        if (updated is not null) await ChangedAsync(note.EntryId, ct);
        return updated;
    }

    /// <summary>Where each page note opens now, by note: in the book's current copy, as a session item would.</summary>
    public async Task<IReadOnlyDictionary<long, PagePlace>> ResolveAsync(IReadOnlyList<PageNoteInfo> pageNotes, CancellationToken ct = default)
    {
        var resolved = await places.ResolveAsync([.. pageNotes.Select(n => (n.EntryId, n.Range))], ct);
        var result = new Dictionary<long, PagePlace>(pageNotes.Count);
        for (var i = 0; i < pageNotes.Count; i++) result[pageNotes[i].Id] = resolved[i];
        return result;
    }

    /// <summary>
    /// Each page note as a list shows it, by note. In the details (<paramref name="documentId"/> null), at the pages it
    /// opens at now, in the version the book opens, so a note a new version moved lists at its new page. In a reader
    /// open on <paramref name="documentId"/>, at its pages in that copy: its own when it was made there, where it opens
    /// when that copy is the one it opens in, and otherwise looked for there by fingerprint and printed page number.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, PageNotePages>> ListAsync(IReadOnlyList<PageNoteInfo> pageNotes, long? documentId = null, CancellationToken ct = default)
    {
        var opens = await ResolveAsync(pageNotes, ct);
        var result = new Dictionary<long, PageNotePages>(pageNotes.Count);
        foreach (var note in pageNotes)
        {
            var place = opens[note.Id];
            var range = note.Range;
            var copy = documentId ?? (place.CanOpen ? place.DocumentId!.Value : range.DocumentId);
            if (copy == range.DocumentId) result[note.Id] = new PageNotePages(place, range);
            else if (place.CanOpen && place.DocumentId == copy) result[note.Id] = new PageNotePages(place, await MakeRangeAsync(copy, place.FirstPage, place.LastPage, ct));
            else
            {
                var (first, last, found) = await places.MatchAsync(range, copy, ct);
                result[note.Id] = new PageNotePages(place, await MakeRangeAsync(copy, first, last, ct), found);
            }
        }
        return result;
    }

    /// <summary>"Use this page" on a note whose page changed: the note is on these pages of the open document from now on.</summary>
    public async Task<PageNoteInfo?> UsePageAsync(long noteId, long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        var used = await notes.RepointAsync(noteId, await MakeRangeAsync(documentId, firstPage, lastPage, ct), ct);
        if (used is not null) await ChangedAsync(used.EntryId, ct);
        return used;
    }

    public async Task<PageNoteInfo?> DeletePageNoteAsync(long noteId, CancellationToken ct = default)
    {
        var deleted = await notes.DeletePageNoteAsync(noteId, ct);
        if (deleted is not null) await ChangedAsync(deleted.EntryId, ct);
        return deleted;
    }

    public async Task<PageNoteInfo?> RestorePageNoteAsync(PageNoteInfo deleted, CancellationToken ct = default)
    {
        var restored = await notes.RestorePageNoteAsync(deleted, ct);
        if (restored is not null) await ChangedAsync(deleted.EntryId, ct);
        return restored;
    }

    /// <summary>
    /// After a page note came, went or changed: readers list the book's notes again, at once, and search finds the book by
    /// its words once they are projected, which can wait behind another projection.
    /// </summary>
    async Task ChangedAsync(EntryId entryId, CancellationToken ct)
    {
        _pageNotesChanged?.Invoke(this, entryId);
        await projector.ProjectMarksAsync([entryId], ct);
    }

    Task<PageRange> MakeRangeAsync(long documentId, int firstPage, int lastPage, CancellationToken ct) => places.MakeRangeAsync(documentId, firstPage, lastPage, ct);
}
