using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
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

        // Whole books, or a page found by search, with Undo taking out just what was added.
        binders.MapPost("/{id:long}/add", async (long id, BinderAdd add, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var sessions = library.Get<SessionsService>();
            if (await sessions.GetAsync(id, ct) is not { } contents) return Results.NotFound();
            IReadOnlyList<long> added;
            string what;
            if (add.DocumentId is { } document && add.Page is { } page)
            {
                var labels = await library.Get<PartsService>().GetPageLabelsAsync(document, ct);
                what = BinderText.PageWhat(page < labels.Count ? labels[page] : null, page);
                added = await sessions.AddPagesAsync(id, document, page, page, ct: ct) is { } item ? [item] : [];
            }
            else
            {
                what = add.Books.Count == 1 && add.Title is { Length: > 0 } title ? title : SelectionRules.Books(add.Books.Count);
                added = add.Books.Count == 0 ? [] : await sessions.AddAsync(id, [.. add.Books.Distinct().Select(b => new NewSessionItem(new EntryId(b)))], ct: ct);
            }
            return Results.Ok(added.Count == 0
                ? new ActionResult(BinderText.NotAdded)
                : new ActionResult(BinderText.Added(what, contents.Pack.Title),
                    EditApi.Hold(library, async () => await sessions.RemoveItemsAsync(added, CancellationToken.None), BinderText.Failed)));
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
        var rows = await BinderRows.BuildAsync(library.Get<SessionsService>(), library.Get<LibraryQueries>(), contents, ct);
        var away = await library.Get<LibraryStore>().GetUnavailableAsync(ct);
        var pack = contents.Pack;
        BinderLine Line(BinderRow row) => row.Section is { } section
            ? new BinderLine(new BinderSection(section.Id, section.Name), null)
            : new BinderLine(null, Item(row.Item!));
        BinderItem Item(BinderItemRow row) => new(row.Item.Id, BinderText.Number(row.Number), row.BookTitle, row.Item.Label ?? "", row.Heading, row.Where,
            row.Item.Note ?? "", row.Card is { } card ? LibraryApi.ToCard(card, away.GetValueOrDefault(card.EntryId)) : null, row.Reason, row.IsDimmed, row.IsChanged,
            [.. BinderRows.MoveTargets(contents, row.Item).Select(m => new BinderMove(m.SectionId, m.Label))]);
        return new BinderPage(pack.Id, pack.Title, BinderText.Eyebrow(pack.Date), BinderText.ListCount(pack.ItemCount), pack.Date, pack.KeepAtHand, pack.GameSystem,
            pack.Notes ?? "", [.. rows.Select(Line)], await SystemsAsync(library, ct), BinderText.SuggestedSections);
    }
}
