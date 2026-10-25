using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/searches: saved searches (Smart Views), stored as the Windows app stores them so both apps open the same ones.
/// The web's library query is turned into the Windows app's definition and back here, so a definition the web can't
/// show in full says so rather than showing something else.
/// </summary>
public static class SavedSearchesApi
{
    public static void MapSavedSearchesApi(this RouteGroupBuilder api)
    {
        var searches = api.MapGroup("/searches");

        searches.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var pinned = await PinnedAsync(lease.Library, ct);
            return (await lease.Library.Get<SmartViewStore>().ListAsync(ct)).Select(v => new SavedSearchInfo(v.Id, v.Name, pinned.Contains(v.Id))).ToList();
        });

        searches.MapGet("/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await lease.Library.Get<SmartViewStore>().GetAsync(id, ct) is { } view ? Results.Ok(await OpenAsync(lease.Library, view, ct)) : Results.NotFound();
        });

        searches.MapPost("", async (SavedSearchSave request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (request.Name.Trim() is not { Length: > 0 } name) return NeedsName();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var created = await lease.Library.Get<SmartViewStore>().CreateAsync(name, ToDefinition(request.Query, request.List).ToJson(), ct);
            return Results.Ok(new SavedSearchCreated(await OpenAsync(lease.Library, created, ct), new ActionResult(SavedSearchText.Saved(created.Name))));
        });

        searches.MapPut("/{id:long}", async (long id, SavedSearchChange request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var store = library.Get<SmartViewStore>();
            if (await store.GetAsync(id, ct) is not { } view) return Results.NotFound();
            await store.UpdateAsync(id, ToDefinition(request.Query, request.List).ToJson(), ct);
            return Results.Ok(new ActionResult(SavedSearchText.Updated(view.Name),
                EditApi.Hold(library, () => store.UpdateAsync(id, view.Definition, CancellationToken.None), "That couldn't be undone.")));
        });

        searches.MapPut("/{id:long}/name", async (long id, SavedSearchRename request, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            if (request.Name.Trim() is not { Length: > 0 } name) return NeedsName();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await lease.Library.Get<SmartViewStore>().RenameAsync(id, name, ct) ? Results.Ok(new ActionResult("")) : Results.NotFound();
        });

        // Deleting a saved search never touches a book; Undo brings it back with its pin where it was.
        searches.MapDelete("/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var store = library.Get<SmartViewStore>();
            if (await store.DeleteAsync(id, ct) is not { } deleted) return Results.NotFound();
            return Results.Ok(new ActionResult(SavedSearchText.Deleted(deleted.View.Name),
                EditApi.Hold(library, () => store.RestoreAsync(deleted, CancellationToken.None), "That couldn't be undone.")));
        });
    }

    static IResult NeedsName() => Results.Problem("Give the saved search a name.", statusCode: StatusCodes.Status400BadRequest);

    static async Task<HashSet<long>> PinnedAsync(UserLibrary library, CancellationToken ct) =>
        [.. (await library.Get<PinsService>().ListAsync(ct)).Where(p => p.Target.Kind == PinKind.SavedSearch).Select(p => p.Target.Id!.Value)];

    /// <summary>
    /// A saved search as the web opens it. A scope that has gone is left out, as the Windows app leaves it out, and the
    /// note says so.
    /// </summary>
    static async Task<SavedSearch> OpenAsync(UserLibrary library, SmartViewInfo view, CancellationToken ct)
    {
        var definition = SmartViewDefinition.Parse(view.Definition);
        var query = ToQuery(definition);
        string? note = null;
        if (definition.Scope is { } scope && scope != ScopeKeys.Favorites)
        {
            var collections = await CollectionsApi.AllAsync(library, ct);
            if (CollectionOf(scope) is { } collection && collections.ContainsKey(collection)) query = query with { Collection = collection };
            else if (await BinderOfAsync(library, scope, ct) is { } binder) query = query with { Binder = binder };
            else note = SavedSearchText.ScopeGone;
            if (query.Collection is null) query = query with { AddedHere = false };
        }
        return new SavedSearch(view.Id, view.Name, SavedSearchText.Subtitle, SavedSearchText.Changed, SavedSearchText.SecondName(view.Name), query, definition.Layout == LibraryLayout.List, note, (await PinnedAsync(library, ct)).Contains(view.Id));
    }

    static async Task<long?> BinderOfAsync(UserLibrary library, string scope, CancellationToken ct) =>
        (await library.Get<SessionStore>().ListAsync(ct)).FirstOrDefault(p => ScopeKeys.Session(p.Id) == scope)?.Id;

    static long? CollectionOf(string scope) =>
        scope.StartsWith("collection:", StringComparison.Ordinal)
        && long.TryParse(scope["collection:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    /// <summary>What the web's Library shows, as the Windows Library's CurrentView keeps it.</summary>
    internal static SmartViewDefinition ToDefinition(LibraryQuery q, bool list) => new(
        Query: q.Q?.Trim() ?? "",
        Scope: q.Collection is { } collection ? ScopeKeys.Collection(collection) : q.Binder is { } binder ? ScopeKeys.Session(binder)
            : q.Favorites ? ScopeKeys.Favorites : null,
        OnlyAddedHere: q.Collection is not null && q.AddedHere,
        Kind: Parse<KindFilter>(q.Kind) ?? KindFilter.All,
        Folder: q.Folder,
        System: Text(q.System),
        Type: Text(q.Type),
        Level: q.Level,
        IncludeUnknownLevels: q.Level is not null && q.UnknownLevels,
        Ai: Parse<AiFilter>(q.Ai) ?? AiFilter.All,
        Copies: q.Copies,
        Own: Text(q.Own),
        Sort: Parse<LibrarySort>(q.Sort) is { } sort && (q.IsSearching || sort != LibrarySort.Relevance) ? sort
            : q.IsSearching ? LibrarySort.Relevance : LibrarySort.RecentlyAdded,
        Layout: list ? LibraryLayout.List : LibraryLayout.Grid,
        Tab: q.IsPagesTab ? ResultsTab.Pages : ResultsTab.Documents,
        Style: Text(q.Style),
        Duration: Text(q.Duration),
        Players: q.Players,
        Store: Text(q.Store),
        Collections: Parse<CollectionFilter>(q.Collections) ?? CollectionFilter.All,
        Picked: LibraryApi.Picked(q.Picked),
        Adventures: q.Adventures switch { "Hidden" => AdventureCards.Hidden, "Cards" => AdventureCards.Cards, _ => AdventureCards.InBooks });

    /// <summary>A definition as the web's library query, its scope left for <see cref="OpenAsync"/> to check.</summary>
    internal static LibraryQuery ToQuery(SmartViewDefinition d) => new()
    {
        Q = d.Query.Trim() is { Length: > 0 } text ? text : null,
        Tab = d.Tab == ResultsTab.Pages ? "pages" : null,
        Sort = d.Sort.ToString(),
        System = d.System,
        Type = d.Type,
        Level = d.Level,
        UnknownLevels = d.Level is not null && d.IncludeUnknownLevels,
        Players = d.Players,
        Style = d.Style,
        Duration = d.Duration,
        Folder = d.Folder,
        Own = d.Own,
        Store = d.Store,
        Kind = d.Kind == KindFilter.All ? null : d.Kind.ToString(),
        Ai = d.Ai == AiFilter.All ? null : d.Ai.ToString(),
        Copies = d.Copies,
        Adventures = d.Adventures switch { AdventureCards.Hidden => "Hidden", AdventureCards.Cards => "Cards", _ => null },
        Collections = d.Collections == CollectionFilter.All ? null : d.Collections.ToString(),
        Picked = d.Picked is { IsEmpty: false } picked ? JsonSerializer.Serialize(picked) : null,
        Favorites = d.Scope == ScopeKeys.Favorites,
        AddedHere = d.OnlyAddedHere,
    };

    static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value;

    static T? Parse<T>(string? value) where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
