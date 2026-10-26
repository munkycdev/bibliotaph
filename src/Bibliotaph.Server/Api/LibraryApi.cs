using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Bibliotaph.Server.Api;

/// <summary>/api/library and its covers: the Library page's cards, search results and filter menus.</summary>
public static class LibraryApi
{
    public static void MapLibraryApi(this RouteGroupBuilder api)
    {
        api.MapGet("/library", async (HttpRequest request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await ResultsAsync(lease.Library, Query(request.Query), ct);
        });

        api.MapGet("/library/values/{field}", async (string field, string? text, HttpRequest request, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            if (TypeAheadFilters.All.All(f => f.Field.Key != field)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return Results.Ok(await SuggestAsync(lease.Library, field, text ?? "", Query(request.Query), ct));
        });

        // The search box's field guide: a field's values among the books shown, with their counts.
        api.MapGet("/library/guide/{field}", async (string field, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (SearchFields.Find(field) is not { ListsValues: true } info) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            return Results.Ok(await SearchGuideValues.LoadAsync(library.Get<LibraryQueries>(), library.Get<LibraryStore>(), info, ct));
        });

        api.MapGet("/home", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await HomeAsync(lease.Library, ct);
        });

        api.MapGet("/library/covers/{name}", async (string name, HttpContext context, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            if (!IsCoverName(name)) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var path = lease.Library.Get<CoverCache>().PathFor(name);
            if (!await lease.Library.Get<CoverMirror>().RestoreAsync(name, ct)) return Results.NotFound();
            // A cover's name changes with its image, except a book owned elsewhere's, which importing again redraws.
            context.Response.Headers.CacheControl = name.StartsWith("elsewhere-", StringComparison.Ordinal)
                ? "private, no-cache"
                : "private, max-age=31536000, immutable";
            var modified = File.GetLastWriteTimeUtc(path);
            return Results.File(path, "image/jpeg", lastModified: modified,
                entityTag: new EntityTagHeaderValue($"\"{modified.Ticks.ToString(CultureInfo.InvariantCulture)}\""));
        });
    }

    /// <summary>How many covers each of Home's rows shows.</summary>
    const int HomeRow = 8;

    /// <summary>Home's rows: the books opened last, newest first, and those that joined the library last.</summary>
    public static async Task<HomeShelves> HomeAsync(UserLibrary library, CancellationToken ct)
    {
        var store = library.Get<LibraryStore>();
        var queries = library.Get<LibraryQueries>();
        var scope = await store.GetVisibleEntryIdsAsync(ct: ct);
        var away = await store.GetUnavailableAsync(ct);
        var opened = await queries.ListAsync(new LibraryFilter(scope, Sort: LibrarySort.RecentlyOpened, OnlyOpened: true), limit: HomeRow, ct: ct);
        var added = await queries.ListAsync(new LibraryFilter(scope, Sort: LibrarySort.RecentlyAdded), limit: HomeRow, ct: ct);
        BookCard Card(LibraryEntry entry) => ToCard(entry, away.GetValueOrDefault(entry.EntryId));
        return new HomeShelves([.. opened.Select(Card)], [.. added.Select(Card)], (await StorageApi.StatusAsync(library, ct)).Text,
            await BindersApi.PreparingAsync(library, ct));
    }

    /// <summary>The Library page's query string. Anything missing or unreadable is "any", never a refusal.</summary>
    static LibraryQuery Query(IQueryCollection q)
    {
        string? Text(string key) => q[key].FirstOrDefault() is { Length: > 0 } value ? value : null;
        bool On(string key) => string.Equals(Text(key), "true", StringComparison.OrdinalIgnoreCase);
        int? Number(string key) => int.TryParse(Text(key), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        return new LibraryQuery
        {
            Q = Text("q"), Tab = Text("tab"), Sort = Text("sort"), EachFile = On("eachFile"), System = Text("system"), Type = Text("type"),
            Level = Number("level"), UnknownLevels = On("unknownLevels"), Players = Number("players"), Style = Text("style"), Duration = Text("duration"),
            Folder = long.TryParse(Text("folder"), NumberStyles.None, CultureInfo.InvariantCulture, out var folder) ? folder : null,
            Own = Text("own"), Store = Text("store"), Kind = Text("kind"), Ai = Text("ai"), Copies = On("copies"), Adventures = Text("adventures"),
            Collections = Text("collections"), Picked = Text("picked"),
            Collection = long.TryParse(Text("collection"), NumberStyles.None, CultureInfo.InvariantCulture, out var collection) ? collection : null,
            AddedHere = On("addedHere"), Favorites = On("favorites"),
            Binder = long.TryParse(Text("binder"), NumberStyles.None, CultureInfo.InvariantCulture, out var binder) ? binder : null,
        };
    }

    /// <summary>The names CoverCache gives covers: a hash, a page key or a chosen cover's, always a .jpg in its own folder.</summary>
    static bool IsCoverName(string name) =>
        name.Length is > 4 and < 200 && name.EndsWith(".jpg", StringComparison.Ordinal) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
        && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Runs the Library page's query as the Windows app's Library does: the books in scope, filtered and in order, or
    /// the search's documents and matching pages, with the words for the count line and an empty page.
    /// </summary>
    public static async Task<LibraryResults> ResultsAsync(UserLibrary library, LibraryQuery query, CancellationToken ct)
    {
        var store = library.Get<LibraryStore>();
        var queries = library.Get<LibraryQueries>();
        var searching = query.IsSearching;
        var scope = await store.GetVisibleEntryIdsAsync(query.Folder, ct);
        var away = await store.GetUnavailableAsync(ct);
        var filter = Filter(query, scope);
        var scopeText = await ScopeAsync(library, query, ct);
        var text = query.Q?.Trim() ?? "";
        var parsed = SearchQuery.Parse(searching ? text : "");
        var plan = searching ? SearchPlan.From(parsed) : null;

        var documents = plan is null ? await queries.ListAsync(filter, ct: ct) : await queries.SearchDocumentsAsync(plan, filter, ct: ct);
        var pages = plan is null ? null : await queries.SearchPagesAsync(plan, filter, ct: ct);
        var menus = await MenusAsync(library, filter, plan, query, ct);
        var filtered = IsFiltering(query);
        var issue = parsed.Issues.Count == 0 ? null : CardText.Issue(parsed, parsed.Issues[0]);
        BookCard Card(LibraryEntry entry) => ToCard(entry, away.GetValueOrDefault(entry.EntryId));
        static string N(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

        if (pages is null || plan is null)
        {
            EmptyLibrary? empty = null;
            if (documents.Count == 0)
            {
                var folders = (await library.Get<SourceRootStore>().ListAsync(ct)).Count;
                empty = folders == 0
                    ? new("No folders yet.", "Add a folder of RPG books, maps or handouts to start your library. Bibliotaph reads them where they are.",
                        "add-folder", "Add a folder")
                    : filtered ? new("Nothing here. Yet.", "No books match these filters.", "clear-filters", "Clear filters")
                    : query.Collection is not null ? new(CollectionText.EmptyTitle, CollectionText.EmptyMessage, "clear-scope", "Show your library")
                    : query.Binder is not null ? new(BinderText.ScopeEmptyTitle, BinderText.ScopeEmptyMessage, "clear-scope", "Show your library")
                    : query.Favorites ? new(SavedSearchText.FavoritesEmptyTitle, SavedSearchText.FavoritesEmptyMessage, "clear-scope", "Show your library")
                    : new("Indexing your library.", $"{(await StorageApi.StatusAsync(library, ct)).Text}. Books appear here as they are read.",
                        "manage-folders", "Manage folders");
            }
            return new LibraryResults([.. documents.Select(Card)], [], N(documents.Count), documents.Count == 1 ? "document" : "documents",
                "Documents", "Inside documents", issue, null, empty, menus, scopeText);
        }

        var documentsTab = $"Documents · {N(documents.Count)}";
        var pagesTab = plan.TextMatch is null ? "Inside documents" : $"Inside documents · {N(pages.MatchingPages)}";
        var forQuery = $"for “{text}”";
        EmptyLibrary NoResults() => filtered
            ? new("Nothing here. Yet.", "Try fewer or shorter words, or clear the filters.", "clear-all", "Clear search and filters")
            : new("Nothing here. Yet.", "Try fewer or shorter words. Books still being read are searched as soon as they're done.", "clear-search", "Clear search");

        if (!query.IsPagesTab)
            return new LibraryResults([.. documents.Select(Card)], [], N(documents.Count), $"{(documents.Count == 1 ? "document" : "documents")} {forQuery}",
                documentsTab, pagesTab, issue, null, documents.Count > 0 ? null : NoResults(), menus, scopeText);

        var progress = await library.Get<IndexQueries>().GetProgressAsync(ct);
        var coverage = progress.Searchable < progress.Documents ? CardText.Coverage(progress) : null;
        var matches = pages.Entries.Select(d => new BookMatches(Card(d.Entry),
            [.. d.Pages.Select(p => new PageMatch(p.PdfPage, CardText.Where(p), p.Snippet, CardText.Source(p), p.DocumentId))],
            CardText.MorePages(d.MatchingPages, d.Pages.Count))).ToList();
        var label = $"matching {(pages.MatchingPages == 1 ? "page" : "pages")} in {N(pages.MatchingEntries)} {(pages.MatchingEntries == 1 ? "document" : "documents")} {forQuery}";
        if (pages.Entries.Count < pages.MatchingEntries) label += $", best {N(pages.Entries.Count)} shown";
        var emptyPages = plan.TextMatch is null
            ? new EmptyLibrary("Inside documents looks for words.", "Add a word to search the pages, or see the matching books on the Documents tab.",
                "show-documents", "Show documents")
            : matches.Count > 0 ? null : NoResults();
        return new LibraryResults([], matches, N(pages.MatchingPages), label, documentsTab, pagesTab, issue, coverage, emptyPages, menus, scopeText);
    }

    /// <summary>Favorites or a binder as the page names it; a collection's words come with its own page.</summary>
    static async Task<ScopeText?> ScopeAsync(UserLibrary library, LibraryQuery query, CancellationToken ct)
    {
        if (query.Collection is not null) return null;
        if (query.Binder is { } binder)
            return (await library.Get<SessionStore>().ListAsync(ct)).FirstOrDefault(p => p.Id == binder) is { } pack
                ? new ScopeText(pack.Title, BinderText.ScopeSubtitle, $"In {pack.Title}")
                : null;
        return query.Favorites ? new ScopeText(SavedSearchText.FavoritesName, SavedSearchText.FavoritesSubtitle, $"In {SavedSearchText.FavoritesName}") : null;
    }

    internal static LibraryFilter Filter(LibraryQuery query, IReadOnlyCollection<EntryId> scope)
    {
        var sort = Parse<LibrarySort>(query.Sort) ?? LibrarySort.Relevance;
        // Best match means nothing without a search; the Windows app browses newest first.
        if (!query.IsSearching && sort == LibrarySort.Relevance) sort = LibrarySort.RecentlyAdded;
        var adventures = query.Adventures ?? "";
        return new LibraryFilter(scope, Parse<KindFilter>(query.Kind) ?? KindFilter.All, sort, One(query.System), One(query.Type), query.Level,
            query.UnknownLevels, Parse<AiFilter>(query.Ai) ?? AiFilter.All, query.Copies, One(query.Own), Styles: One(query.Style),
            Durations: One(query.Duration), Players: query.Players, Stores: One(query.Store), HideParts: adventures == "Hidden",
            Collections: Parse<CollectionFilter>(query.Collections) ?? CollectionFilter.All, PartsInBooks: adventures.Length == 0,
            PackFiles: query.EachFile, Picked: Picked(query.Picked),
            Group: query.Collection is { } collection ? query.AddedHere ? ScopeKeys.CollectionOwn(collection) : ScopeKeys.Collection(collection)
                : query.Binder is { } binder ? ScopeKeys.Session(binder)
                : query.Favorites ? ScopeKeys.Favorites : null);
    }

    static bool IsFiltering(LibraryQuery q) =>
        !string.IsNullOrEmpty(q.Kind) || q.Folder is not null || !string.IsNullOrEmpty(q.System) || !string.IsNullOrEmpty(q.Type) || q.Level is not null
        || !string.IsNullOrEmpty(q.Ai) || q.Copies || !string.IsNullOrEmpty(q.Own) || !string.IsNullOrEmpty(q.Style) || !string.IsNullOrEmpty(q.Duration)
        || q.Players is not null || !string.IsNullOrEmpty(q.Store) || !string.IsNullOrEmpty(q.Adventures) || !string.IsNullOrEmpty(q.Collections)
        || Picked(q.Picked) is not null;

    /// <summary>The type-ahead filters' picks in a query, or null with none or with JSON that can't be read.</summary>
    internal static PickedValues? Picked(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<PickedValues>(json) is { IsEmpty: false } picked ? picked : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static async Task<LibraryMenus> MenusAsync(UserLibrary library, LibraryFilter filter, SearchPlan? plan, LibraryQuery query, CancellationToken ct)
    {
        var queries = library.Get<LibraryQueries>();
        async Task<IReadOnlyList<FilterChoice>> Counted(string field, LibraryFilter without, string all, string? selected)
        {
            var counts = await queries.GetFacetCountsAsync(field, without, plan, ct);
            List<FilterChoice> choices = [new("", all), .. counts.Select(c => new FilterChoice(c.Value, $"{c.Label} ({c.Count.ToString("N0", CultureInfo.CurrentCulture)})"))];
            // The menu never changes what was picked: a value no book has any more stays, counted as none.
            if (!string.IsNullOrEmpty(selected) && choices.All(c => c.Value != selected)) choices.Add(new(selected, $"{selected} (0)"));
            return choices;
        }

        var roots = await library.Get<SourceRootStore>().ListAsync(ct);
        await using var db = library.Catalog.CreateContext();
        var hasCollections = await db.Collections.AnyAsync(ct);
        var styles = await Counted("style", filter with { Styles = null }, "Any style", query.Style);
        var durations = await Counted("duration", filter with { Durations = null }, "Any length", query.Duration);
        var owns = await Counted("own", filter with { Owns = null }, "Also own: any", query.Own);
        var stores = await Counted("store", filter with { Stores = null }, "Any store", query.Store);
        // A menu shows once some book has a value for it, or while one is chosen.
        static bool Offered(IReadOnlyList<FilterChoice> menu, string? selected) =>
            menu.Any(c => c.Value.Length > 0 && c.Value != SearchQuery.Unknown) || !string.IsNullOrEmpty(selected);
        return new LibraryMenus(
            await Counted("system", filter with { Systems = null }, "All systems", query.System),
            await Counted("type", filter with { Types = null }, "All types", query.Type),
            styles, durations,
            [new("", "All folders"), .. roots.Select(r => new FilterChoice(r.Id.ToString(CultureInfo.InvariantCulture), FolderText.Name(r.Path)))],
            owns, stores, Offered(styles, query.Style), Offered(durations, query.Duration), Offered(owns, query.Own), Offered(stores, query.Store),
            await queries.CountAiReadAsync(ct) > 0 || !string.IsNullOrEmpty(query.Ai),
            await queries.CountWithCopiesAsync(ct) > 0 || query.Copies,
            await queries.CountPartsAsync(ct) > 0 || !string.IsNullOrEmpty(query.Adventures),
            hasCollections || !string.IsNullOrEmpty(query.Collections),
            await BoxesAsync(queries, filter, plan, ct));
    }

    /// <summary>
    /// The type-ahead filters, each shown once some book in view has a value for it, or while it has picks; a pick
    /// takes its value's name from the counts, and a value no book has any more keeps its own.
    /// </summary>
    static async Task<IReadOnlyList<FilterBox>> BoxesAsync(LibraryQueries queries, LibraryFilter filter, SearchPlan? plan, CancellationToken ct)
    {
        var boxes = new List<FilterBox>();
        foreach (var (field, hint) in TypeAheadFilters.All)
        {
            var counts = await queries.GetValueCountsAsync(field.Key, filter with { Picked = filter.Picked?.Without(field.Key) }, plan, ct);
            var picks = filter.Picked?[field.Key] ?? [];
            var labels = counts.ToDictionary(c => c.Value, c => c.Label, StringComparer.Ordinal);
            boxes.Add(new FilterBox(field.Key, field.Label, hint, counts.Count > 0 || picks.Count > 0,
                [.. picks.Select(v => new FilterChoice(v, labels.GetValueOrDefault(v) ?? v))]));
        }
        return boxes;
    }

    /// <summary>What a type-ahead filter offers for what was typed, among the books the rest of the query keeps.</summary>
    public static async Task<IReadOnlyList<ValueSuggestion>> SuggestAsync(UserLibrary library, string field, string text, LibraryQuery query,
        CancellationToken ct)
    {
        var scope = await library.Get<LibraryStore>().GetVisibleEntryIdsAsync(query.Folder, ct);
        var filter = Filter(query, scope);
        var plan = query.IsSearching ? SearchPlan.From(SearchQuery.Parse(query.Q!.Trim())) : null;
        var counts = await library.Get<LibraryQueries>().GetValueCountsAsync(field, filter with { Picked = filter.Picked?.Without(field) }, plan, ct);
        static string N(long count) => count.ToString("N0", CultureInfo.CurrentCulture);
        return [.. TypeAheadFilters.Suggest(counts, [.. filter.Picked?[field] ?? []], text)
            .Select(c => new ValueSuggestion(c.Value, c.Label, N(c.Count), $"{c.Label}, {N(c.Count)} {(c.Count == 1 ? "book" : "books")}"))];
    }

    internal static BookCard ToCard(LibraryEntry e, EntryAvailability availability) => new(
        e.CardId.ToString(), e.EntryId.Value, e.DocumentId, e.Title, CardText.Meta(e), CardText.Byline(e), CardText.Size(e), CardText.Format(e),
        e.System ?? "", e.Kind ?? "", e.Levels ?? "", e.Players ?? "", CardText.Copies(e), e.Cover,
        e.IsPack && e.CoverMember is null ? e.MosaicCovers : [], CardText.Initial(e.Title), e.Favorite, CardText.AiTip(e), CardText.TextMark(e),
        CardText.TextMarkTip(e), e.IsElsewhere, CardText.Badge(e), e.IsPart, e.FoundInBook,
        availability switch { EntryAvailability.Offline => "Offline", EntryAvailability.Missing => "File missing", _ => "" },
        availability switch
        {
            EntryAvailability.Offline => "Its folder can't be reached right now. It comes back with the folder.",
            EntryAvailability.Missing => "Its file isn't where it was. The details show where it was last; the card stays until the file turns up.",
            _ => null,
        });

    static T? Parse<T>(string? value) where T : struct, Enum => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    static string[]? One(string? value) => string.IsNullOrEmpty(value) ? null : [value];
}
