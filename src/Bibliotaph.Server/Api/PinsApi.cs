using System.Globalization;
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
/// /api/pins: the sidebar's Pinned group: each pin with its target's
/// current name, in order, a pin whose target has gone left out. Each change answers with the pins as they are now.
/// </summary>
public static class PinsApi
{
    public static void MapPinsApi(this RouteGroupBuilder api)
    {
        var pins = api.MapGroup("/pins");

        pins.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await ListAsync(lease.Library, ct);
        });

        pins.MapPost("", async (PinRef pin, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (Target(pin) is not { } target) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            if (!await ExistsAsync(lease.Library, target, ct)) return Results.NotFound();
            await lease.Library.Get<PinsService>().PinAsync(target, ct);
            return Results.Ok(await ListAsync(lease.Library, ct));
        });

        pins.MapPost("/remove", async (PinRef pin, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (Target(pin) is not { } target) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<PinsService>().UnpinAsync(target, ct);
            return Results.Ok(await ListAsync(lease.Library, ct));
        });

        // The index counts the pins shown, as the sidebar does; one not shown keeps its place among the others.
        pins.MapPost("/move", async (PinMove move, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (Target(new PinRef(move.Kind, move.Id)) is not { } target) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var service = library.Get<PinsService>();
            var shown = (await ItemsAsync(library, ct)).Select(i => Target(new PinRef(i.Kind, i.Id))!.Value).ToList();
            if (!shown.Remove(target)) return Results.NotFound();
            var index = Math.Clamp(move.Index, 0, shown.Count);
            var others = (await service.ListAsync(ct)).Select(p => p.Target).Where(t => t != target).ToList();
            await service.MoveAsync(target, index < shown.Count ? others.IndexOf(shown[index]) : others.Count, ct);
            return Results.Ok(await ListAsync(library, ct));
        });
    }

    public static PinTarget? Target(PinRef pin) => (pin.Kind, pin.Id) switch
    {
        (PinKinds.Favorites, _) => PinTarget.Favorites,
        (PinKinds.Collection, { } id) => PinTarget.Collection(id),
        (PinKinds.Binder, { } id) => PinTarget.Binder(id),
        (PinKinds.Search, { } id) => PinTarget.SavedSearch(id),
        (PinKinds.Book, { } id) => PinTarget.Entry(new EntryId(id)),
        _ => null,
    };

    /// <summary>Whether what a pin would point at is there to open.</summary>
    static async Task<bool> ExistsAsync(UserLibrary library, PinTarget target, CancellationToken ct)
    {
        var id = target.Id ?? 0;
        return target.Kind switch
        {
            PinKind.Favorites => true,
            PinKind.Collection => (await CollectionsApi.AllAsync(library, ct)).ContainsKey(id),
            PinKind.Binder => (await library.Get<SessionStore>().ListAsync(ct)).Any(b => b.Id == id),
            PinKind.SavedSearch => await library.Get<SmartViewStore>().GetAsync(id, ct) is not null,
            PinKind.Entry => (await library.Get<LibraryQueries>().GetEntriesAsync([new EntryId(id)], ct)).Count > 0,
            _ => false,
        };
    }

    static async Task<PinList> ListAsync(UserLibrary library, CancellationToken ct) => new(await ItemsAsync(library, ct), SavedSearchText.NoPins);

    /// <summary>The pins in order, each with its target's name and the page it opens.</summary>
    public static async Task<IReadOnlyList<PinItem>> ItemsAsync(UserLibrary library, CancellationToken ct)
    {
        var pins = await library.Get<PinsService>().ListAsync(ct);
        if (pins.Count == 0) return [];
        var collections = await CollectionsApi.AllAsync(library, ct);
        var binders = (await library.Get<SessionStore>().ListAsync(ct)).ToDictionary(b => b.Id);
        var views = (await library.Get<SmartViewStore>().ListAsync(ct)).ToDictionary(v => v.Id);
        var entryIds = pins.Where(p => p.Target.Kind == PinKind.Entry).Select(p => new EntryId(p.Target.Id!.Value)).ToList();
        var books = entryIds.Count == 0 ? [] : (await library.Get<LibraryQueries>().GetEntriesAsync(entryIds, ct)).ToDictionary(b => b.EntryId);
        var away = entryIds.Count == 0 ? new Dictionary<EntryId, EntryAvailability>() : await library.Get<LibraryStore>().GetUnavailableAsync(ct);
        static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
        var items = new List<PinItem>();
        foreach (var pin in pins)
        {
            var id = pin.Target.Id ?? 0;
            PinItem? item = pin.Target.Kind switch
            {
                PinKind.Favorites => new(PinKinds.Favorites, null, SavedSearchText.FavoritesName, "library?favorites=true", "Heart", false, SavedSearchText.FavoritesName),
                PinKind.Collection when collections.TryGetValue(id, out var c) => new(PinKinds.Collection, id, c.Name, $"library?collection={Id(id)}", "Folders", false, c.Name),
                PinKind.Binder when binders.TryGetValue(id, out var b) => new(PinKinds.Binder, id, b.Title, $"binders/{Id(id)}", "NotebookTabs", false, b.Title),
                PinKind.SavedSearch when views.TryGetValue(id, out var v) => new(PinKinds.Search, id, v.Name, $"library?view={Id(id)}", "SlidersHorizontal", false, v.Name),
                PinKind.Entry when books.TryGetValue(new EntryId(id), out var e) => Book(e, away.GetValueOrDefault(e.EntryId)),
                _ => null,
            };
            if (item is not null) items.Add(item);
        }
        return items;

        // A book whose file is offline or missing keeps its pin, dimmed.
        static PinItem Book(LibraryEntry entry, EntryAvailability availability)
        {
            var card = LibraryApi.ToCard(entry, availability);
            return new(PinKinds.Book, entry.EntryId.Value, entry.Title, $"library?book={entry.EntryId.Value.ToString(CultureInfo.InvariantCulture)}", "BookOpen",
                card.AvailabilityTip is not null, card.AvailabilityTip is { } tip ? $"{entry.Title}: {tip}" : entry.Title);
        }
    }
}
