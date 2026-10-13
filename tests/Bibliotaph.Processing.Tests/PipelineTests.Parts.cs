using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Dapper;

namespace Bibliotaph.Processing.Tests;

/// <summary>Adventures inside a file, marked by hand, as cards of their own that follow the file's copies.</summary>
public sealed partial class PipelineTests
{
    static SearchPlan Plan(string query) => SearchPlan.From(SearchQuery.Parse(query));

    [Fact]
    public async Task An_adventure_marked_by_hand_is_a_card_of_its_own_that_survives_reprocessing_and_a_rebuild()
    {
        Copy(pdfs.WatermarkedForAna, "Purchases/Drowned Abbey.pdf");
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var book = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Null(await _metadata.SetAsync(book.EntryId, MetadataFields.System, "D&D", Ct));

        // The bell tower and the crypt become an adventure; pages it shares with it, or no title, can't.
        var marked = await _parts.AddAsync(book.DocumentId, new PartPages(2, 3), "The Bell Tower", Ct);
        var part = marked.EntryId!.Value;
        Assert.Null(marked.Problem);
        Assert.Contains("overlap “The Bell Tower”", (await _parts.AddAsync(book.DocumentId, new PartPages(3, 4), "Crypt", Ct)).Problem, StringComparison.Ordinal);
        Assert.Equal("Give the adventure a title.", (await _parts.AddAsync(book.DocumentId, new PartPages(4, 4), " ", Ct)).Problem);
        Assert.Equal("Those pages aren't all in this file.", (await _parts.AddAsync(book.DocumentId, new PartPages(4, 9), "Beyond", Ct)).Problem);

        async Task<Dictionary<EntryId, LibraryEntry>> CardsAsync() => (await search.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(c => c.EntryId);
        async Task CheckCardAsync()
        {
            var cards = await CardsAsync();
            var card = cards[part];
            Assert.Equal((book.DocumentId, "The Bell Tower", 2, "Adventure", "D&D"), (card.DocumentId, card.Title, card.PageCount, card.Kind, card.System));
            Assert.Equal("in Drowned Abbey · pp. 3–4", card.PartLine);
            Assert.Equal($"{book.Cover![..^4]}-p2.jpg", card.Cover);
            Assert.True(File.Exists(Path.Combine(_paths.Cache, "covers", card.Cover!)));
            Assert.Equal((1, "Anthology"), (cards[book.EntryId].Parts, cards[book.EntryId].Kind));
            // type:adventure finds the adventure, not its file, and a page in it is found under it.
            Assert.Equal([part], (await search.ListAsync(new LibraryFilter(), Plan("type:adventure"), ct: Ct)).Select(e => e.EntryId));
            var eels = Assert.Single((await search.SearchPagesAsync(Plan("eels"), new LibraryFilter(), ct: Ct)).Entries);
            Assert.Equal((part, 2), (eels.Entry.EntryId, Assert.Single(eels.Pages).PdfPage));
            var bells = Assert.Single((await search.SearchPagesAsync(Plan("bells"), new LibraryFilter(), ct: Ct)).Entries);
            Assert.Equal((book.EntryId, 1), (bells.Entry.EntryId, Assert.Single(bells.Pages).PdfPage));
        }
        await CheckCardAsync();

        // Reprocessing the file draws its cover again and keeps the adventure as it was.
        Assert.True(await _service.ReprocessAsync(book.DocumentId, ct: Ct));
        await SettleAsync();
        await CheckCardAsync();

        // index.db loses its cards and covers, as a rebuild would: the projection and the Covers stage bring them back.
        await _writer.WriteAsync((c, t) => c.Execute(
            "DELETE FROM entry_doc; DELETE FROM entry_meta; DELETE FROM entry_facet; DELETE FROM entry_fts; DELETE FROM page_cover;", transaction: t), Ct);
        await _projector.ProjectAllAsync(Ct);
        await _service.RerunAsync(Stage.Covers, Ct);
        await SettleAsync();
        await CheckCardAsync();

        // Edit range moves it; Undo puts it back. Remove adventure takes its card away and the file is whole again.
        var moved = await _parts.SetPagesAsync(part, book.DocumentId, new PartPages(2, 4), Ct);
        Assert.Equal(3, (await CardsAsync())[part].PageCount);
        await moved.Undo!();
        Assert.Equal(2, (await CardsAsync())[part].PageCount);
        var removed = await _parts.RemoveAsync(part, Ct);
        var after = await CardsAsync();
        Assert.False(after.ContainsKey(part));
        Assert.Equal(0, after[book.EntryId].Parts);
        Assert.Null(after[book.EntryId].Kind);
        var back = await _parts.RestoreAsync(removed!, Ct);
        Assert.Equal("The Bell Tower", (await CardsAsync())[back!.Value].Title);
    }

    [Fact]
    public async Task An_adventure_follows_its_pages_into_a_copy_that_joins_and_a_new_version()
    {
        var path = Path.Combine(_library, "Purchases", "Drowned Abbey.pdf");
        Copy(pdfs.WatermarkedForAna, Path.Combine("Purchases", "Drowned Abbey.pdf"));
        await _roots.AddAsync(_library, Ct);
        await _service.StartAsync(Ct);
        await SettleAsync();
        var search = new LibraryQueries(_index);
        var book = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
        var tower = (await _parts.AddAsync(book.DocumentId, new PartPages(1, 2), "Background and tower", Ct)).EntryId!.Value;
        var crypt = (await _parts.AddAsync(book.DocumentId, new PartPages(3, 3), "The Crypt", Ct)).EntryId!.Value;

        // A copy sold to another buyer joins the book; both adventures are found at the same pages in it.
        Copy(pdfs.WatermarkedForDale, "Backup/Drowned Abbey (1).pdf");
        _service.RequestScan();
        await SettleUntilAsync(async () => (await search.ListAsync(new LibraryFilter(HideParts: true), ct: Ct)) is [{ Copies: 2 }]);
        await SettleAsync();
        var dale = (await _copies.GetAsync(book.EntryId, Ct)).Single(c => !c.Copy.IsCurrent).Copy.DocumentId;
        Assert.Equal(new PartPages(1, 2), (await _partStore.GetPartAsync(tower, Ct))!.On(dale)!.Pages);
        Assert.Equal(new PartPages(3, 3), (await _partStore.GetPartAsync(crypt, Ct))!.On(dale)!.Pages);
        Assert.NotNull((await _partStore.GetPartAsync(crypt, Ct))!.On(dale)!.FirstFingerprint);

        // Opening the other copy opens its pages.
        await _copies.MakeCurrentAsync(book.EntryId, dale, Ct);
        Assert.Equal(dale, (await search.GetEntriesAsync([tower], Ct)).Single().DocumentId);
        await _copies.MakeCurrentAsync(book.EntryId, book.DocumentId, Ct);
        Assert.Equal(book.DocumentId, (await search.GetEntriesAsync([tower], Ct)).Single().DocumentId);
        Assert.Empty(await _parts.GetUnplacedAsync(Ct));

        // A revised edition saved over the first file adds a cloister after the background and corrects the crypt. The
        // first adventure follows its first and last pages; the crypt's page isn't there as it was, so it needs a look.
        File.Copy(pdfs.ExpandedForAna, path, overwrite: true);
        _service.RequestScan();
        long revised = 0;
        await SettleUntilAsync(async () => await _entries.GetCurrentDocumentAsync(book.EntryId, Ct) is { } current && (revised = current) != book.DocumentId
            && await _queries.GetStageStatusAsync(current, Stage.Match, Ct) == StageStatus.Complete);
        await SettleAsync();

        var card = (await search.GetEntriesAsync([tower], Ct)).Single();
        Assert.Equal((revised, new PartPages(1, 3)), (card.DocumentId, card.Pages!.Value));
        var unplaced = Assert.Single(await _parts.GetUnplacedAsync(Ct));
        Assert.Equal((crypt, book.EntryId, "The Crypt", revised, new PartPages(3, 3)),
            (unplaced.EntryId, unplaced.FileEntryId, unplaced.Title, unplaced.DocumentId, unplaced.WasPages!.Value));
        var review = new ReviewService(_metadataStore, _vocabulary, _metadata, _projector, _queries, _settings,
            new VocabularyService(_vocabulary, _projector, _service, _hints), _copies, _packs, places: _places, parts: _parts);
        Assert.Equal([crypt], (await review.GetQueueAsync(Ct)).Parts!.Select(p => p.EntryId));
        var count = await review.CountAsync(Ct);

        // Set its pages: the corrected crypt is the fifth page now.
        Assert.Null((await _parts.SetPagesAsync(crypt, revised, new PartPages(4, 4), Ct)).Problem);
        Assert.Empty(await _parts.GetUnplacedAsync(Ct));
        Assert.Equal(count - 1, await review.CountAsync(Ct));
        Assert.Equal((revised, new PartPages(4, 4)), ((await search.GetEntriesAsync([crypt], Ct)).Single().DocumentId, (await search.GetEntriesAsync([crypt], Ct)).Single().Pages!.Value));
    }
}
