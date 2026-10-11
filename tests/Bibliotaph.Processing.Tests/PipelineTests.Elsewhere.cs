using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Owned elsewhere (F5a): a book with no file, and its file arriving later.</summary>
public sealed partial class PipelineTests
{
    [Fact]
    public async Task A_book_owned_elsewhere_meets_its_file_in_needs_review_and_same_book_keeps_its_work()
    {
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        var search = new LibraryQueries(_index);
        var review = new ReviewService(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
            new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs, _elsewhere);

        // A title is required, and nothing is made without one.
        Assert.Equal(MetadataFields.Title, (await _elsewhere.AddAsync(new ElsewhereDraft(" "), Ct)).Problem?.Field);
        var (added, problem) = await _elsewhere.AddAsync(
            new ElsewhereDraft("Drowned Abbey", System: "5e", Types: ["Adventure"], Publisher: "Kobold Works", AlsoOwn: ["Print", "Foundry"]), Ct);
        Assert.Null(problem);
        var printed = Assert.IsType<EntryId>(added);

        var card = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal((printed, true, "Drowned Abbey", "D&D 5e", "Foundry VTT · Print"), (card.EntryId, card.IsElsewhere, card.Title, card.System, card.AlsoOwn));
        Assert.Equal([printed], (await search.ListAsync(new LibraryFilter(), SearchPlan.From(SearchQuery.Parse("own:foundry")), ct: Ct)).Select(e => e.EntryId));

        // Its PDF arrives: Match offers it as the book's file.
        Copy(pdfs.WatermarkedForAna, "Purchases/Drowned Abbey.pdf");
        _service.RequestScan();
        await SettleUntilAsync(async () => await _elsewhere.CountPendingAsync(Ct) == 1);
        var item = Assert.Single((await review.GetQueueAsync(Ct)).Elsewhere!);
        Assert.Equal(("Drowned Abbey", "Foundry VTT · Print"), (item.BookTitle, item.AlsoOwn));
        Assert.EndsWith("Drowned Abbey.pdf", item.Path, StringComparison.Ordinal);
        Assert.Equal(2, (await search.ListAsync(new LibraryFilter(), ct: Ct)).Count);

        // Same book: one card, the book's, opening the file and still owned in print and on Foundry.
        var undo = await review.AnswerElsewhereAsync(item.Match, ElsewhereAnswer.SameBook, Ct);
        Assert.NotNull(undo);
        var joined = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal((printed, item.Match.DocumentId, false, "Foundry VTT · Print"), (joined.EntryId, joined.DocumentId, joined.IsElsewhere, joined.AlsoOwn));
        Assert.Equal(0, await _elsewhere.CountPendingAsync(Ct));

        // Undo parts them again, and the card waits again.
        await undo();
        var cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        Assert.Equal(2, cards.Count);
        Assert.True(cards.Single(c => c.EntryId == printed).IsElsewhere);
        Assert.Equal(1, await _elsewhere.CountPendingAsync(Ct));

        // Remove from library, then Undo.
        await review.AnswerElsewhereAsync(item.Match, ElsewhereAnswer.SeparateBook, Ct);
        var removed = await _elsewhere.RemoveAsync(printed, Ct);
        Assert.NotNull(removed);
        Assert.False(Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).IsElsewhere);
        var back = await _elsewhere.UndoRemoveAsync(removed, Ct);
        Assert.Equal("Drowned Abbey", (await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Single(c => c.EntryId == back).Title);
    }

    [Fact]
    public async Task Files_already_in_the_library_are_offered_to_a_book_added_elsewhere_and_after_a_title_or_publisher_changes()
    {
        Copy(pdfs.WatermarkedForAna, "Purchases/Ember Vault.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var file = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal("Ember Vault", file.Title);
        Assert.Null(await _metadata.SetAsync(file.EntryId, MetadataFields.Publisher, "Gilded Press", Ct));

        // A publisher that says otherwise rules the file out; a title in other letters, with no publisher, doesn't.
        var (other, _) = await _elsewhere.AddAsync(new ElsewhereDraft("Ember Vault", Publisher: "Kobold Works"), Ct);
        Assert.Equal(0, await _elsewhere.CountPendingAsync(Ct));
        var (printed, _) = await _elsewhere.AddAsync(new ElsewhereDraft("ember vault", AlsoOwn: ["Print"]), Ct);
        var match = Assert.Single(await _elsewhere.GetPendingAsync(Ct)).Match;
        Assert.Equal((printed, file.DocumentId, file.EntryId), (match.EntryId, match.DocumentId, match.FileEntryId));
        // A pair is asked about once.
        Assert.Equal(0, await _elsewhere.CheckFilesAsync([printed!.Value, file.EntryId], Ct));

        // Changes are checked as they are projected: the other book's publisher now agrees, then a file is renamed.
        _elsewhere.CheckFilesWhenProjected();
        var (glass, _) = await _elsewhere.AddAsync(new ElsewhereDraft("Glass Tower"), Ct);
        Assert.Null(await _metadata.SetAsync(other!.Value, MetadataFields.Publisher, "Gilded Press", Ct));
        await SettleUntilAsync(async () => await _elsewhere.CountPendingAsync(Ct) == 2);
        Assert.Null(await _metadata.SetAsync(file.EntryId, MetadataFields.Title, "Glass Tower", Ct));
        await SettleUntilAsync(async () => await _elsewhere.CountPendingAsync(Ct) == 3);
        Assert.Equal([printed.Value, other.Value, glass!.Value], (await _elsewhere.GetPendingAsync(Ct)).Select(p => p.Match.EntryId));
    }
}
