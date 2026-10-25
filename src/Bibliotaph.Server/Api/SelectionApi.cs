using System.Security.Claims;
using System.Text;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/library/selection: Select mode's actions on the ticked books, as the Windows Library's Select bar does them:
/// which fit, Same book, Pack together, Remove adventures and Export these. A ticked book no longer in the library is
/// left out.
/// </summary>
public static class SelectionApi
{
    public static void MapSelectionApi(this RouteGroupBuilder api)
    {
        var selection = api.MapGroup("/library/selection");

        selection.MapPost("", async (SelectionQuery query, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var cards = await CardsAsync(lease.Library, query.EntryIds, ct);
            return new SelectionInfo(SelectionRules.Summary(query.EntryIds.Count, query.NotShown), SelectionRules.Books(query.EntryIds.Count),
                SelectionRules.CanJoin(cards), SelectionRules.CanPack(cards), SelectionRules.CanRemoveAdventures(cards));
        });

        selection.MapPost("/same-book", async (SelectedBooks books, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var cards = await CardsAsync(lease.Library, books.EntryIds, ct);
            if (!SelectionRules.CanJoin(cards)) return Gone(SameBookPlan.Gone);
            var plan = await SameBookPlan.LoadAsync(lease.Library.Get<CopiesService>(), cards, ct);
            return Results.Ok(new SameBookPreview(plan.Heading, plan.Elsewhere,
                [.. plan.Files.Select(f => new SameBookChoice(f.DocumentId, f.Card, f.Path is { } path ? FileName(path) : f.Name, f.Detail))], plan.Opens));
        });

        selection.MapPost("/same-book/join", async (SameBookJoin request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var cards = await CardsAsync(library, request.EntryIds, ct);
            var copies = library.Get<CopiesService>();
            if (!SelectionRules.CanJoin(cards) || await copies.JoinAsOneBookAsync([.. cards.Select(c => c.EntryId)], request.Opens, ct) is not { } join)
                return Gone(SameBookPlan.Gone);
            var joined = await library.Get<LibraryQueries>().GetEntriesAsync([join.EntryId], ct);
            var title = joined.Count > 0 ? joined[0].Title : "one book";
            return Results.Ok(new ActionResult(SameBookPlan.Joined(cards.Count, title),
                EditApi.Hold(library, () => copies.UndoJoinAsOneBookAsync(join, CancellationToken.None), SameBookPlan.UndoFailed)));
        });

        selection.MapPost("/pack", async (SelectedBooks books, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var cards = await CardsAsync(lease.Library, books.EntryIds, ct);
            if (!SelectionRules.CanPack(cards)) return Gone(PackTogetherPlan.Gone);
            var plan = await PackTogetherPlan.LoadAsync(lease.Library.Get<LibraryStore>(), cards, ct);
            return Results.Ok(new PackTogetherPreview(plan.Eyebrow, plan.Heading, plan.Explanation, plan.Name));
        });

        selection.MapPost("/pack/make", async (PackTogetherRequest request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (request.Name.Trim() is not { Length: > 0 } name)
                return Results.Problem("Give the pack a name.", statusCode: StatusCodes.Status400BadRequest);
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var cards = await CardsAsync(library, request.EntryIds, ct);
            if (!SelectionRules.CanPack(cards)) return Gone(PackTogetherPlan.Gone);
            var plan = await PackTogetherPlan.LoadAsync(library.Get<LibraryStore>(), cards, ct);
            var packs = library.Get<PackService>();
            if (await packs.PackTogetherAsync([.. cards.Select(c => c.EntryId)], name, ct) is not { } result) return Gone(PackTogetherPlan.Gone);
            return Results.Ok(new ActionResult(plan.Done(name),
                EditApi.Hold(library, () => packs.UndoPackTogetherAsync(result, CancellationToken.None), PackTogetherPlan.UndoFailed)));
        });

        // Each adventure's card goes at once, as each one's own Remove adventure would, with one Undo for them all.
        selection.MapPost("/remove-adventures", async (SelectedBooks books, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var cards = await CardsAsync(library, books.EntryIds, ct);
            if (!SelectionRules.CanRemoveAdventures(cards)) return Results.NotFound();
            var parts = library.Get<PartsService>();
            var removed = new List<RemovedPart>();
            string message;
            try
            {
                foreach (var card in cards)
                    if (await parts.RemoveAsync(card.EntryId, ct) is { } part) removed.Add(part);
                message = SelectionRules.Removed(removed.Count);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                library.Get<ILogger<UserLibrary>>().LogError(e, "Removing {Count} selected adventures failed after {Removed}", cards.Count, removed.Count);
                message = SelectionRules.RemoveFailed(removed.Count);
            }
            return Results.Ok(new ActionResult(message, removed.Count == 0 ? null : EditApi.Hold(library, async () =>
            {
                foreach (var part in Enumerable.Reverse(removed)) await parts.RestoreAsync(part, CancellationToken.None);
            }, SelectionRules.RemoveUndoFailed)));
        });

        // The CSV Settings > Backup's Export CSV writes, for the ticked books, with each file's place in the user's storage.
        selection.MapPost("/export", async (SelectedBooks books, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var rows = await library.Get<ExportService>().GetCsvRowsAsync([.. books.EntryIds.Select(id => new EntryId(id))], ct);
            var drives = library.Get<UserDrives>();
            var csv = new StringBuilder();
            csv.Append(Csv.Row(ExportService.CsvHeader)).Append("\r\n");
            foreach (var row in rows)
                csv.Append(Csv.Row(row.Select((value, i) => i == row.Count - 1 && value is not null ? FolderText.Location(value, drives) : value))).Append("\r\n");
            return new ExportedCsv(SelectionRules.ExportName, csv.ToString(), SelectionRules.Exported(rows.Count, SelectionRules.ExportName));
        });
    }

    /// <summary>The ticked books still in the library, in the order they were ticked.</summary>
    static async Task<IReadOnlyList<LibraryEntry>> CardsAsync(UserLibrary library, IReadOnlyList<long> entryIds, CancellationToken ct)
    {
        var ids = entryIds.Distinct().Select(id => new EntryId(id)).ToList();
        var found = (await library.Get<LibraryQueries>().GetEntriesAsync(ids, ct)).ToDictionary(e => e.EntryId);
        return [.. ids.Where(found.ContainsKey).Select(id => found[id])];
    }

    static string FileName(string path) => DrivePath.TryParse(path, out _, out var inner) ? inner[(inner.LastIndexOf('/') + 1)..] : Path.GetFileName(path);

    static IResult Gone(string message) => Results.Problem(message, statusCode: StatusCodes.Status409Conflict);
}
