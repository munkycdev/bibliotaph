using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>A map beside its book goes with it, takes its values, and can be kept inside it or left out of the Library.</summary>
    public sealed class SourceBooksTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_map_beside_its_book_goes_with_it_and_takes_its_game()
        {
            Copy(_pdfs.WatermarkedForAna, "Harbor/Drowned Abbey.pdf");
            Directory.CreateDirectory(Path.Combine(_library, "Harbor"));
            File.WriteAllBytes(Path.Combine(_library, "Harbor", "Lower Docks.png"), FakeCodec.Png(640, 480));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();
            var search = new LibraryQueries(_index);
            var cards = (await search.ListAsync(new LibraryFilter(Sort: LibrarySort.Title), ct: Ct)).ToDictionary(c => c.Title);
            var book = cards["Drowned Abbey"];
            var map = cards["Lower Docks"];
            Assert.Null(await _metadata.SetAsync(book.EntryId, MetadataFields.System, "D&D", Ct));

            var store = new CompanionStore(new Factory(_catalog));
            var finder = new SourceBookFinder(store, _libraryStore, search, _queries, _projector, _metadataStore);
            Assert.True(await finder.RunAsync(ct: Ct));

            var link = await store.GetLiveAsync(map.EntryId, Ct);
            Assert.Equal((book.EntryId, CompanionState.Suggested, SourceBookSignal.SameFolder), (link!.Book, link.State, link.Evidence!.Signal));
            cards = (await search.ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(c => c.Title);
            Assert.Equal(("D&D", "goes with Drowned Abbey"), (cards["Lower Docks"].System, cards["Lower Docks"].SourceLine));
            Assert.Equal(1, cards["Drowned Abbey"].Companions);

            // Kept inside its book, or left out altogether.
            Assert.Equal(["Drowned Abbey"], (await search.ListAsync(new LibraryFilter(CompanionsInBooks: true), ct: Ct)).Select(c => c.Title));
            Assert.Equal(["Drowned Abbey"], (await search.ListAsync(new LibraryFilter(HideCompanions: true), ct: Ct)).Select(c => c.Title));

            // A second pass finds nothing new.
            Assert.False(await finder.RunAsync(ct: Ct));
        }
    }
}
