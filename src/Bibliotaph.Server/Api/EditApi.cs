using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// The changes the details drawer makes, as the Windows inspector makes them: a field's value typed, kept, used,
/// rejected or reset, Cataloged wrong… and its look-alikes, the heart, the book's note, Reprocess, adventures marked
/// in a file, a pack's cover and split, and a book's copies. Each answers with the details as they are now, or what
/// it did with the token that undoes it.
/// </summary>
public static class EditApi
{
    public static void MapEditApi(this RouteGroupBuilder api)
    {
        var book = api.MapGroup("/library/books/{entryId:long}");

        book.MapPost("/fields/{key}", (long entryId, string key, FieldEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            EditAsync(libraries, principal, entryId, key, async (metadata, field) =>
                (await metadata.SetAsync(new EntryId(entryId), field, edit.Text, ct))?.Message, true, ct));

        book.MapPost("/fields/{key}/keep", (long entryId, string key, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            EditAsync(libraries, principal, entryId, key, async (metadata, field) =>
            {
                await metadata.ConfirmAsync(new EntryId(entryId), field, ct);
                return null;
            }, false, ct));

        book.MapPost("/fields/{key}/use", (long entryId, string key, FieldEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            EditAsync(libraries, principal, entryId, key, async (metadata, field) =>
            {
                await metadata.UseAsync(new EntryId(entryId), field, edit.Text, ct);
                return null;
            }, true, ct));

        book.MapPost("/fields/{key}/reject", (long entryId, string key, FieldEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            EditAsync(libraries, principal, entryId, key, async (metadata, field) =>
            {
                await metadata.RejectAsync(new EntryId(entryId), field, edit.Text, ct);
                return null;
            }, true, ct));

        book.MapPost("/fields/{key}/reset", (long entryId, string key, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            EditAsync(libraries, principal, entryId, key, async (metadata, field) =>
            {
                await metadata.ResetAsync(new EntryId(entryId), field, ct);
                return null;
            }, false, ct));

        // Cataloged wrong… closed: left without a change or a kept value, the book gets a Needs review card that opens it again.
        book.MapPost("/correction", async (long entryId, CorrectionEnd end, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var corrections = lease.Library.Get<CorrectionService>();
            if (end.Decided) await corrections.FinishAsync(new EntryId(entryId), ct);
            else await corrections.LeaveUnfinishedAsync(new EntryId(entryId), ct);
            return Results.NoContent();
        });

        book.MapPut("/favorite", async (long entryId, SetFavorite favorite, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<FavoritesService>().SetAsync([new EntryId(entryId)], favorite.Favorite, ct);
            return Results.NoContent();
        });

        book.MapGet("/notes", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await NotesAsync(lease.Library, new EntryId(entryId), ct);
        });

        book.MapPut("/notes", async (long entryId, FieldEdit note, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<NotesService>().SetEntryNoteAsync(new EntryId(entryId), note.Text, ct);
            return await NotesAsync(lease.Library, new EntryId(entryId), ct);
        });

        book.MapPost("/reprocess", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            if (await lease.Library.Get<LibraryQueries>().GetDetailsAsync(new EntryId(entryId), ct) is not { Entry: { IsElsewhere: false } entry })
                return Results.NotFound();
            await lease.Library.Indexing.ReprocessAsync(entry.DocumentId, ct: ct);
            return Results.Ok(await DetailsApi.DetailsAsync(lease.Library, new EntryId(entryId), ct));
        });

        // Adventures inside a file: pages are typed as the file numbers them.
        book.MapPost("/parts", async (long entryId, PartEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryQueries>().GetDetailsAsync(new EntryId(entryId), ct) is not { Entry: { IsPart: false, IsPack: false, IsElsewhere: false } file }
                || file.Format != SourceFormats.Pdf)
                return Results.NotFound();
            var title = edit.Title?.Trim() ?? "";
            if (title.Length == 0) return Results.Ok(new EditResult(null, "Give the adventure a title."));
            var parts = library.Get<PartsService>();
            if (await PagesAsync(parts, file.DocumentId, edit, ct) is not { } pages) return Results.Ok(new EditResult(null, DetailsText.PagesHint));
            var added = await parts.AddAsync(file.DocumentId, pages, title, ct);
            return Results.Ok(added.Problem is { } problem ? new EditResult(null, problem)
                : new EditResult(await DetailsApi.DetailsAsync(library, new EntryId(entryId), ct), Message: DetailsText.PartAdded(title)));
        });

        book.MapPut("/pages", async (long entryId, PartEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryQueries>().GetDetailsAsync(new EntryId(entryId), ct) is not { Entry: { IsPart: true } part })
                return Results.NotFound();
            var parts = library.Get<PartsService>();
            if (await PagesAsync(parts, part.DocumentId, edit, ct) is not { } pages) return Results.Ok(new ActionResult(DetailsText.PagesHint));
            var moved = await parts.SetPagesAsync(part.EntryId, part.DocumentId, pages, ct);
            if (moved.Problem is { } problem) return Results.Ok(new ActionResult(problem));
            var labels = await parts.GetPageLabelsAsync(part.DocumentId, ct);
            return Results.Ok(new ActionResult(DetailsText.PartMoved(part.Title, pages.Describe(labels)), Hold(library, moved.Undo, "The change couldn't be undone.")));
        });

        book.MapDelete("/part", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryQueries>().GetDetailsAsync(new EntryId(entryId), ct) is not { Entry: { IsPart: true } part })
                return Results.NotFound();
            var parts = library.Get<PartsService>();
            if (await parts.RemoveAsync(part.EntryId, ct) is not { } removed) return Results.NotFound();
            return Results.Ok(new ActionResult(DetailsText.PartRemoved(part.Title),
                Hold(library, () => parts.RestoreAsync(removed, CancellationToken.None), "The adventure couldn't be brought back."), removed.ParentEntryId.Value));
        });

        // A pack: its cover, one of its files or the mosaic; and splitting it into a card for each file.
        book.MapPut("/cover", async (long entryId, PackCover cover, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var member = cover.Member is { } m ? new EntryId(m) : (EntryId?)null;
            if (!await lease.Library.Get<PackService>().UseCoverAsync(new EntryId(entryId), member, ct)) return Results.NotFound();
            return Results.Ok(await DetailsApi.DetailsAsync(lease.Library, new EntryId(entryId), ct));
        });

        book.MapPost("/split", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryQueries>().GetDetailsAsync(new EntryId(entryId), ct) is not { Entry: { IsPack: true } pack }) return Results.NotFound();
            var packs = library.Get<PackService>();
            if (await packs.SplitAsync(pack.EntryId, ct) is not { } files) return Results.NotFound();
            var made = pack.PdfMembers > 0 ? CardText.Files(files.Count) : CardText.Images(files.Count);
            return Results.Ok(new ActionResult($"Split {pack.Title} into {made}.",
                Hold(library, () => packs.RepackAsync(pack.EntryId, files, CancellationToken.None), "The split couldn't be undone.")));
        });

        // A book in more than one file: which opens, and a file that isn't the same book after all.
        book.MapPost("/copies/{documentId:long}/current", async (long entryId, long documentId, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<CopiesService>().MakeCurrentAsync(new EntryId(entryId), documentId, ct);
            return Results.Ok(new ActionResult("", Show: entryId));
        });

        book.MapPost("/copies/{documentId:long}/not-same", async (long entryId, long documentId, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var card = await lease.Library.Get<CopiesService>().NotSameBookAsync(new EntryId(entryId), documentId, ct);
            return Results.Ok(new ActionResult("", Show: (card ?? new EntryId(entryId)).Value));
        });

        api.MapGet("/library/fields/{key}/resolution", async (string key, string? text, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            if (MetadataFields.Find(key) is not { } field) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Results.Ok(new Resolution(FieldText.Resolution(field, text, await lease.Library.Get<VocabularyStore>().GetAsync(ct))));
        });

        // An Undo, or Change them: what the server held for the token it gave.
        api.MapPost("/library/held/{token}", async (string token, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            if (lease.Library.Get<HeldActions>().Take(token) is not { } action) return Results.Ok(new ActionResult("That can't be done any more."));
            return Results.Ok(await action(ct));
        });
    }

    /// <summary>
    /// A change to one field, then the details as they are now. After a value was typed, chosen or rejected, the books
    /// from the same folder that showed what this one did are offered the same correction, as on Windows.
    /// </summary>
    static async Task<IResult> EditAsync(UserLibraries libraries, ClaimsPrincipal principal, long entryId, string key,
        Func<MetadataService, MetadataField, Task<string?>> change, bool offers, CancellationToken ct)
    {
        if (MetadataFields.Find(key) is not { } field) return Results.NotFound();
        await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
        var library = lease.Library;
        var id = new EntryId(entryId);
        if (await library.Get<LibraryQueries>().GetDetailsAsync(id, ct) is null) return Results.NotFound();
        var store = library.Get<MetadataStore>();
        var before = (await store.GetAsync(id, ct)).Compute()[field];
        if (await change(library.Get<MetadataService>(), field) is { } problem) return Results.Ok(new EditResult(null, problem));

        CorrectionOffer? offer = null;
        if (offers)
        {
            var after = (await store.GetAsync(id, ct)).Compute()[field];
            if (await library.Get<CorrectionService>().FindLookAlikesAsync(id, before, after, ct) is { } found)
                offer = new CorrectionOffer(library.Get<HeldActions>().Hold(c => ApplyLookAlikesAsync(library, found, c)),
                    DetailsText.LookAlikeQuestion(found), DetailsText.LookAlikeDetail(found));
        }
        return Results.Ok(new EditResult(await DetailsApi.DetailsAsync(library, id, ct), null, offer));
    }

    /// <summary>Change them: the look-alikes corrected as a bulk edit, whose Undo takes it all back.</summary>
    static async Task<ActionResult> ApplyLookAlikesAsync(UserLibrary library, LookAlikeOffer offer, CancellationToken ct)
    {
        MetadataProblem? problem;
        IReadOnlyList<FieldSnapshot>? undo;
        try
        {
            (problem, undo) = await library.Get<CorrectionService>().ApplyAsync(offer, ct: ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            library.Get<ILogger<HeldActions>>().LogError(e, "Correcting {Count} look-alikes failed", offer.Books.Count);
            return new ActionResult("The other books couldn't be changed.");
        }
        if (problem is not null) return new ActionResult($"The other books couldn't be changed: {problem.Message}");
        var books = undo?.Select(s => s.EntryId).Distinct().Count() ?? 0;
        var metadata = library.Get<MetadataService>();
        return new ActionResult(DetailsText.LookAlikesChanged(books, offer.FolderName),
            books > 0 && undo is not null ? Hold(library, () => metadata.UndoManyAsync(undo, ct: CancellationToken.None), "The edit couldn't be undone.") : null);
    }

    /// <summary>
    /// Keeps an Undo for the client to ask for by its token. Undone, the note goes, as on Windows; what couldn't be
    /// undone says so in <paramref name="failed"/>.
    /// </summary>
    static string? Hold(UserLibrary library, Func<Task>? undo, string failed) =>
        undo is null ? null : library.Get<HeldActions>().Hold(async _ =>
        {
            try
            {
                await undo();
                return new ActionResult("");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                library.Get<ILogger<HeldActions>>().LogError(e, "Undoing a change failed");
                return new ActionResult(failed);
            }
        });

    static async Task<PartPages?> PagesAsync(PartsService parts, long documentId, PartEdit edit, CancellationToken ct)
    {
        var labels = await parts.GetPageLabelsAsync(documentId, ct);
        if (PageNumbers.Find(edit.From, labels, labels.Count) is not { } first) return null;
        var last = edit.To.Trim().Length == 0 ? first : PageNumbers.Find(edit.To, labels, labels.Count);
        return last is { } l ? new PartPages(first, l) : null;
    }

    /// <summary>The Notes tab: the book's own note, and the notes on its pages where they open now.</summary>
    static async Task<BookNotes> NotesAsync(UserLibrary library, EntryId entryId, CancellationToken ct)
    {
        var notes = library.Get<NotesService>();
        var text = await notes.GetEntryNoteAsync(entryId, ct) ?? "";
        var pageNotes = await notes.GetPageNotesAsync(entryId, ct);
        var places = await notes.ListAsync(pageNotes, ct: ct);
        var lines = pageNotes.Select(n =>
        {
            var shown = places.GetValueOrDefault(n.Id);
            var reason = shown?.Place is { State: not (PagePlaceState.Ready or PagePlaceState.OtherCopy) } place ? place.Reason : null;
            return new PageNoteLine(DetailsText.Pages(shown?.Pages ?? n.Range), n.Text, reason, shown?.Place.State == PagePlaceState.Changed);
        }).ToList();
        return new BookNotes(text, lines, DetailsText.NotesLabel((text.Trim().Length > 0 ? 1 : 0) + lines.Count));
    }
}
