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

    static IServiceProvider ServiceProvider(this RouteGroupBuilder api) => ((IEndpointRouteBuilder)api).ServiceProvider;

    /// <summary>Nornis can file nothing for a user who isn't connected to it, so nothing is offered.</summary>
    static async Task<bool> ConnectedAsync(StorageConnections connections, string userId, CancellationToken ct) =>
        (await connections.ListAsync(userId, ct)).Any(c => c.Provider == NornisProvider.Key && !c.NeedsReconnect);

    static async Task<NornisSend> ForAsync(UserLibrary library, NornisOptions nornis, long documentId, (int First, int Last, string? Label)? pages,
        CancellationToken ct)
    {
        List<(string Id, string Name)> live;
        await using (var catalog = await library.Contexts.CreateDbContextAsync(ct))
            live = [.. (await catalog.NornisWorlds.AsNoTracking().Where(w => w.GoneUtc == null && w.CollectionId != null)
                .Select(w => new { w.WorldId, w.Name }).ToListAsync(ct)).Select(w => (w.WorldId, w.Name))];
        var worlds = new List<NornisSendWorld>();
        foreach (var (id, name) in live.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!Guid.TryParse(id, out var worldId) || await NornisReads.WorldAsync(library, worldId, ct) is not { } world) continue;
            // An adventure inside a file lends only its own pages, so it's offered where its pages are.
            var holds = pages is { } range ? world.Pages.Contains(documentId, range.First) : world.Pages.Holds(documentId);
            if (holds) worlds.Add(new NornisSendWorld(worldId, name, $"{nornis.Origin!.TrimEnd('/')}/bibliotaph/excerpt?world={id}&document={documentId}"));
        }
        if (worlds.Count == 0) return None;
        var problem = await library.Get<LibraryAskService>().IsKeptFromAiAsync(documentId, ct) ? NornisSend.KeptFromAi
            : pages is { } chosen && chosen.Last - chosen.First + 1 > NornisSend.MostPages ? NornisSend.TooManyPages
            : null;
        return new NornisSend(worlds, problem, pages?.First, pages?.Last, pages?.Label);
    }
}
