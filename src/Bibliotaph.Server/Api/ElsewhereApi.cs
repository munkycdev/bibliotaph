using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Http.Features;

namespace Bibliotaph.Server.Api;

/// <summary>
/// Books owned elsewhere: Add a book I own elsewhere, a cover picture of the user's
/// own, and Remove from library with Undo. A picture is shrunk to cover size as it arrives and only that is kept.
/// </summary>
public static class ElsewhereApi
{
    public static void MapElsewhereApi(this RouteGroupBuilder api)
    {
        api.MapGet("/library/elsewhere/choices", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var vocabulary = await lease.Library.Get<ElsewhereService>().GetVocabularyAsync(ct);
            return new ElsewhereChoices(ElsewhereText.Eyebrow, ElsewhereText.Heading, ElsewhereText.Intro, ElsewhereText.Systems(vocabulary),
                ElsewhereText.Types(vocabulary), [.. ElsewhereText.Owns(vocabulary).Select(t => t.Label)], ElsewhereText.OtherHint, ElsewhereText.OtherTip);
        });

        // Add to library: every value is checked before the card is made; then its cover, if one was picked.
        api.MapPost("/library/elsewhere", async (ElsewhereAdd add, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var draft = ElsewhereText.Draft(add.Title, Blank(add.System), Blank(add.Type), add.Publisher, add.Owns, add.Other);
            var (entryId, problem) = await library.Get<ElsewhereService>().AddAsync(draft, ct);
            if (entryId is not { } added) return new ElsewhereAdded(null, problem?.Message);
            if (add.Cover is { } token && library.Get<PendingCovers>().Take(token) is { } jpeg) await library.Get<ChosenCovers>().KeepAsync(added, jpeg, ct);
            return new ElsewhereAdded(added.Value, problem?.Message);
        });

        // A picture picked in the Add dialog, read now so one that can't be a cover says so before the book is made.
        api.MapPost("/library/elsewhere/cover", async (HttpContext context, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            var (body, tooBig) = await ReadAsync(context, ct);
            if (body is null) return new ChosenCoverRead(null, tooBig);
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var (jpeg, problem) = lease.Library.Get<ChosenCovers>().Read(body);
            return jpeg is null ? new ChosenCoverRead(null, problem) : new ChosenCoverRead(lease.Library.Get<PendingCovers>().Keep(jpeg), null);
        });

        var book = api.MapGroup("/library/books/{entryId:long}");

        // Choose a cover image…: the book's details as they are now, or why the picture can't be its cover.
        book.MapPut("/chosen-cover", async (long entryId, HttpContext context, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            var (body, tooBig) = await ReadAsync(context, ct);
            if (body is null) return Results.Problem(tooBig, statusCode: StatusCodes.Status400BadRequest);
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var covers = lease.Library.Get<ChosenCovers>();
            var (jpeg, problem) = covers.Read(body);
            if (jpeg is null) return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
            if (await covers.KeepAsync(new EntryId(entryId), jpeg, ct) is { } refused)
                return Results.Problem(refused, statusCode: StatusCodes.Status400BadRequest);
            return await DetailsApi.DetailsAsync(lease.Library, new EntryId(entryId), ct) is { } details ? Results.Ok(details) : Results.NotFound();
        });

        book.MapDelete("/chosen-cover", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<ChosenCovers>().RemoveAsync(new EntryId(entryId), ct);
            return await DetailsApi.DetailsAsync(lease.Library, new EntryId(entryId), ct) is { } details ? Results.Ok(details) : Results.NotFound();
        });

        // Remove from library, for a book owned elsewhere only: the user made it, so the user can take it away.
        book.MapDelete("/elsewhere", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await library.Get<LibraryQueries>().GetDetailsAsync(new EntryId(entryId), ct) is not { Entry: { IsElsewhere: true } entry }) return Results.NotFound();
            var elsewhere = library.Get<ElsewhereService>();
            if (await elsewhere.RemoveAsync(entry.EntryId, ct) is not { } removed) return Results.NotFound();
            return Results.Ok(new ActionResult(ElsewhereText.Removed(entry.Title),
                EditApi.Hold(library, () => elsewhere.UndoRemoveAsync(removed, CancellationToken.None), ElsewhereText.UndoRemoveFailed)));
        });
    }

    /// <summary>The picture in the request's body, or why it can't be a cover; one too big isn't read past the limit.</summary>
    static async Task<(MemoryStream? Body, string? Problem)> ReadAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = ChosenCovers.MaxBytes;
        if (context.Request.ContentLength > ChosenCovers.MaxBytes) return (null, ChosenCovers.TooBig);
        if (!UploadMemory.ReserveFor(context, ChosenCovers.MaxBytes)) return (null, UploadMemory.Busy);
        var body = new MemoryStream();
        try
        {
            await context.Request.Body.CopyToAsync(body, ct);
        }
        catch (BadHttpRequestException)
        {
            return (null, ChosenCovers.TooBig);
        }
        body.Position = 0;
        return (body, null);
    }

    static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
