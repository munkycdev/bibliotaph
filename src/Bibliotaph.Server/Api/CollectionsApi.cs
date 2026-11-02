using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/collections: the Collections page's cards, a collection's page, the pickers and a book menu's Add to collection
/// items; and the changes the Windows app's collection actions make, each with the note it shows and its Undo.
/// </summary>
public static class CollectionsApi
{
    public static void MapCollectionsApi(this RouteGroupBuilder api)
    {
        var collections = api.MapGroup("/collections");

        collections.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var all = await AllAsync(lease.Library, ct);
            var tree = CollectionText.Tree(all.Values);
            string Path(long id) => CollectionText.PathOf(all, id);
            List<CollectionMenuItem> menu =
            [
                .. CollectionText.Recent(all.Values, Path).Select(c => new CollectionMenuItem("collection", c.Id, CollectionText.AddItem(c.Name), Path(c.Id))),
                new("new", null, CollectionText.NewItem, null),
            ];
            if (CollectionText.OffersChoose(all.Count)) menu.Add(new("choose", null, CollectionText.ChooseItem, null));
            return new CollectionList([.. tree.Select(t => Node(t.Collection, all))], [.. tree.Select(t => t.Depth)], menu);
        });

        collections.MapGet("/cards", async (long? parent, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var all = await AllAsync(lease.Library, ct);
            // At the top, the pinned ones come first, as the sidebar has them.
            var shown = CollectionText.ChildrenOf(all.Values, parent);
            if (parent is null) shown = [.. shown.OrderBy(c => c.Pinned ? 0 : 1)];
            return await CardsAsync(lease.Library, shown, all, ct);
        });

        collections.MapGet("/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var all = await AllAsync(lease.Library, ct);
            if (!all.TryGetValue(id, out var collection)) return Results.NotFound();
            var children = await CardsAsync(lease.Library, CollectionText.ChildrenOf(all.Values, id), all, ct);
            return Results.Ok(new CollectionPage(Node(collection, all), collection.Description ?? CollectionText.Subtitle, children));
        });

        collections.MapPost("", async (CollectionName request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (request.Name.Trim() is not { Length: > 0 } name) return NeedsName();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (request.ParentId is { } parent && !(await AllAsync(library, ct)).ContainsKey(parent)) return Results.NotFound();
            var created = await library.Get<CollectionsService>().CreateAsync(name, request.ParentId, Description(request.Description), ct);
            return Results.Ok(new CollectionCreated(Node(created, await AllAsync(library, ct)), new ActionResult("")));
        });

        collections.MapPut("/{id:long}", async (long id, CollectionName request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (request.Name.Trim() is not { Length: > 0 } name) return NeedsName();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (!(await AllAsync(library, ct)).TryGetValue(id, out var collection)) return Results.NotFound();
            var service = library.Get<CollectionsService>();
            await service.RenameAsync(id, name, Description(request.Description), ct);
            return Results.Ok(new ActionResult(CollectionText.Renamed(collection.Name, name),
                Hold(library, () => service.RenameAsync(id, collection.Name, collection.Description, CancellationToken.None))));
        });

        collections.MapPut("/{id:long}/parent", async (long id, CollectionMove request, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var all = await AllAsync(library, ct);
            if (!all.TryGetValue(id, out var collection)) return Results.NotFound();
            CollectionInfo? to = null;
            // Never into itself or one inside it.
            if (request.ParentId is { } parent
                && (!all.TryGetValue(parent, out to) || !CollectionText.Tree(all.Values, excluding: id).Any(t => t.Collection.Id == parent)))
                return Results.NotFound();
            var service = library.Get<CollectionsService>();
            await service.MoveAsync(id, request.ParentId, ct);
            return Results.Ok(new ActionResult(CollectionText.Moved(collection.Name, to?.Name),
                Hold(library, () => service.MoveAsync(id, collection.ParentId, CancellationToken.None))));
        });

        // Deleting a collection never deletes its books; its sub-collections move up a level.
        collections.MapDelete("/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var service = library.Get<CollectionsService>();
            if (await service.DeleteAsync(id, ct) is not { } deleted) return Results.NotFound();
            return Results.Ok(new ActionResult(CollectionText.Deleted(deleted), Hold(library, () => service.RestoreAsync(deleted, CancellationToken.None))));
        });

        collections.MapPost("/{id:long}/books", async (long id, CollectionBooks request, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (!(await AllAsync(library, ct)).TryGetValue(id, out var collection)) return Results.NotFound();
            var service = library.Get<CollectionsService>();
            var books = await BooksAsync(library, request.EntryIds, ct);
            var added = await service.AddAsync(id, books, ct);
            return Results.Ok(new ActionResult(CollectionText.Added(request.What, books.Count, added.Count, collection.Name),
                added.Count == 0 ? null : Hold(library, () => service.RemoveAsync(id, added, CancellationToken.None))));
        });

        collections.MapPost("/{id:long}/books/remove", async (long id, CollectionBooks request, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (!(await AllAsync(library, ct)).TryGetValue(id, out var collection)) return Results.NotFound();
            var service = library.Get<CollectionsService>();
            var books = await BooksAsync(library, request.EntryIds, ct);
            var removed = await service.RemoveAsync(id, books, ct);
            return Results.Ok(new ActionResult(CollectionText.Removed(request.What, books.Count, removed.Count, collection.Name),
                removed.Count == 0 ? null : Hold(library, () => service.AddAsync(id, removed, CancellationToken.None))));
        });
    }

    static IResult NeedsName() => Results.Problem("Give the collection a name.", statusCode: StatusCodes.Status400BadRequest);

    static string? Description(string? text) => text?.Trim() is { Length: > 0 } description ? description : null;

    public static async Task<Dictionary<long, CollectionInfo>> AllAsync(UserLibrary library, CancellationToken ct) =>
        (await library.Get<CollectionsService>().ListAsync(ct)).ToDictionary(c => c.Id);

    static CollectionNode Node(CollectionInfo c, IReadOnlyDictionary<long, CollectionInfo> all) =>
        new(c.Id, c.ParentId, c.Name, c.Description, CollectionText.PathOf(all, c.Id), c.Pinned);

    /// <summary>Only books of this library: an id from elsewhere is left out rather than added.</summary>
    static async Task<IReadOnlyList<EntryId>> BooksAsync(UserLibrary library, IReadOnlyList<long> entryIds, CancellationToken ct)
    {
        var known = await library.Get<LibraryQueries>().GetEntriesAsync([.. entryIds.Distinct().Select(id => new EntryId(id))], ct);
        return [.. known.Select(e => e.EntryId)];
    }

    /// <summary>
    /// Each collection's card: how many books it holds, its sub-collections' included, and the covers of the two added
    /// last. Only books in the library's shown folders count.
    /// </summary>
    static async Task<IReadOnlyList<CollectionCard>> CardsAsync(UserLibrary library, IReadOnlyList<CollectionInfo> shown,
        IReadOnlyDictionary<long, CollectionInfo> all, CancellationToken ct)
    {
        if (shown.Count == 0) return [];
        var store = library.Get<LibraryStore>();
        var queries = library.Get<LibraryQueries>();
        var visible = await store.GetVisibleEntryIdsAsync(ct: ct);
        var away = await store.GetUnavailableAsync(ct);
        var counts = await queries.CountGroupsAsync("collection:", visible, ct);
        var worlds = await Nornis.NornisWorlds.CollectionsAsync(library, ct);
        var cards = new List<CollectionCard>();
        foreach (var collection in shown)
        {
            var key = ScopeKeys.Collection(collection.Id);
            var covers = await queries.ListAsync(new LibraryFilter(visible, Sort: LibrarySort.RecentlyAdded, Group: key), limit: 2, ct: ct);
            var children = all.Values.Count(c => c.ParentId == collection.Id);
            cards.Add(new CollectionCard(collection.Id, collection.Name, CollectionText.Tip(collection), collection.Pinned,
                CollectionText.Count(counts.GetValueOrDefault(key), children), [.. covers.Select(e => LibraryApi.ToCard(e, away.GetValueOrDefault(e.EntryId)))],
                worlds.TryGetValue(collection.Id, out var gone) ? gone ? "World gone" : "Nornis world" : null));
        }
        return cards;
    }

    static string? Hold(UserLibrary library, Func<Task> undo) => EditApi.Hold(library, undo, "That couldn't be undone.");
}
