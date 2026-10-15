using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>A compilation's adventures proposed from its bookmarks and blurbs, settled in Needs review.</summary>
    public sealed class ProposalsTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_compilations_bookmarks_and_blurbs_propose_its_adventures_which_Accept_makes_cards_of()
        {
            Copy(_pdfs.Compilation, "Compilations/Marsh Tales.pdf");
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
            // The credits say it is a collection, which blurbs alone never would.
            Assert.Equal(0, proposal.Evidence!.PdfPage);
            Assert.EndsWith("Two short adventures, each of which can be run on its own.", proposal.Evidence.Quote, StringComparison.Ordinal);
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
            Assert.Equal(("The Salt Mine", "in Marsh Tales · pp. 2–3", "Level 1", "4 players"), (mine.Title, mine.PartLine, mine.Levels, mine.Players));
            Assert.Equal(("Lantern Festival", null), (cards[accepted.Parts[1]].Title, cards[accepted.Parts[1]].Players));
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

        [Fact]
        public async Task An_adventure_marked_by_hand_is_suggested_the_levels_and_party_size_its_blurb_gives()
        {
            Copy(_pdfs.Compilation, "Compilations/Marsh Tales.pdf");
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();
            var search = new LibraryQueries(_index);
            var book = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));

            var marked = await _parts.AddAsync(book.DocumentId, new PartPages(1, 2), "The Salt Mine", Ct);

            var fields = (await _metadataStore.GetAsync(marked.EntryId!.Value, Ct)).Compute();
            var players = fields[MetadataFields.Players].First!;
            Assert.Equal(("4", AssertionOrigin.Rule, false, "for four characters of 1st level"), (players.Value, players.Origin, players.Confirmed, players.Source.Quote));
            Assert.Equal([1], players.Source.Pages);
            Assert.Equal("1", fields[MetadataFields.Levels].First!.Value);
            var card = Assert.Single(await search.ListAsync(new LibraryFilter(), Plan("players:4"), ct: Ct));
            Assert.Equal((marked.EntryId, "Level 1", "4 players"), (card.EntryId, card.Levels, card.Players));

            // Pages with no blurb suggest nothing.
            var quiet = await _parts.AddAsync(book.DocumentId, new PartPages(5, 5), "New Monsters", Ct);
            Assert.Null(quiet.Problem);
            Assert.Empty((await _metadataStore.GetAsync(quiet.EntryId!.Value, Ct)).Compute()[MetadataFields.Players].Values);
        }
    }
}
