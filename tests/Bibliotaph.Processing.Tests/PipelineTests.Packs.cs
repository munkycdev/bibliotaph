using Bibliotaph.Catalog;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>Image packs: a folder of many images is one card once it has been read, and splits back into images.</summary>
    public sealed class PacksTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_folder_of_tokens_becomes_one_pack_card_and_splitting_gives_the_tokens_back()
        {
            var tokens = Path.Combine(_library, "Tokens", "Undead");
            Directory.CreateDirectory(tokens);
            for (var i = 1; i <= PackStore.AutomaticMinimum; i++)
                File.WriteAllBytes(Path.Combine(tokens, $"Zombie {i}.png"), FakeCodec.Png(100 + i, 100));
            File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var cards = await search.ListAsync(new LibraryFilter(Sort: LibrarySort.Title), ct: Ct);
            Assert.Equal(["Handout Map", "Undead"], cards.Select(c => c.Title));
            var pack = cards[1];
            Assert.Equal((true, 20, 4), (pack.IsPack, pack.Members, pack.MosaicCovers.Count));
            var images = await search.GetPackImagesAsync(pack.EntryId, Ct);
            Assert.Equal(["Zombie 1.png", "Zombie 2.png"], images.Take(2).Select(i => i.Name));
            Assert.Equal(pack.DocumentId, images[0].DocumentId);

            Assert.NotNull(await _packs.SplitAsync(pack.EntryId, Ct));

            cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
            Assert.Equal(21, cards.Count);
            Assert.DoesNotContain(cards, c => c.IsPack);
            Assert.Equal(20, cards.Count(c => c.FolderHint == "Tokens / Undead"));
        }

        [Fact]
        public async Task An_image_chosen_as_a_packs_cover_replaces_the_mosaic_until_it_leaves_the_pack()
        {
            var maps = Path.Combine(_library, "Maps", "Harbor");
            Directory.CreateDirectory(maps);
            for (var i = 1; i <= PackStore.AutomaticMinimum + 1; i++)
                File.WriteAllBytes(Path.Combine(maps, $"Map {i}.png"), FakeCodec.Png(100 + i, 100));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var pack = Assert.Single(await search.ListAsync(new LibraryFilter(Kind: KindFilter.Packs), ct: Ct));
            var images = await search.GetPackImagesAsync(pack.EntryId, Ct);
            var chosen = images[4];
            Assert.Null(pack.CoverMember);

            Assert.True(await _packs.UseCoverAsync(pack.EntryId, chosen.MemberEntryId, Ct));
            pack = Assert.Single(await search.GetEntriesAsync([pack.EntryId], Ct));
            Assert.Equal((chosen.MemberEntryId, chosen.Cover), (pack.CoverMember, pack.Cover));
            Assert.Empty(pack.MosaicCovers);

            // Reading the library again keeps the choice: it is the user's, in catalog.db.
            await _projector.ProjectAllAsync(Ct);
            pack = Assert.Single(await search.GetEntriesAsync([pack.EntryId], Ct));
            Assert.Equal(chosen.MemberEntryId, pack.CoverMember);

            // The chosen image goes: once the pack's card is next written, as at the next start, it shows the mosaic again.
            File.Delete(Path.Combine(maps, "Map 5.png"));
            _service.RequestScan();
            await SettleUntilAsync(async () => (await _libraryStore.GetCountsAsync(Ct)).Missing == 1);
            await _projector.ProjectAllAsync(Ct);
            pack = Assert.Single(await search.GetEntriesAsync([pack.EntryId], Ct));
            Assert.Null(pack.CoverMember);
            Assert.Equal(4, pack.MosaicCovers.Count);
            Assert.Equal(images[0].Cover, pack.Cover);
        }

        [Fact]
        public async Task Use_the_mosaic_takes_the_chosen_cover_away_and_a_file_outside_the_pack_cant_be_its_cover()
        {
            var tokens = Path.Combine(_library, "Tokens");
            Directory.CreateDirectory(tokens);
            for (var i = 1; i <= PackStore.AutomaticMinimum; i++)
                File.WriteAllBytes(Path.Combine(tokens, $"Goblin {i}.png"), FakeCodec.Png(100 + i, 100));
            File.WriteAllBytes(Path.Combine(_library, "Handout.png"), FakeCodec.Png(640, 480));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var cards = await search.ListAsync(new LibraryFilter(Sort: LibrarySort.Title), ct: Ct);
            var (handout, pack) = (cards.Single(c => !c.IsPack), cards.Single(c => c.IsPack));
            Assert.False(await _packs.UseCoverAsync(pack.EntryId, handout.EntryId, Ct));
            Assert.False(await _packs.UseCoverAsync(handout.EntryId, null, Ct));

            var images = await search.GetPackImagesAsync(pack.EntryId, Ct);
            Assert.True(await _packs.UseCoverAsync(pack.EntryId, images[^1].MemberEntryId, Ct));
            Assert.True(await _packs.UseCoverAsync(pack.EntryId, null, Ct));
            pack = Assert.Single(await search.GetEntriesAsync([pack.EntryId], Ct));
            Assert.Null(pack.CoverMember);
            Assert.Equal(4, pack.MosaicCovers.Count);
        }

        [Fact]
        public async Task A_pack_takes_hints_from_its_folder_names_and_is_found_by_an_image_name()
        {
            var sea = Path.Combine(_library, "D&D 5e", "Sea");
            Directory.CreateDirectory(sea);
            for (var i = 1; i <= PackStore.AutomaticMinimum; i++)
                File.WriteAllBytes(Path.Combine(sea, i == 7 ? "Kraken.png" : $"Shark {i}.png"), FakeCodec.Png(100 + i, 100));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var pack = Assert.Single(await search.ListAsync(new LibraryFilter(Kind: KindFilter.Packs), ct: Ct));
            Assert.Equal(("Sea", "D&D 5e"), (pack.Title, pack.System));
            var hit = Assert.Single(await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("kraken")), new LibraryFilter(), ct: Ct));
            Assert.Equal((pack.EntryId, "Kraken.png"), (hit.EntryId, hit.MatchedMember));
        }
    }
}
