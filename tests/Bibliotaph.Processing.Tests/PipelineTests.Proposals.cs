using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Slice 5b: a compilation's adventures proposed from its bookmarks and blurbs, settled in Needs review.</summary>
public sealed partial class PipelineTests
{
    ReviewService Review() => new(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
        new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs, places: _places, parts: _parts, proposals: _proposals);

    [Fact]
    public async Task A_compilations_bookmarks_and_blurbs_propose_its_adventures_which_Accept_makes_cards_of()
    {
        Copy(pdfs.Compilation, "Compilations/Marsh Tales.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var book = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));

        // One card for the file, nothing made yet; credits and the appendix aren't adventures.
        var review = Review();
        var proposal = Assert.Single((await review.GetQueueAsync(Ct)).Proposals!);
        Assert.Equal((book.EntryId, book.DocumentId, PartProposalSource.Rules), (proposal.FileEntryId, proposal.DocumentId, proposal.Source));
        Assert.Equal([("The Salt Mine", new PartPages(1, 2), "1"), ("Lantern Night", new PartPages(3, 4), "3-5")],
            proposal.Parts.Select(p => (p.Title, p.Pages, p.Levels)));
        Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        var count = await review.CountAsync(Ct);

        // Not split, then Undo: the card is back.
        var undoNotSplit = await _proposals.NotSplitAsync(proposal, Ct);
        Assert.Empty((await review.GetQueueAsync(Ct)).Proposals!);
        Assert.Equal(count - 1, await review.CountAsync(Ct));
        await undoNotSplit();
        Assert.Single((await review.GetQueueAsync(Ct)).Proposals!);

        // Accept with the second retitled: two cards, the levels from each blurb suggested, the file an anthology.
        var accepted = await _proposals.AcceptAsync(proposal, [proposal.Parts[0], proposal.Parts[1] with { Title = "Lantern Festival" }], Ct);
        Assert.Null(accepted.Problem);
        var cards = (await search.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(c => c.EntryId);
        Assert.Equal(3, cards.Count);
        var mine = cards[accepted.Parts[0]];
        Assert.Equal(("The Salt Mine", "in Marsh Tales · pp. 2–3", "Level 1"), (mine.Title, mine.PartLine, mine.Levels));
        Assert.Equal("Lantern Festival", cards[accepted.Parts[1]].Title);
        var levels = (await _metadataStore.GetAsync(accepted.Parts[1], Ct)).Compute()[MetadataFields.Levels].First!;
        Assert.Equal(("3-5", AssertionOrigin.Rule, false, "for characters of 3rd to 5th level"), (levels.Value, levels.Origin, levels.Confirmed, levels.Source.Quote));
        Assert.Equal((2, "Anthology"), (cards[book.EntryId].Parts, cards[book.EntryId].Kind));
        Assert.Empty((await review.GetQueueAsync(Ct)).Proposals!);
        Assert.Equal([accepted.Parts[1]], (await search.ListAsync(new LibraryFilter(), Plan("level:4"), ct: Ct)).Select(c => c.EntryId));

        // Reading the file again remembers the answer.
        Assert.True(await _service.ReprocessAsync(book.DocumentId, ct: Ct));
        await SettleAsync();
        Assert.Empty((await review.GetQueueAsync(Ct)).Proposals!);

        // Undo takes the cards away and asks again.
        await accepted.Undo!();
        Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Single((await review.GetQueueAsync(Ct)).Proposals!);

        // Pages that overlap one already marked by hand take nothing, and say which.
        Assert.Null((await _parts.AddAsync(book.DocumentId, new PartPages(2, 3), "Hand marked", Ct)).Problem);
        Assert.Empty((await review.GetQueueAsync(Ct)).Proposals!);
        var clash = await _proposals.AcceptAsync(proposal, proposal.Parts, Ct);
        Assert.Equal([], clash.Parts);
        Assert.Contains("The Salt Mine: Those pages overlap “Hand marked”", clash.Problem, StringComparison.Ordinal);
        Assert.Equal(2, (await search.ListAsync(new LibraryFilter(), ct: Ct)).Count);
    }
}
