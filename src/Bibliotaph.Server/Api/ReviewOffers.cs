using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using ReviewList = Bibliotaph.Processing.ReviewList;

namespace Bibliotaph.Server.Api;

/// <summary>
/// Needs review's cards that ask one question each, as the Windows app's: new versions, image packs, books owned
/// elsewhere, catalogue records that may be a book's, places to check in a new version, books cataloged wrong, folder
/// rules, collections from folders and books for collections. Each is found again by its id and answered only when it still says what the page showed.
/// </summary>
static class ReviewOffers
{
    const string Changed = "This card has changed since the list was made. Show the new list to see it as it is now.";

    /// <summary>A card, and what it was made from, which answering it needs.</summary>
    sealed record Offer(ReviewOfferCard Card, object Item);

    public static void MapReviewOffers(this RouteGroupBuilder review)
    {
        review.MapPost("/offers/answer", async (ReviewOfferAnswer answer, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await FindAsync(library, answer.Id, answer.Stamp, ct) is not { } offer) return Results.Problem(Changed, statusCode: StatusCodes.Status409Conflict);
            if (offer.Card.Choices.All(c => c.Key != answer.Choice) && offer.Card.Dismiss != answer.Choice) return Results.NotFound();
            var reviews = library.Get<ReviewService>();
            var vocabulary = await reviews.GetVocabularyAsync(ct);
            (Func<Task>? Undo, string Message) done = (offer.Item, answer.Choice) switch
            {
                (VersionItem v, var choice) => await VersionAsync(reviews, v, choice, ct),
                (PackProposal p, "pack") => (await reviews.AnswerPackAsync(p, PackAnswer.Packed, ct), new PackCardText(p).Packed),
                (PackProposal p, _) => (await reviews.AnswerPackAsync(p, PackAnswer.Split, ct), new PackCardText(p).KeptSeparate),
                (ElsewhereItem e, "same") => (await reviews.AnswerElsewhereAsync(e.Match, ElsewhereAnswer.SameBook, ct), new ElsewhereCardText(e).SameBook),
                (ElsewhereItem e, _) => (await reviews.AnswerElsewhereAsync(e.Match, ElsewhereAnswer.SeparateBook, ct), new ElsewhereCardText(e).KeptSeparate),
                (LookupMatchItem l, "same") => (await reviews.AnswerLookupAsync(l.Match, LookupAnswer.SameBook, ct), new LookupMatchCardText(l).SameBook),
                (LookupMatchItem l, _) => (await reviews.AnswerLookupAsync(l.Match, LookupAnswer.NotThisBook, ct), new LookupMatchCardText(l).NotThisBook),
                (CorrectionItem c, "fine") => (await reviews.DismissCorrectionAsync(c, ct), new CorrectionCardText(c).LeftAsIs),
                (PartProposalItem p, "notsplit") => (await library.Get<PartProposalService>().NotSplitAsync(p, ct), new PartProposalCardText(p, []).NotSplit),
                (UnplacedPart u, "remove") => await RemoveAsync(library.Get<PartsService>(), u, ct),
                (FolderRuleInfo r, var choice) => (await reviews.AnswerRuleAsync(r, choice == "use", ct),
                    choice == "use" ? new FolderRuleCardText(r, vocabulary).Used : new FolderRuleCardText(r, vocabulary).NoRule),
                (FolderCollectionProposal f, var choice) => (await reviews.AnswerFolderAsync(f, Answer(choice), ct), Said(new FolderCollectionCardText(f), choice)),
                (CollectionSuggestion s, var choice) => (await reviews.AnswerSuggestionAsync(s, Answer(choice), ct), Said(new CollectionSuggestionCardText(s, vocabulary), choice)),
                _ => (null, ""),
            };
            if (done.Message.Length == 0) return Results.NotFound();
            return Results.Ok(new ActionResult(done.Message, EditApi.Hold(library, done.Undo, ReviewText.UndoFailed)));
        });

        // Accept on a proposal: a card for each ticked adventure, as typed. Pages that don't read keep the card, saying
        // which, as on Windows.
        review.MapPost("/offers/parts/accept", async (ReviewPartsAccept accept, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await FindAsync(library, accept.Id, accept.Stamp, ct) is not { Item: PartProposalItem item }) return Results.Problem(Changed, statusCode: StatusCodes.Status409Conflict);
            var text = new PartProposalCardText(item, await library.Get<PartsService>().GetPageLabelsAsync(item.DocumentId, ct));
            var chosen = new List<ProposedPart>();
            foreach (var row in accept.Parts.DistinctBy(r => r.Index))
            {
                if (row.Index < 0 || row.Index >= item.Parts.Count) return Results.NotFound();
                if (text.Read(item.Parts[row.Index], row.Title, row.From, row.To) is not { } part)
                    return Results.Problem(PartProposalCardText.Unreadable(row.Title), statusCode: StatusCodes.Status400BadRequest);
                chosen.Add(part);
            }
            var accepted = await library.Get<PartProposalService>().AcceptAsync(item, chosen, ct);
            if (accepted.Problem is { } problem) return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(new ActionResult(text.Accepted(chosen.Count), EditApi.Hold(library, accepted.Undo, ReviewText.UndoFailed)));
        });

        // Set its pages, for an adventure whose pages weren't found in its file as it is now.
        review.MapPost("/offers/parts/pages", async (ReviewPartPagesSet set, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await FindAsync(library, set.Id, set.Stamp, ct) is not { Item: UnplacedPart part }) return Results.Problem(Changed, statusCode: StatusCodes.Status409Conflict);
            var parts = library.Get<PartsService>();
            var labels = await parts.GetPageLabelsAsync(part.DocumentId, ct);
            if (PageNumbers.Find(set.From, labels, labels.Count) is not { } first
                || (set.To.Trim().Length == 0 ? first : PageNumbers.Find(set.To, labels, labels.Count)) is not { } last)
                return Results.Problem(DetailsText.PagesHint, statusCode: StatusCodes.Status400BadRequest);
            var pages = new PartPages(first, last);
            var moved = await parts.SetPagesAsync(part.EntryId, part.DocumentId, pages, ct);
            if (moved.Problem is { } problem) return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
            return Results.Ok(new ActionResult(new UnplacedPartCardText(part).Moved(pages, labels), EditApi.Hold(library, moved.Undo, ReviewText.UndoFailed)));
        });

        // Use this page: the place opens at the page suggested for it from now on. A place that has gone meanwhile
        // needs nothing, so it counts as checked too.
        review.MapPost("/offers/places/use", async (ReviewPlaceUse use, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if (await FindAsync(library, use.Id, use.Stamp, ct) is not { Item: RevisionItem revision }) return Results.Problem(Changed, statusCode: StatusCodes.Status409Conflict);
            if (revision.Places.FirstOrDefault(p => KeyOf(p) == use.Place) is not { } place) return Results.NotFound();
            var undo = await library.Get<ReviewService>().UsePlaceAsync(place, ct);
            return Results.Ok(new ActionResult("", EditApi.Hold(library, undo, ReviewText.UndoFailed)));
        });
    }

    /// <summary>The offer cards, in the order the Windows app shows them after the new terms.</summary>
    public static async Task<IReadOnlyList<ReviewOfferCard>> CardsAsync(UserLibrary library, ReviewList list, CancellationToken ct) =>
        [.. (await OffersAsync(library, list, ct)).Select(o => o.Card)];

    static async Task<Offer?> FindAsync(UserLibrary library, string id, string stamp, CancellationToken ct)
    {
        var list = await library.Get<ReviewService>().GetOffersAsync(ct);
        var offer = (await OffersAsync(library, list, ct)).FirstOrDefault(o => o.Card.Id == id);
        return offer?.Card.Stamp == stamp ? offer : null;
    }

    static async Task<IReadOnlyList<Offer>> OffersAsync(UserLibrary library, ReviewList list, CancellationToken ct)
    {
        var drives = library.Get<UserDrives>();
        var queries = library.Get<IndexQueries>();
        string? Where(string? path) => path is null ? null : FolderText.Location(path, drives);
        var offers = new List<Offer>();

        foreach (var item in list.Versions)
        {
            var text = new VersionCardText(item);
            offers.Add(Make(item, $"versions:{item.Version.Id}", "versions", item.Title, VersionCardText.Chip, text.Heading, Where(item.Path), text.Evidence,
                text.Detail, text.Choices, null, new ReviewLink(item.Version.EntryId.Value, item.Title, "Open this file"),
                [new ReviewLink(item.Version.MatchedEntryId.Value, "Open the book", "Open the book")]));
        }
        foreach (var proposal in list.Packs)
        {
            var text = new PackCardText(proposal);
            var tiles = new List<string>();
            foreach (var image in proposal.Images)
            {
                if (tiles.Count == PackCardText.Tiles) break;
                if (await queries.GetCoverAsync(image, ct) is { } cover) tiles.Add(cover);
            }
            offers.Add(Make(proposal, $"packs:{proposal.PackId.Value}", "packs", proposal.Name, PackCardText.Chip, text.Heading, Where(proposal.Place.FullPath), null,
                text.Detail, PackCardText.Choices, "separate", null, [], tiles));
        }
        foreach (var item in list.Elsewhere ?? [])
        {
            var text = new ElsewhereCardText(item);
            offers.Add(Make(item, $"elsewhere:{item.Match.Id}", "elsewhere", item.FileTitle, ElsewhereCardText.Chip, text.Heading, Where(item.Path), null,
                text.Detail, ElsewhereCardText.Choices, null, new ReviewLink(item.Match.FileEntryId.Value, item.FileTitle, "Open this file"), []));
        }
        foreach (var item in list.Records ?? [])
        {
            var text = new LookupMatchCardText(item);
            var record = item.Match.Record;
            offers.Add(Make(item, $"records:{item.Match.EntryId.Value}:{record.Source}:{record.Key}", "records", item.BookTitle, LookupMatchCardText.Chip, text.Heading,
                Where(item.Path), text.Evidence, text.Detail, LookupMatchCardText.Choices, null, new ReviewLink(item.Match.EntryId.Value, item.BookTitle, "Open this book"), []));
        }
        foreach (var item in list.Revisions ?? [])
        {
            var rows = RevisionCardText.Rows(item);
            var places = rows.Select(p => new ReviewPlace(KeyOf(p), RevisionCardText.Describe(p), RevisionCardText.Where(p, false),
                RevisionCardText.Where(p, true), p.Outcome == PageCheck.Checked)).ToList();
            var card = new ReviewOfferCard($"revisions:{item.EntryId.Value}:{item.DocumentId}", "revisions", "", item.BookTitle, RevisionCardText.Chip,
                RevisionCardText.Heading(item.BookTitle, item.Found, places.Count(p => !p.Checked)), Where(item.Path), null, RevisionCardText.Detail(item), [], null,
                new ReviewLink(item.EntryId.Value, item.BookTitle, "Open the new version"), [], [], places, item.Found, RevisionCardText.Found(item),
                RevisionCardText.FoundMore(item), null);
            // Using a page changes what a place says but not which places the card is about.
            offers.Add(new Offer(card with { Stamp = Stamp([item.BookTitle, .. places.Select(p => p.Key)]) }, item));
        }
        var parts = library.Get<PartsService>();
        foreach (var item in list.Proposals ?? [])
        {
            var text = new PartProposalCardText(item, await parts.GetPageLabelsAsync(item.DocumentId, ct));
            var proposed = item.Parts.Select(p =>
            {
                var (from, to) = text.Typed(p);
                return new ReviewProposedPart(p.Title, from, to, p.Pages.Describe(text.Labels), PartProposalCardText.Levels(p), PartProposalCardText.Players(p));
            }).ToList();
            var card = new ReviewOfferCard($"proposals:{item.FileEntryId.Value}", "parts", "", item.FileTitle, text.Chip, text.Heading, null, null, text.Detail, [],
                "notsplit", new ReviewLink(item.FileEntryId.Value, item.FileTitle, "Open this file"), [], [], [], 0, [], "", null, proposed);
            offers.Add(new Offer(card with { Stamp = Stamp([text.Heading, text.Detail, .. proposed.Select(p => $"{p.Title}|{p.From}|{p.To}")]) }, item));
        }
        foreach (var part in list.Parts ?? [])
        {
            var text = new UnplacedPartCardText(part);
            var (from, to) = text.Start(await parts.GetPageLabelsAsync(part.DocumentId, ct));
            var offer = Make(part, $"unplaced:{part.EntryId.Value}", "parts", part.Title, UnplacedPartCardText.Chip, text.Heading, null, null,
                UnplacedPartCardText.Detail, UnplacedPartCardText.Choices, null, new ReviewLink(part.FileEntryId.Value, part.Title, "Open its file"), []);
            offers.Add(offer with { Card = offer.Card with { Pages = new ReviewPartPages(from, to) } });
        }
        foreach (var item in list.Corrections ?? [])
        {
            var text = new CorrectionCardText(item);
            offers.Add(Make(item, $"corrections:{item.EntryId.Value}", "corrections", item.Title, CorrectionCardText.Chip, CorrectionCardText.Heading, null, null,
                CorrectionCardText.Detail, CorrectionCardText.Choices, null, new ReviewLink(item.EntryId.Value, item.Title, "Open this book"), [],
                fix: item.EntryId.Value));
        }
        foreach (var rule in list.Rules ?? [])
        {
            var text = new FolderRuleCardText(rule, list.Vocabulary);
            offers.Add(Make(rule, $"rules:{rule.Id}", "rules", text.Title, FolderRuleCardText.Chip, text.Heading, Where(FolderRuleText.FullPath(rule)), null,
                FolderRuleCardText.Detail, FolderRuleCardText.Choices, "no", null, []));
        }
        foreach (var proposal in list.Folders ?? [])
        {
            var text = new FolderCollectionCardText(proposal);
            var folder = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(proposal.Folder.Folder)))[..16];
            offers.Add(Make(proposal, $"folders:{proposal.Folder.RootId}:{folder}", "folders", proposal.Name, FolderCollectionCardText.Chip, text.Heading,
                Where(proposal.FullPath), null, text.Detail, FolderCollectionCardText.Choices, "never", null, []));
        }
        foreach (var suggestion in list.Suggestions ?? [])
        {
            var text = new CollectionSuggestionCardText(suggestion, list.Vocabulary);
            offers.Add(Make(suggestion, $"suggestions:{suggestion.CollectionId}", "suggestions", suggestion.Name, CollectionSuggestionCardText.Chip, text.Heading,
                null, null, text.Detail, CollectionSuggestionCardText.Choices, "never", null,
                [.. suggestion.Books.Select(b => new ReviewLink(b.EntryId.Value, b.Title, "Open this book"))]));
        }
        return offers;
    }

    static Offer Make(object item, string id, string kind, string title, string chip, string heading, string? path, string? evidence, string detail,
        IReadOnlyList<ReviewChoiceText> choices, string? dismiss, ReviewLink? book, IReadOnlyList<ReviewLink> links, IReadOnlyList<string>? tiles = null,
        long? fix = null)
    {
        var card = new ReviewOfferCard(id, kind, Stamp([heading, evidence ?? "", detail, .. links.Select(l => l.EntryId.ToString(System.Globalization.CultureInfo.InvariantCulture))]),
            title, chip, heading, path, evidence, detail, [.. choices.Select(c => new ReviewChoice(c.Key, c.Label, c.Hint, c.Primary))], dismiss, book, links,
            tiles ?? [], [], 0, [], "", fix);
        return new Offer(card, item);
    }

    static string Stamp(IEnumerable<string> parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..16];

    /// <summary>"SessionItem:42": a session item or page note, by what it belongs to.</summary>
    static async Task<(Func<Task>? Undo, string Message)> RemoveAsync(PartsService parts, UnplacedPart part, CancellationToken ct)
    {
        // An adventure already gone needs nothing more, and there's nothing to bring back.
        var removed = await parts.RemoveAsync(part.EntryId, ct);
        return (removed is null ? null : async () => await parts.RestoreAsync(removed, CancellationToken.None), new UnplacedPartCardText(part).Removed);
    }

    static string KeyOf(RevisionPlace place) => $"{place.Place.Kind}:{place.Place.OwnerId}";

    static async Task<(Func<Task>? Undo, string Message)> VersionAsync(ReviewService reviews, VersionItem item, string choice, CancellationToken ct)
    {
        var text = new VersionCardText(item);
        var (answer, message) = choice switch
        {
            "current" => (VersionAnswer.MakeCurrent, text.MadeCurrent),
            "copy" => (VersionAnswer.KeepAsCopy, text.KeptAsCopy),
            _ => (VersionAnswer.SeparateBook, text.KeptSeparate),
        };
        // A card that isn't waiting any more (its files joined meanwhile) is done, with nothing to undo.
        var decision = await reviews.AnswerVersionAsync(item.Version, answer, ct);
        return (decision is null ? null : () => reviews.UndoVersionAsync(decision, CancellationToken.None), message);
    }

    static CollectionAnswer Answer(string choice) => choice switch
    {
        "accept" => CollectionAnswer.Accept,
        "notnow" => CollectionAnswer.NotNow,
        _ => CollectionAnswer.Never,
    };

    static string Said(FolderCollectionCardText text, string choice) => choice switch
    {
        "accept" => text.Accepted,
        "notnow" => text.NotNow,
        _ => text.Never,
    };

    static string Said(CollectionSuggestionCardText text, string choice) => choice switch
    {
        "accept" => text.Accepted,
        "notnow" => text.NotNow,
        _ => text.Never,
    };
}
