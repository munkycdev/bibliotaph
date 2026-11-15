using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/library/organize: what Organize mode's drops do that the Select bar's actions don't, each answering with what
/// it did and the token that undoes it, and the Books panel's books beside a card. Same book and a new pack go through
/// /api/library/selection's dialogs.
/// </summary>
public static class OrganizeApi
{
    public static void MapOrganizeApi(this RouteGroupBuilder api)
    {
        var organize = api.MapGroup("/library/organize");

        organize.MapPost("/goes-with", (OrganizeGoesWith drop, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DoAsync(principal, libraries, (o, c) => o.GoWithAsync(Ids(drop.EntryIds), new EntryId(drop.Book), c), ct));

        organize.MapPost("/not-with-a-book", (SelectedBooks drop, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DoAsync(principal, libraries, (o, c) => o.NotWithABookAsync(Ids(drop.EntryIds), c), ct));

        organize.MapPost("/keep", (SelectedBooks shown, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DoAsync(principal, libraries, (o, c) => o.KeepAsync(Ids(shown.EntryIds), c), ct));

        organize.MapPost("/join-pack", (OrganizeJoinPack drop, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DoAsync(principal, libraries, (o, c) => o.JoinPackAsync(Ids(drop.EntryIds), new EntryId(drop.Pack), c), ct));

        // The Books panel's "In this folder": the books beside a card's files.
        organize.MapGet("/near/{entryId:long}", async (long entryId, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var found = await lease.Library.Get<SourceBooksService>().FindNearAsync(new EntryId(entryId), ct: ct);
            return Results.Ok(found.Select(SourceBooksApi.ToChoice).ToList());
        });
    }

    static List<EntryId> Ids(IReadOnlyList<long> ids) => [.. ids.Distinct().Select(id => new EntryId(id))];

    static async Task<IResult> DoAsync(ClaimsPrincipal principal, UserLibraries libraries, Func<OrganizeService, CancellationToken, Task<OrganizeChange>> change,
        CancellationToken ct)
    {
        await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
        var library = lease.Library;
        var done = await change(library.Get<OrganizeService>(), ct);
        return Results.Ok(new ActionResult(done.Message,
            EditApi.Hold(library, done.Undo is { } undo ? () => undo(CancellationToken.None) : null, OrganizeService.UndoFailed)));
    }
}
