using System.Security.Claims;
using System.Text;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
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

        selection.MapPost("/edit", async (SelectedBooks books, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var plan = await PlanAsync(lease.Library, books.EntryIds, ct);
            if (plan.Books.Count == 0) return Results.NotFound();
            return Results.Ok(new BulkEditForm(BulkEditText.Eyebrow, plan.Heading, plan.ApplyLabel, BulkEditText.Intro, BulkEditText.ReviewIntro,
                plan.Books.Count, [.. plan.Fields.Select(f => FieldForm(f, plan.Books.Count))]));
        });

        selection.MapPost("/edit/read", async (BulkReadRequest request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (MetadataFields.Find(request.Field) is not { } field || field == MetadataFields.Title) return Results.NotFound();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var vocabulary = await lease.Library.Get<VocabularyStore>().GetAsync(ct);
            IReadOnlyList<BulkValue> values;
            string? problem;
            if (field.Multiple && !request.Whole) (values, problem) = BulkChipsField.ReadMany(field, request.Text, vocabulary);
            else
            {
                (var one, problem) = BulkField.Read(field, request.Text, vocabulary);
                values = one is null ? [] : [one];
            }
            return Results.Ok(new BulkReadResult(problem, !field.Multiple && values.Count == 1 ? BulkValueField.WillBe(field, values[0]) : "",
                [.. values.Select(v => ChipForm(BulkChipsField.Typed(field, v), request.Books))]));
        });

        selection.MapPost("/edit/review", async (BulkEditRequest request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var plan = await PlanAsync(lease.Library, request.EntryIds, ct);
            IReadOnlyList<BulkSummaryItem> lines = [.. Edit(plan, request.Changes).Summary.Select(l => new BulkSummaryItem(l.Field, l.Text, l.Warning))];
            return lines;
        });

        selection.MapPost("/edit/apply", async (BulkEditRequest request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var plan = await PlanAsync(library, request.EntryIds, ct);
            var metadata = library.Get<MetadataService>();
            MetadataProblem? problem;
            IReadOnlyList<FieldSnapshot>? undo;
            try
            {
                // Not cancelled once started: the edit is one transaction, and search should follow all of it.
                (problem, undo) = await metadata.EditManyAsync(plan.Books, Edit(plan, request.Changes).Changes, ct: CancellationToken.None);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                library.Get<ILogger<HeldActions>>().LogError(e, "A bulk edit of {Count} books failed", plan.Books.Count);
                return new BulkApplyResult(BulkEditText.SaveFailed(e.Message), null);
            }
            if (problem is not null) return new BulkApplyResult(problem.Message, null);
            var books = undo!.Select(s => s.EntryId).Distinct().Count();
            return new BulkApplyResult(null, new ActionResult(SelectionRules.Edited(books),
                books > 0 ? EditApi.Hold(library, () => metadata.UndoManyAsync(undo!, ct: CancellationToken.None), BulkEditText.UndoFailed) : null));
        });
    }

    /// <summary>The bulk editor for the ticked books still in the library.</summary>
    static async Task<BulkEditPlan> PlanAsync(UserLibrary library, IReadOnlyList<long> entryIds, CancellationToken ct) =>
        await BulkEditPlan.LoadAsync(library.Get<MetadataService>(), [.. (await CardsAsync(library, entryIds, ct)).Select(c => c.EntryId)], ct);

    static BulkFieldForm FieldForm(BulkField field, int books) => field switch
    {
        BulkValueField value => new BulkFieldForm(field.Field.Key, field.Label, false, value.Current, value.Breakdown, value.IsKnown, value.Own > 0,
            value.StartText, value.EditHint, value.Blank, BulkValueField.ResetPending, "", "", field.Choices, []),
        BulkChipsField chips => new BulkFieldForm(field.Field.Key, field.Label, true, "", "", chips.Chips.Count > 0, false, "", "", "", "",
            chips.AddLabel, chips.AddHint, field.Choices, [.. chips.Chips.Select(c => ChipForm(c, books))]),
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    static BulkChipForm ChipForm(BulkChip chip, int books) => new(chip.Value, chip.Normalized, chip.Text, chip.NewNote, chip.IsTyped, chip.Count < books,
        Look(chip, null, books), Look(chip, BulkAction.Add, books), Look(chip, BulkAction.Remove, books));

    static BulkChipLook Look(BulkChip chip, BulkAction? change, int books) =>
        new(BulkChipsField.CountText(chip, change, books), BulkChipsField.Description(chip, change, books), BulkChipsField.RemoveName(chip, change));

    /// <summary>
    /// The browser's changes on the editor's fields, as the Windows dialog holds them: the last set or reset of a
    /// single-value field, and each value added or removed, in the order the chips show.
    /// </summary>
    static (IReadOnlyList<BulkChange> Changes, IReadOnlyList<BulkSummaryLine> Summary) Edit(BulkEditPlan plan, IReadOnlyList<BulkFieldChange> requested)
    {
        var changes = new List<BulkChange>();
        var summary = new List<BulkSummaryLine>();
        foreach (var field in plan.Fields)
        {
            var mine = requested.Where(c => c.Field == field.Field.Key).ToList();
            if (mine.Count == 0) continue;
            if (field is BulkValueField value)
            {
                var last = mine[^1];
                var set = last.Action == BulkEditAction.Set && last.Value is { } v ? BulkField.Given(field.Field, v, last.Text ?? v, last.IsNew) : null;
                var reset = last.Action == BulkEditAction.Reset;
                changes.AddRange(value.Changes(set, reset));
                summary.AddRange(value.Describe(set, reset));
            }
            else if (field is BulkChipsField chips)
            {
                var chipped = mine.Where(c => c.Action is BulkEditAction.Add or BulkEditAction.Remove && c.Value is not null)
                    .Select(c => (Chip: chips.ChipFor(Given(field.Field, c)),
                        Change: c.Action == BulkEditAction.Add ? BulkAction.Add : BulkAction.Remove))
                    // A typed value can only be added.
                    .Where(c => !c.Chip.IsTyped || c.Change == BulkAction.Add)
                    .DistinctBy(c => (c.Chip.Normalized, c.Chip.IsNew))
                    .OrderBy(c => IndexOf(chips.Chips, c.Chip))
                    .ToList();
                changes.AddRange(chips.Changes(chipped));
                summary.AddRange(chips.Describe(chipped));
            }
        }
        return (changes, summary);
    }

    /// <summary>A value as the editor gave it, matched to its chip by what the chip's own normal form is.</summary>
    static BulkValue Given(MetadataField field, BulkFieldChange change)
    {
        var value = BulkField.Given(field, change.Value!, change.Text ?? change.Value!, change.IsNew);
        return change.Normalized is { } normalized ? value with { Normalized = normalized } : value;
    }

    static int IndexOf(IReadOnlyList<BulkChip> chips, BulkChip chip)
    {
        for (var i = 0; i < chips.Count; i++)
            if (ReferenceEquals(chips[i], chip)) return i;
        return int.MaxValue;
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
