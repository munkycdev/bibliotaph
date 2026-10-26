using System.Reflection;
using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using ReviewList = Bibliotaph.Processing.ReviewList;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/review: Needs review's cards, the decisions the Windows app's cards make, each with its note and Undo, and the
/// files needing attention. A card is found again by what it names, and refused when what it proposes has changed
/// since the page showed it, so nobody accepts a suggestion they didn't see.
/// </summary>
public static class ReviewApi
{
    const string Changed = "This suggestion has changed since the list was made. Show the new list to see it as it is now.";

    public static void MapReviewApi(this RouteGroupBuilder api)
    {
        var review = api.MapGroup("/review");

        review.MapGet("", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var list = await lease.Library.Get<ReviewService>().GetQueueAsync(ct);
            return ListOf(list, await ReviewOffers.CardsAsync(lease.Library, list, ct));
        });

        review.MapReviewOffers();

        review.MapGet("/mode", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return new ReviewMode(await lease.Library.Get<ReviewService>().GetReviewAllAsync(ct));
        });

        // Every book's cards are counted again, so the answer waits until Needs review's count is right.
        review.MapPut("/mode", async (ReviewMode mode, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var reviews = lease.Library.Get<ReviewService>();
            await reviews.SetReviewAllAsync(mode.All, ct);
            return new ReviewMode(await reviews.GetReviewAllAsync(ct));
        });

        review.MapPost("/fields/accept", (ReviewFieldDecision decision, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DecideAsync(libraries, principal, decision.Id, decision.GroupKey, async (library, item, text) =>
            {
                var reviews = library.Get<ReviewService>();
                var undo = await reviews.AcceptAsync(item, ct);
                return Results.Ok(new ActionResult(text.Accepted, Hold(library, reviews, [undo])));
            }, ct));

        review.MapPost("/fields/reject", (ReviewFieldDecision decision, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DecideAsync(libraries, principal, decision.Id, decision.GroupKey, async (library, item, text) =>
            {
                var reviews = library.Get<ReviewService>();
                var undo = await reviews.RejectAsync(item, ct);
                return Results.Ok(new ActionResult(text.Rejected, Hold(library, reviews, [undo])));
            }, ct));

        review.MapPost("/fields/edit", (ReviewFieldEdit edit, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DecideAsync(libraries, principal, edit.Id, edit.GroupKey, async (library, item, _) =>
            {
                var reviews = library.Get<ReviewService>();
                var (problem, undo) = await reviews.EditAsync(item, edit.Text, ct);
                if (problem is not null) return Results.Problem(problem.Message, statusCode: StatusCodes.Status400BadRequest);
                return Results.Ok(new ActionResult(ReviewText.Edited(item, edit.Text), Hold(library, reviews, [undo!])));
            }, ct));

        // Every waiting card that proposes the same, shown or not. Each shown card can be undone on its own, and the
        // banner undoes them all.
        review.MapPost("/fields/accept-all", (ReviewAcceptAll request, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            DecideAsync(libraries, principal, request.Id, request.GroupKey, async (library, _, _) =>
            {
                var reviews = library.Get<ReviewService>();
                var list = await reviews.GetQueueAsync(ct);
                var group = list.Items.Where(i => i.Issue.GroupKey == request.GroupKey).ToList();
                var undo = await reviews.AcceptAllAsync(group, ct);
                var shown = request.Shown.ToHashSet(StringComparer.Ordinal);
                var cards = new List<ReviewCardDone>();
                for (var i = 0; i < group.Count; i++)
                {
                    var id = IdOf(group[i]);
                    if (!shown.Contains(id)) continue;
                    cards.Add(new ReviewCardDone(id, new FieldCardText(group[i], list.Vocabulary).Accepted, Hold(library, reviews, [undo[i]])));
                }
                return Results.Ok(new ReviewAcceptedAll(cards, group.Count, ReviewText.AcceptedAll(group.Count), Hold(library, reviews, undo)));
            }, ct));

        review.MapPost("/terms/{id:long}/add", (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            TermAsync(libraries, principal, id, async (reviews, term) =>
                (await reviews.AddTermAsync(term, ct), ReviewText.TermAdded(term)), ct));

        review.MapPost("/terms/{id:long}/reject", (long id, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            TermAsync(libraries, principal, id, async (reviews, term) =>
                (await reviews.RejectTermAsync(term, ct), ReviewText.TermRejected(term)), ct));

        review.MapPost("/terms/{id:long}/map", (long id, ReviewTermMap map, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
            TermAsync(libraries, principal, id, async (reviews, term) =>
            {
                var vocabulary = await reviews.GetVocabularyAsync(ct);
                if (ReviewText.TermTargets(term, vocabulary).FirstOrDefault(t => t.Key == map.Target) is not { } target) return null;
                return (await reviews.MapTermAsync(term, target.Key, ct), ReviewText.TermFiled(term, target.Label));
            }, ct));

        review.MapGet("/files", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await FilesAsync(lease.Library, ct);
        });

        // Try again, for one file, a card's or every file listed; a locked or protected file isn't among them.
        review.MapPost("/files/retry", async (ReviewRetry retry, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var files = await FilesAsync(lease.Library, ct);
            var retryable = files.Cards.SelectMany(c => c.Files).Where(f => f.CanRetry).Select(f => f.DocumentId!.Value).ToHashSet();
            var ids = retry.Documents.Where(retryable.Contains).Distinct().ToList();
            if (ids.Count > 0) await lease.Library.Indexing.RetryAsync(ids, ct);
            return Results.Ok(await FilesAsync(lease.Library, ct));
        });
    }

    /// <summary>"12:title:Conflict": the entry, the field and the kind of card, which is one card in the queue.</summary>
    internal static string IdOf(ReviewItem item) => $"{item.EntryId.Value}:{item.Issue.Field.Key}:{item.Issue.Kind}";

    static bool TryParse(string id, out EntryId entryId, out string fieldKey, out ReviewKind kind)
    {
        (entryId, fieldKey, kind) = (default, "", default);
        var first = id.IndexOf(':', StringComparison.Ordinal);
        var last = id.LastIndexOf(':');
        if (first <= 0 || last <= first || !long.TryParse(id[..first], out var entry) || !Enum.TryParse(id[(last + 1)..], out kind)) return false;
        (entryId, fieldKey) = (new EntryId(entry), id[(first + 1)..last]);
        return true;
    }

    static ReviewSuggestions ListOf(ReviewList list, IReadOnlyList<ReviewOfferCard> offers)
    {
        var fields = list.Items.Select(i => FieldCard(i, list.Vocabulary)).ToList();
        var shown = offers.Select(o => o.Kind).ToHashSet(StringComparer.Ordinal);
        if (list.Terms.Count > 0) shown.Add("terms");
        IReadOnlyList<ReviewShowKind> kinds =
        [
            .. ReviewText.Kinds.Where(k => shown.Contains(k.Key)).Select(k => new ReviewShowKind(k.Key, k.Name)),
            .. list.Items.DistinctBy(i => i.Issue.Field.Key).OrderBy(i => i.Issue.Field.Label, StringComparer.CurrentCultureIgnoreCase)
                .Select(i => new ReviewShowKind(ReviewText.FieldKey(i), i.Issue.Field.Label)),
        ];
        var terms = list.Terms.Select(t => new ReviewTermCard(t.Id, ReviewText.Term(t), ReviewText.TermChip(t), ReviewText.TermDetail(t),
            ReviewText.TermExample(t), ReviewText.TermAdd(t), [.. ReviewText.TermTargets(t, list.Vocabulary).Select(v => new ReviewTermTarget(v.Key, v.Label))])).ToList();
        var choices = list.Items.Select(i => i.Issue.Field).DistinctBy(f => f.Key)
            .Select(f => (f.Key, Values: FieldText.Choices(f, list.Vocabulary))).Where(c => c.Values.Count > 0)
            .ToDictionary(c => c.Key, c => c.Values);
        return new ReviewSuggestions(terms, fields, kinds, list.Count, choices, offers);
    }

    static ReviewFieldCard FieldCard(ReviewItem item, Vocabulary vocabulary)
    {
        var text = new FieldCardText(item, vocabulary);
        return new ReviewFieldCard(IdOf(item), item.Issue.GroupKey, ReviewText.FieldKey(item), item.EntryId.Value, item.Title, item.Issue.Field.Key,
            text.FieldLabel, text.Reason, text.CurrentLabel, text.CurrentText, text.CurrentSource, text.SuggestedLabel, text.SuggestedText,
            text.Evidence, text.PageText, text.CanAccept, text.CanAcceptAll, text.AcceptAllLabel, text.AcceptAllHint, text.CanReject,
            text.RejectLabel, text.RejectHint, text.EditLabel, text.EditStart, text.EditHint, item.Issue.Field.Multiple);
    }

    /// <summary>Finds the card again and checks it still proposes what the page showed, then decides it.</summary>
    static async Task<IResult> DecideAsync(UserLibraries libraries, ClaimsPrincipal principal, string id, string groupKey,
        Func<UserLibrary, ReviewItem, FieldCardText, Task<IResult>> decide, CancellationToken ct)
    {
        if (!TryParse(id, out var entryId, out var fieldKey, out var kind)) return Results.NotFound();
        await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
        var reviews = lease.Library.Get<ReviewService>();
        if (await reviews.FindAsync(entryId, fieldKey, kind, ct) is not { } issue || issue.GroupKey != groupKey)
            return Results.Problem(Changed, statusCode: StatusCodes.Status409Conflict);
        var item = new ReviewItem(entryId, 0, "", issue, 1);
        return await decide(lease.Library, item, new FieldCardText(item, await reviews.GetVocabularyAsync(ct)));
    }

    static async Task<IResult> TermAsync(UserLibraries libraries, ClaimsPrincipal principal, long id,
        Func<ReviewService, PendingTerm, Task<(TermDecision Decision, string Message)?>> decide, CancellationToken ct)
    {
        await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
        var library = lease.Library;
        var reviews = library.Get<ReviewService>();
        if (await reviews.FindTermAsync(id, ct) is not { } term) return Results.Problem(Changed, statusCode: StatusCodes.Status409Conflict);
        if (await decide(reviews, term) is not var (decision, message)) return Results.NotFound();
        return Results.Ok(new ActionResult(message, EditApi.Hold(library, () => reviews.UndoTermAsync(decision, CancellationToken.None), ReviewText.UndoFailed)));
    }

    static string? Hold(UserLibrary library, ReviewService reviews, IReadOnlyList<FieldSnapshot> undo) =>
        EditApi.Hold(library, () => reviews.UndoAsync(undo, CancellationToken.None), ReviewText.UndoFailed);

    /// <summary>
    /// Files needing attention, as the Windows app's second tab lists them: failed and blocked stages, files that
    /// couldn't be read, and files in ZIPs that aren't read. A locked file opens in the reader to be unlocked, and one
    /// with DRM is downloaded to open in another app.
    /// </summary>
    static async Task<ReviewFiles> FilesAsync(UserLibrary library, CancellationToken ct)
    {
        var stages = library.Get<IStageCatalog>();
        var rows = (await library.Get<IndexQueries>().GetAttentionAsync(await library.Get<LibraryStore>().GetVisibleDocumentIdsAsync(ct), ct))
            .Select(item => ReviewText.Row(item, stages))
            .Concat(library.Indexing.Unreadable.Select(u => ReviewText.Unreadable(Path.GetFileName(u.Path), u.Reason)))
            .Concat((await library.Get<LibraryStore>().GetProblemsAsync(ct)).Select(p => ReviewText.Problem(Path.GetFileName(p.FullPath), p.Problem, p.FullPath)));
        var entries = library.Get<EntryStore>();
        var cards = new List<AttentionCard>();
        foreach (var g in ReviewText.Group(rows))
        {
            var files = new List<AttentionFile>(g.Count);
            foreach (var r in g)
            {
                // Only a locked or protected file opens from here, so only those need their card found.
                var card = r is { DocumentId: { } id } && (r.CanUnlock || r.CanOpenElsewhere) ? await entries.GetCardAsync(id, ct) : null;
                files.Add(new AttentionFile(r.DocumentId, r.Title, r.CanRetry, r.CanUnlock && card is not null, r.CanOpenElsewhere && card is not null,
                    card?.Value));
            }
            cards.Add(new AttentionCard(ReviewText.GroupHeading(g), g[0].Detail, g.Any(r => r.CanRetry),
                g[0].IsBug ? ReviewText.BugDetails(Version, g) : null, files));
        }
        return new ReviewFiles(cards, cards.Any(c => c.CanRetry));
    }

    /// <summary>"1.3.600 (4578ab9)": the server's build, as the Windows app names its own in a bug report.</summary>
    static string Version { get; } = VersionOf(typeof(ReviewApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    static string VersionOf(string informational)
    {
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        var commit = plus < 0 ? "" : informational[(plus + 1)..];
        var version = plus < 0 ? informational : informational[..plus];
        return (commit.Length == 0 ? version : $"{version} ({commit[..Math.Min(7, commit.Length)]})") + " on the web";
    }
}
