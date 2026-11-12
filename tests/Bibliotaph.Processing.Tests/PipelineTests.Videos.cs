using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;
using Dapper;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>Videos (animated maps): listed and found like images, packed with them, with a cover from a still beside them.</summary>
    public sealed class VideosTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_video_is_listed_with_its_size_and_a_cover_from_the_still_beside_it()
        {
            var harbor = Path.Combine(_library, "Maps", "Harbor");
            Directory.CreateDirectory(harbor);
            File.WriteAllBytes(Path.Combine(harbor, "Harbor_Night.jpg"), FakeCodec.Png(1920, 1080));
            File.WriteAllBytes(Path.Combine(harbor, "Harbor_Night_Animated.webm"), FakeWebm.Bytes(1920, 1080));
            File.WriteAllBytes(Path.Combine(_library, "Storm.webm"), FakeWebm.Bytes(1280, 720));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var cards = await search.ListAsync(new LibraryFilter(Sort: LibrarySort.Title), ct: Ct);
            var video = cards.Single(c => c.Title == "Harbor Night Animated");
            Assert.Equal((SourceFormats.Webm, "WEBM video"), (video.Format, CardText.Describe(video)));
            Assert.NotNull(video.Cover);
            // Nothing beside it to draw a cover from: it shows the placeholder.
            var storm = cards.Single(c => c.Title == "Storm");
            Assert.Null(storm.Cover);

            await using var c = _index.OpenRead();
            Assert.Equal((1920L, 1080L), await c.QuerySingleAsync<(long, long)>("SELECT width_px, height_px FROM doc WHERE document_id = @id", new { id = video.DocumentId }));
            Assert.Equal("Skipped", await c.QuerySingleAsync<string>("SELECT status FROM stage_status WHERE stage = 'Text' AND document_id = @id", new { id = video.DocumentId }));
        }

        [Fact]
        public async Task Format_video_finds_videos_and_format_image_and_the_Images_kind_take_them_with_the_images()
        {
            Directory.CreateDirectory(_library);
            File.WriteAllBytes(Path.Combine(_library, "Harbor.png"), FakeCodec.Png(640, 480));
            File.WriteAllBytes(Path.Combine(_library, "Harbor Animated.webm"), FakeWebm.Bytes(640, 480));
            Copy(_pdfs.KnownText, "Known Text.pdf");
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            async Task<string[]> Found(string query) =>
                [.. (await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse(query)), new LibraryFilter(Sort: LibrarySort.Title), ct: Ct)).Select(e => e.Title).Order()];
            Assert.Equal(["Harbor Animated"], await Found("format:video"));
            Assert.Equal(["Harbor Animated"], await Found("format:webm"));
            Assert.Equal(["Harbor", "Harbor Animated"], await Found("format:image"));
            var pictures = await search.ListAsync(new LibraryFilter(Kind: KindFilter.Images, Sort: LibrarySort.Title), ct: Ct);
            Assert.Equal(["Harbor", "Harbor Animated"], pictures.Select(p => p.Title));
        }

        [Fact]
        public async Task A_folder_of_maps_and_their_animated_versions_is_one_pack()
        {
            var maps = Path.Combine(_library, "Maps", "Aetherstar");
            Directory.CreateDirectory(maps);
            for (var i = 1; i <= PackStore.AutomaticMinimum - 2; i++)
                File.WriteAllBytes(Path.Combine(maps, $"Aetherstar {i}.jpg"), FakeCodec.Png(100 + i, 100));
            File.WriteAllBytes(Path.Combine(maps, "Aetherstar 1 Animated.webm"), FakeWebm.Bytes(1920, 1080));
            File.WriteAllBytes(Path.Combine(maps, "Aetherstar 2 Animated.webm"), FakeWebm.Bytes(1280, 720));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var pack = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
            Assert.Equal((true, PackStore.AutomaticMinimum), (pack.IsPack, pack.Members));
            var files = await search.GetPackImagesAsync(pack.EntryId, Ct);
            Assert.Contains(files, f => f.Name == "Aetherstar 2 Animated.webm");
        }
    }
}
