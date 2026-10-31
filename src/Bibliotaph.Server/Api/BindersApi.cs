using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/binders: the Binders page's groups, a binder's page, and the changes the Windows app's binder pages make, each
/// with the note it shows and its Undo. The browser says what day it is where the user is, since upcoming and earlier
/// binders are told apart by it.
/// </summary>
public static class BindersApi
{
    public static void MapBindersApi(this RouteGroupBuilder api)
    {
        var binders = api.MapGroup("/binders");

        binders.MapGet("", async (DateOnly? today, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var all = await library.Get<SessionsService>().ListAsync(ct);
            var systems = await SystemsAsync(library, ct);
            var groups = BinderGroups.Of(all, today ?? DateOnly.FromDateTime(DateTime.UtcNow));
            var cards = (await CardsAsync(library, all, systems, ct)).ToDictionary(c => c.Id);
            IReadOnlyList<BinderCard> Cards(IReadOnlyList<SessionPackInfo> packs) => [.. packs.Select(p => cards[p.Id])];
            return new BinderList(Cards(groups.Upcoming), Cards(groups.AtHand), Cards(groups.Undated), Cards(groups.Earlier),
                BinderText.EarlierLabel(false, groups.Earlier.Count), BinderText.EarlierLabel(true, groups.Earlier.Count), systems);
        });

        binders.MapPost("", async (BinderEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (edit.Title.Trim() is not { Length: > 0 } title) return NeedsTitle();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var created = await lease.Library.Get<SessionsService>().CreateAsync(title, edit.KeepAtHand ? null : edit.Date, edit.KeepAtHand,
                edit.KeepAtHand ? edit.GameSystem : null, ct);
            return Results.Ok(new BinderCreated(created.Id));
        });

        // Opening a binder's page makes it the current one, which Add to binder adds to.
        binders.MapPost("/{id:long}/open", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var contents = await lease.Library.Get<SessionsService>().OpenAsync(id, ct);
            return contents is null ? Results.NotFound() : Results.Ok(await PageAsync(lease.Library, contents, ct));
        });

        binders.MapGet("/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var contents = await lease.Library.Get<SessionsService>().GetAsync(id, ct);
            return contents is null ? Results.NotFound() : Results.Ok(await PageAsync(lease.Library, contents, ct));
        });

        binders.MapPut("/{id:long}", async (long id, BinderEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (edit.Title.Trim() is not { Length: > 0 } title) return NeedsTitle();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var sessions = lease.Library.Get<SessionsService>();
            if (await sessions.GetAsync(id, ct) is null) return Results.NotFound();
            await sessions.UpdateAsync(id, title, edit.KeepAtHand ? null : edit.Date, edit.KeepAtHand, edit.KeepAtHand ? edit.GameSystem : null, ct);
            return Results.Ok(new ActionResult(""));
        });

        binders.MapPut("/{id:long}/notes", async (long id, BinderNotes notes, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<SessionsService>().SetNotesAsync(id, notes.Notes, ct);
            return Results.NoContent();
        });

        // Add to binder's menu, which leads with the binder worked on last.
        binders.MapGet("/menu", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var all = await lease.Library.Get<SessionsService>().ListAsync(ct);
            List<BinderMenuChoice> choices = [];
            if (all is [var current, ..]) choices.Add(new BinderMenuChoice(BinderMenuKind.Binder, current.Id, BinderText.AddTo(current.Title)));
            if (all.Count > 1) choices.Add(new BinderMenuChoice(BinderMenuKind.Choose, null, BinderText.AddToAnother));
            choices.Add(new BinderMenuChoice(BinderMenuKind.New, null, BinderText.AddToNew));
            return new BinderMenu(choices, [.. all.Select(p => new BinderChoice(p.Id, p.Title))], await SystemsAsync(lease.Library, ct), BinderText.NewHeading,
                BinderText.NewAction);
        });

        // Add pages…'s Where: the end, before the first section, or a section.
        binders.MapGet("/{id:long}/sections", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await lease.Library.Get<SessionsService>().GetAsync(id, ct) is { } contents
                ? Results.Ok(contents.Sections.Select(s => new BinderSection(s.Id, s.Name)).ToList())
                : Results.NotFound();
        });

        // Whole books, or pages of a book (a page found by search, the reader's Add page and Add pages…), with Undo
        // taking out just what was added.
        binders.MapPost("/{id:long}/add", async (long id, BinderAdd add, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await AddAsync(lease.Library, id, add, ct) is { } done ? Results.Ok(done) : Results.NotFound();
        });

        // At hand, in a binder's reader: the pages of the other binders kept at hand.
        binders.MapGet("/{id:long}/at-hand", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await AtHandAsync(lease.Library, id, ct) is { } list ? Results.Ok(list) : Results.NotFound();
        });

        // The binder reader's Add this page: right after the item in hand, in its section.
        binders.MapPost("/{id:long}/items", async (long id, BinderPageAdd add, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await AddAfterAsync(lease.Library, id, add, ct) is { } added ? Results.Ok(new BinderItemAdded(added)) : Results.NotFound();
        });

        // Use this page: the item points at the page in view from now on, keeping how many pages it spans.
        binders.MapPost("/items/{id:long}/use-page", async (long id, ReaderUsePage use, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await UsePageAsync(lease.Library, id, use, ct) is { } used ? Results.Ok(used) : Results.NotFound();
        });

        // A copy without its date, which then opens.
        binders.MapPost("/{id:long}/duplicate", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var sessions = lease.Library.Get<SessionsService>();
            if (await sessions.GetAsync(id, ct) is not { } contents) return Results.NotFound();
            var copy = await sessions.DuplicateAsync(id, BinderText.CopyTitle(contents.Pack.Title), ct);
            return copy is null ? Results.NotFound() : Results.Ok(new BinderCreated(copy.Id));
        });

        // Deleting a binder never touches its books; Undo brings it back.
        binders.MapDelete("/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var sessions = library.Get<SessionsService>();
            if (await sessions.DeleteAsync(id, ct) is not { } deleted) return Results.NotFound();
            return Results.Ok(new ActionResult(BinderText.Deleted(deleted.Title),
                EditApi.Hold(library, () => sessions.RestoreAsync(deleted, CancellationToken.None), BinderText.Failed)));
        });

        binders.MapPost("/{id:long}/sections", async (long id, BinderName name, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (name.Name.Trim() is not { Length: > 0 } text) return NeedsName();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var sessions = lease.Library.Get<SessionsService>();
            if (await sessions.GetAsync(id, ct) is null) return Results.NotFound();
            var section = await sessions.AddSectionAsync(id, text, ct);
            return Results.Ok(new ActionResult(BinderText.SectionAdded(section.Name)));
        });

        binders.MapPut("/{id:long}/sections/{section:long}", async (long id, long section, BinderName name, ClaimsPrincipal principal, UserLibraries libraries,
            CancellationToken ct) =>
        {
            if (name.Name.Trim() is not { Length: > 0 } text) return NeedsName();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var sessions = lease.Library.Get<SessionsService>();
            if (await sessions.GetAsync(id, ct) is not { } contents || contents.Sections.All(s => s.Id != section)) return Results.NotFound();
            await sessions.RenameSectionAsync(section, text, ct);
            return Results.Ok(new ActionResult(""));
        });

        // A heading goes; its items move up to the section above.
        binders.MapDelete("/{id:long}/sections/{section:long}", async (long id, long section, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var sessions = lease.Library.Get<SessionsService>();
            if (await sessions.GetAsync(id, ct) is not { } contents || contents.Sections.FirstOrDefault(s => s.Id == section) is not { } heading)
                return Results.NotFound();
            await sessions.DeleteSectionAsync(section, ct);
            return Results.Ok(new ActionResult(BinderText.SectionDeleted(heading.Name)));
        });

        binders.MapPut("/items/{id:long}", async (long id, BinderItemEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<SessionsService>().UpdateItemAsync(id, edit.Label, edit.Note, ct);
            return Results.NoContent();
        });

        binders.MapPost("/items/{id:long}/move", async (long id, BinderItemMove move, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var sessions = library.Get<SessionsService>();
            if (move.Onto is { } onto)
            {
                if (onto == id) return Results.Ok(false);
                if (await library.Get<SessionStore>().GetItemAsync(onto, ct) is not { } target) return Results.NotFound();
                return Results.Ok(await sessions.MoveItemToAsync(id, target.SectionId ?? SessionStore.NoSection, target.Position, ct));
            }
            var moved = move.SectionId is { } section ? await sessions.MoveItemToAsync(id, section, move.First ? 0 : int.MaxValue, ct) : await sessions.MoveItemAsync(id, move.By, ct);
            return Results.Ok(moved);
        });

        // Taking an item out of a binder, with Undo.
        binders.MapDelete("/items/{id:long}", async (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var sessions = library.Get<SessionsService>();
            if (await library.Get<SessionStore>().GetItemAsync(id, ct) is not { } item || await sessions.GetAsync(item.PackId, ct) is not { } contents)
                return Results.NotFound();
            var rows = await BinderRows.BuildAsync(sessions, library.Get<LibraryQueries>(), contents, ct);
            var heading = rows.Select(r => r.Item).FirstOrDefault(r => r?.Item.Id == id)?.Heading ?? BinderText.NoBook;
            var removed = await sessions.RemoveItemsAsync([id], ct);
            return removed.Count == 0 ? Results.NotFound() : Results.Ok(new ActionResult(BinderText.Removed(heading, contents.Pack.Title),
                EditApi.Hold(library, () => sessions.RestoreItemsAsync(removed, CancellationToken.None), BinderText.Failed)));
        });
    }

    static IResult NeedsTitle() => Results.Problem("Give the binder a title.", statusCode: StatusCodes.Status400BadRequest);

    static IResult NeedsName() => Results.Problem("Give the section a name.", statusCode: StatusCodes.Status400BadRequest);

    /// <summary>Any game system, then those the library's books are for, by name: what a binder kept at hand can be for.</summary>
    /// <summary>What Add to binder did, with its Undo; null when there's no such binder.</summary>
    internal static async Task<ActionResult?> AddAsync(UserLibrary library, long id, BinderAdd add, CancellationToken ct)
    {
        var sessions = library.Get<SessionsService>();
        if (await sessions.GetAsync(id, ct) is not { } contents) return null;
        IReadOnlyList<long> added;
        string what;
        if (add is { DocumentId: { } whole, WholeFile: true })
        {
            what = add.Title is { Length: > 0 } named ? named : "the picture";
            added = await sessions.AddDocumentAsync(id, whole, add.SectionId, ct) is { } item ? [item] : [];
        }
        else if (add.DocumentId is { } document && add.Page is { } page)
        {
            var (first, last) = (Math.Min(page, add.LastPage ?? page), Math.Max(page, add.LastPage ?? page));
            if (first < 0 || await library.Get<IndexQueries>().GetPageCountAsync(document, ct) is { } count && last >= count)
                return new ActionResult(BinderText.NotAdded);
            var labels = await library.Get<PartsService>().GetPageLabelsAsync(document, ct);
            var label = add.Label?.Trim() is { Length: > 0 } typed ? typed : null;
            what = add.LastPage is null ? BinderText.PageWhat(page < labels.Count ? labels[page] : null, page)
                : label ?? (first == last ? "the page" : "the pages");
            added = await sessions.AddPagesAsync(id, document, first, last, label, add.SectionId, ct) is { } item ? [item] : [];
        }
        else
        {
            what = add.Books.Count == 1 && add.Title is { Length: > 0 } title ? title : SelectionRules.Books(add.Books.Count);
            added = add.Books.Count == 0 ? [] : await sessions.AddAsync(id, [.. add.Books.Distinct().Select(b => new NewSessionItem(new EntryId(b)))], ct: ct);
        }
        return added.Count == 0
            ? new ActionResult(BinderText.NotAdded)
            : new ActionResult(BinderText.Added(what, contents.Pack.Title),
                EditApi.Hold(library, async () => await sessions.RemoveItemsAsync(added, CancellationToken.None), BinderText.Failed));
    }

    /// <summary>The item Add this page made; null when there's no such binder or page.</summary>
    internal static async Task<long?> AddAfterAsync(UserLibrary library, long id, BinderPageAdd add, CancellationToken ct)
    {
        var sessions = library.Get<SessionsService>();
        if (await sessions.GetAsync(id, ct) is not { } contents) return null;
        var after = add.After is { } afterId ? contents.Items.FirstOrDefault(i => i.Id == afterId) : null;
        var section = after?.SectionId ?? SessionStore.NoSection;
        long? added;
        if (add.WholeFile) added = await sessions.AddDocumentAsync(id, add.DocumentId, section, ct);
        else
        {
            if (add.Page < 0 || await library.Get<IndexQueries>().GetPageCountAsync(add.DocumentId, ct) is { } count && add.Page >= count) return null;
            added = await sessions.AddPagesAsync(id, add.DocumentId, add.Page, add.Page, sectionId: section, ct: ct);
        }
        if (added is { } item && after is not null) await sessions.MoveItemToAsync(item, section, after.Position + 1, ct);
        return added;
    }

    /// <summary>Use this page on a binder item whose pages changed; null when the item is gone.</summary>
    internal static async Task<ReaderItemUsed?> UsePageAsync(UserLibrary library, long id, ReaderUsePage use, CancellationToken ct)
    {
        var sessions = library.Get<SessionsService>();
        if (await library.Get<SessionStore>().GetItemAsync(id, ct) is not { } item || await sessions.ResolveItemAsync(id, ct) is not { } target
            || await sessions.GetAsync(item.PackId, ct) is not { } contents)
            return null;
        var index = library.Get<IndexQueries>();
        var count = await index.GetPageCountAsync(use.DocumentId, ct) ?? 0;
        if (use.Page < 0 || use.Page >= count) return null;
        var first = use.Page;
        var last = Math.Min(first + (target.LastPage - target.FirstPage), Math.Max(0, count - 1));
        await sessions.RepointAsync(id, use.DocumentId, first, last, ct);
        var labels = await index.GetPageLabelsAsync(use.DocumentId, ct);
        return new ReaderItemUsed(first, last, $"Saved. In {contents.Pack.Title}, this item now opens at page {PageNumbers.Display(first, labels)}.");
    }

    static async Task<IReadOnlyList<FilterChoice>> SystemsAsync(UserLibrary library, CancellationToken ct) =>
    [
        new FilterChoice("", BinderText.AnySystem),
        .. (await library.Get<LibraryQueries>().GetFacetCountsAsync("system", new LibraryFilter(), ct: ct))
            .Where(s => s.Value != SearchQuery.Unknown).OrderBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase).Select(s => new FilterChoice(s.Value, s.Label)),
    ];

    /// <summary>The cards for these binders: each with its date, item count and the covers of its first two books.</summary>
    internal static async Task<IReadOnlyList<BinderCard>> CardsAsync(UserLibrary library, IReadOnlyList<SessionPackInfo> packs, IReadOnlyList<FilterChoice> systems,
        CancellationToken ct)
    {
        if (packs.Count == 0) return [];
        var entries = (await library.Get<LibraryQueries>().ListAsync(new LibraryFilter([.. packs.SelectMany(p => p.FirstEntries).Distinct()]), ct: ct))
            .ToDictionary(e => e.EntryId);
        var away = await library.Get<LibraryStore>().GetUnavailableAsync(ct);
        string System(string key) => systems.FirstOrDefault(s => s.Value == key)?.Label ?? key;
        return [.. packs.Select(p => new BinderCard(p.Id, p.Title, BinderText.CardDate(p, System), BinderText.CountLabel(p.ItemCount),
            [.. p.FirstEntries.Where(entries.ContainsKey).Select(e => LibraryApi.ToCard(entries[e], away.GetValueOrDefault(e)))]))];
    }

    /// <summary>Home's Continue preparing: the binder worked on last that's for a game; null before there is one.</summary>
    internal static async Task<BinderCard?> PreparingAsync(UserLibrary library, CancellationToken ct) =>
        BinderGroups.Preparing(await library.Get<SessionsService>().ListAsync(ct)) is { } pack
            ? (await CardsAsync(library, [pack], await SystemsAsync(library, ct), ct))[0]
            : null;

    static async Task<BinderPage> PageAsync(UserLibrary library, SessionPackContents contents, CancellationToken ct)
    {
        var sessions = library.Get<SessionsService>();
        var rows = await BinderRows.BuildAsync(sessions, library.Get<LibraryQueries>(), contents, ct);
        var away = await library.Get<LibraryStore>().GetUnavailableAsync(ct);
        var pack = contents.Pack;
        BinderLine Line(BinderRow row) => row.Section is { } section
            ? new BinderLine(new BinderSection(section.Id, section.Name), null)
            : new BinderLine(null, Item(contents, row.Item!, away));
        return new BinderPage(pack.Id, pack.Title, BinderText.Eyebrow(pack.Date), BinderText.ListCount(pack.ItemCount), pack.Date, pack.KeepAtHand, pack.GameSystem,
            pack.Notes ?? "", [.. rows.Select(Line)], await SystemsAsync(library, ct), BinderText.SuggestedSections)
        {
            HasAtHand = AtHandGroups.Besides(await sessions.ListAsync(ct), pack.Id).Count > 0,
        };
    }

    static BinderItem Item(SessionPackContents contents, BinderItemRow row, IReadOnlyDictionary<EntryId, EntryAvailability> away) =>
        new(row.Item.Id, BinderText.Number(row.Number), row.BookTitle, row.Item.Label ?? "", row.Heading, row.Where,
            row.Item.Note ?? "", row.Card is { } card ? LibraryApi.ToCard(card, away.GetValueOrDefault(card.EntryId)) : null, row.Reason, row.IsDimmed, row.IsChanged,
            [.. BinderRows.MoveTargets(contents, row.Item).Select(m => new BinderMove(m.SectionId, m.Label))])
        {
            EntryId = row.Item.EntryId.Value,
            CanOpen = row.Target.CanOpen,
            Pages = DetailsText.Pages(row.Item.Range),
            OnlineLink = row.OnlineLink,
            OnlinePlace = row.OnlinePlace,
        };

    /// <summary>At hand's list for a binder's reader; null when there's no such binder.</summary>
    internal static async Task<AtHandList?> AtHandAsync(UserLibrary library, long id, CancellationToken ct)
    {
        var sessions = library.Get<SessionsService>();
        var queries = library.Get<LibraryQueries>();
        if (await sessions.GetAsync(id, ct) is not { } contents) return null;
        var away = await library.Get<LibraryStore>().GetUnavailableAsync(ct);
        var binders = new List<AtHandBinder>();
        var systemOf = new Dictionary<long, string?>();
        foreach (var pack in AtHandGroups.Besides(await sessions.ListAsync(ct), id))
        {
            if (await sessions.GetAsync(pack.Id, ct) is not { } other) continue;
            var rows = await BinderRows.BuildAsync(sessions, queries, other, ct);
            binders.Add(new AtHandBinder(pack.Id, pack.Title, [.. rows.Select(r => r.Item).OfType<BinderItemRow>().Select(r => Item(other, r, away))]));
            systemOf[pack.Id] = pack.GameSystem;
        }
        var systems = await AtHandGroups.SystemsOfAsync(queries, [.. contents.Items.Select(i => i.EntryId).Distinct()], ct);
        return new AtHandList([.. AtHandGroups.Of(binders, b => systemOf[b.Id], systems).Select(g => new AtHandGroup(g.Heading, g.Binders))],
            AtHandGroups.NothingInIt, AtHandGroups.NoneAtHand);
    }
}
