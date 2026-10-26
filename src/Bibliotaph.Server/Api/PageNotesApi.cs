using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/library/documents/{id}/notes: the reader's Notes panel, as the Windows reader's. The notes are the book's, from
/// all its copies; pages come and go as the copy open in the reader numbers them.
/// </summary>
public static class PageNotesApi
{
    public static void MapPageNotesApi(this RouteGroupBuilder api)
    {
        var notes = api.MapGroup("/library/documents/{documentId:long}/notes");

        notes.MapGet("", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await ListAsync(lease.Library, documentId, ct) is { } list ? Results.Ok(list) : Results.NotFound();
        });

        notes.MapPost("", async (long documentId, PageNoteEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (Problem(edit) is { } problem) return problem;
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await AddAsync(lease.Library, documentId, edit, ct) is { } changed ? Results.Ok(changed) : Results.NotFound();
        });

        notes.MapPut("/{noteId:long}", async (long documentId, long noteId, PageNoteEdit edit, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            if (Problem(edit) is { } problem) return problem;
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await UpdateAsync(lease.Library, documentId, noteId, edit, ct) is { } changed ? Results.Ok(changed) : Results.NotFound();
        });

        notes.MapDelete("/{noteId:long}", async (long documentId, long noteId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await DeleteAsync(lease.Library, documentId, noteId, ct) is { } changed ? Results.Ok(changed) : Results.NotFound();
        });
    }

    /// <summary>Note: a note on pages of the copy open in the reader. Null when the document is on no book.</summary>
    public static async Task<ReaderNotesChanged?> AddAsync(UserLibrary library, long documentId, PageNoteEdit edit, CancellationToken ct)
    {
        var added = await library.Get<NotesService>().AddPageNoteAsync(documentId, edit.First, edit.Last, edit.Text.Trim(), ct);
        if (added is null) return null;
        return new ReaderNotesChanged((await ListAsync(library, documentId, ct))!, new ActionResult($"Saved the note on {DetailsText.Pages(added.Range)}."));
    }

    /// <summary>
    /// A note's new text or pages. Pages left as they were stay where the note is, in whichever copy it was made; new
    /// ones are of the open copy.
    /// </summary>
    public static async Task<ReaderNotesChanged?> UpdateAsync(UserLibrary library, long documentId, long noteId, PageNoteEdit edit, CancellationToken ct)
    {
        var service = library.Get<NotesService>();
        var all = await service.GetPageNotesForDocumentAsync(documentId, ct);
        if (all.FirstOrDefault(n => n.Id == noteId) is not { } note) return await GoneAsync(library, documentId, ct);
        var shown = (await service.ListAsync([note], documentId, ct)).GetValueOrDefault(noteId);
        var (first, last) = PagesIn(note, shown, documentId);
        var (document, firstPage, lastPage) = edit.First == first && edit.Last == last
            ? (note.Range.DocumentId, note.Range.FirstPdfPage, note.Range.LastPdfPage)
            : (documentId, edit.First, edit.Last);
        if (await service.UpdatePageNoteAsync(note, edit.Text.Trim(), document, firstPage, lastPage, ct) is null) return await GoneAsync(library, documentId, ct);
        return new ReaderNotesChanged((await ListAsync(library, documentId, ct))!, new ActionResult(""));
    }

    /// <summary>Deletes a note, with Undo.</summary>
    public static async Task<ReaderNotesChanged?> DeleteAsync(UserLibrary library, long documentId, long noteId, CancellationToken ct)
    {
        var service = library.Get<NotesService>();
        var shown = (await ListAsync(library, documentId, ct))?.Notes.FirstOrDefault(n => n.Id == noteId);
        if (await service.DeletePageNoteAsync(noteId, ct) is not { } deleted) return await GoneAsync(library, documentId, ct);
        var undo = EditApi.Hold(library, () => service.RestorePageNoteAsync(deleted, CancellationToken.None), "The note couldn't be put back.");
        return new ReaderNotesChanged((await ListAsync(library, documentId, ct))!,
            new ActionResult($"Deleted the note on {shown?.Pages ?? DetailsText.Pages(deleted.Range)}.", undo));
    }

    /// <summary>The panel's list for the copy open in the reader; null when the document is on no book.</summary>
    public static async Task<ReaderNotes?> ListAsync(UserLibrary library, long documentId, CancellationToken ct)
    {
        var service = library.Get<NotesService>();
        if (await service.GetEntryAsync(documentId, ct) is null) return null;
        var notes = await service.GetPageNotesForDocumentAsync(documentId, ct);
        var places = await service.ListAsync(notes, documentId, ct);
        var rows = notes.Select(note =>
        {
            var shown = places.GetValueOrDefault(note.Id);
            var (first, last) = PagesIn(note, shown, documentId);
            var range = shown?.Pages ?? note.Range;
            var reason = shown?.Place is { State: not (PagePlaceState.Ready or PagePlaceState.OtherCopy) } place ? place.Reason : null;
            var otherCopy = shown?.Found == false || range.DocumentId != documentId;
            return new ReaderNote(note.Id, DetailsText.Pages(range), note.Text, first, last, reason, shown?.Place.State == PagePlaceState.Changed, otherCopy);
        }).ToList();
        return new ReaderNotes(rows, DetailsText.NotesLabel(rows.Count));
    }

    /// <summary>The note's pages in this copy: where they were found here, else its own pages.</summary>
    static (int First, int Last) PagesIn(PageNoteInfo note, PageNotePages? shown, long documentId) =>
        shown?.Pages is { } pages && pages.DocumentId == documentId
            ? (pages.FirstPdfPage, pages.LastPdfPage)
            : (note.Range.FirstPdfPage, note.Range.LastPdfPage);

    static async Task<ReaderNotesChanged?> GoneAsync(UserLibrary library, long documentId, CancellationToken ct) =>
        await ListAsync(library, documentId, ct) is { } list ? new ReaderNotesChanged(list, new ActionResult("That note isn't there any more.")) : null;

    static IResult? Problem(PageNoteEdit edit) =>
        edit.Text.Trim().Length == 0 || edit.First < 0 || edit.Last < edit.First
            ? Results.Problem("A note needs its text and its pages.", statusCode: StatusCodes.Status400BadRequest)
            : null;
}
