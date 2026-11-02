using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.Nornis;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// Send to Nornis, from the reader and a binder's item: the worlds a book can be filed in as an excerpt, and their
/// addresses on Nornis. Bibliotaph only opens Nornis's page; Nornis reads the pages' text itself and asks the game
/// master to confirm.
/// </summary>
public static class SendToNornis
{
    static readonly NornisSend None = new([]);

    public static void MapSendToNornis(this RouteGroupBuilder api)
    {
        if (NornisOptions.From(api.ServiceProvider().GetRequiredService<IConfiguration>()) is not { } nornis) return;

        api.MapGet("/library/documents/{documentId:long}/nornis", async (long documentId, ClaimsPrincipal principal, UserLibraries libraries,
            StorageConnections connections, CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            if (!await ConnectedAsync(connections, userId, ct)) return None;
            await using var lease = await libraries.OpenAsync(userId, ct);
            return await ForAsync(lease.Library, nornis, documentId, null, ct);
        });

        // Only an item of pages: a whole book isn't an excerpt.
        api.MapGet("/binders/items/{id:long}/nornis", async (long id, ClaimsPrincipal principal, UserLibraries libraries,
            StorageConnections connections, CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            if (!await ConnectedAsync(connections, userId, ct)) return None;
            await using var lease = await libraries.OpenAsync(userId, ct);
            var library = lease.Library;
            if (await library.Get<SessionStore>().GetItemAsync(id, ct) is not { Range: not null } item
                || await library.Get<SessionsService>().ResolveItemAsync(id, ct) is not { DocumentId: { } documentId } target)
                return None;
            return await ForAsync(library, nornis, documentId, (target.FirstPage, target.LastPage, item.Label), ct);
        });
    }

    /// <summary>
    /// The same for a connected PC, by the content of the file it has open: the answer names the web library's own
    /// document, since Nornis reads the pages here, so a file the web library doesn't have can't be sent.
    /// </summary>
    public static void MapPcSendToNornis(this RouteGroupBuilder pc)
    {
        if (NornisOptions.From(pc.ServiceProvider().GetRequiredService<IConfiguration>()) is not { } nornis) return;

        pc.MapGet("/books/{hash}/nornis", async (string hash, int? from, int? to, string? label, ClaimsPrincipal principal, UserLibraries libraries,
            StorageConnections connections, CancellationToken ct) =>
        {
            if (from is null != to is null || from < 0 || to < from || label?.Length > LongestLabel) return Results.BadRequest();
            var userId = principal.UserId()!;
            if (!await ConnectedAsync(connections, userId, ct)) return Results.Ok(None);
            await using var lease = await libraries.OpenAsync(userId, ct);
            if (await lease.Library.Get<LibraryStore>().FindDocumentAsync(hash, ct) is not { } documentId) return Results.NotFound();
            return Results.Ok(await ForAsync(lease.Library, nornis, documentId, from is { } first ? (first, to!.Value, label) : null, ct));
        });
    }

    /// <summary>Longer than any binder item's label.</summary>
    const int LongestLabel = 500;

    static IServiceProvider ServiceProvider(this RouteGroupBuilder api) => ((IEndpointRouteBuilder)api).ServiceProvider;

    /// <summary>Nornis can file nothing for a user who isn't connected to it, so nothing is offered.</summary>
    static async Task<bool> ConnectedAsync(StorageConnections connections, string userId, CancellationToken ct) =>
        (await connections.ListAsync(userId, ct)).Any(c => c.Provider == NornisProvider.Key && !c.NeedsReconnect);

    static Task<NornisSend> ForAsync(UserLibrary library, NornisOptions nornis, long documentId, (int First, int Last, string? Label)? pages,
        CancellationToken ct) =>
        library.Get<NornisScope>().SendAsync(nornis.Origin!, documentId, pages, ct);
}
