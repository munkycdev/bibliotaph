using Bibliotaph.Catalog;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Image packs (F4a): a folder of many images is one card once it has been read, and splits back into images.</summary>
public sealed partial class PipelineTests
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

        Assert.True(await _packs.SplitAsync(pack.EntryId, Ct));

        cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        Assert.Equal(21, cards.Count);
        Assert.DoesNotContain(cards, c => c.IsPack);
        Assert.Equal(20, cards.Count(c => c.FolderHint == "Tokens / Undead"));
    }
}
