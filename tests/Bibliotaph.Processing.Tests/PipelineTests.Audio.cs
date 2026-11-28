using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;
using Dapper;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>Audio: tracks listed by their tags and length, albums from a folder of them, found by format and kind.</summary>
    public sealed class AudioTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_track_is_listed_with_its_length_and_a_cover_from_its_artwork_and_has_no_text()
        {
            Directory.CreateDirectory(_library);
            File.WriteAllBytes(Path.Combine(_library, "Rainy Tavern.mp3"), FakeAudio.Mp3("Rainy Tavern", "A Composer", artwork: FakeCodec.Png(300, 300), frames: 1000));
            File.WriteAllBytes(Path.Combine(_library, "Door Creak.wav"), FakeAudio.Wav(2));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var cards = await search.ListAsync(new LibraryFilter(Sort: LibrarySort.Title), ct: Ct);
            var tavern = cards.Single(c => c.Title == "Rainy Tavern");
            Assert.Equal(SourceFormats.Mp3, tavern.Format);
            Assert.StartsWith("MP3 · 0:2", CardText.Describe(tavern));
            Assert.NotNull(tavern.Cover);
            // Nothing in it or beside it to draw a cover from.
            Assert.Null(cards.Single(c => c.Title == "Door Creak").Cover);

            await using var c = _index.OpenRead();
            Assert.InRange(await c.QuerySingleAsync<long>("SELECT duration_ms FROM doc WHERE document_id = @id", new { id = tavern.DocumentId }), 25_000, 27_500);
            Assert.Equal("Skipped", await c.QuerySingleAsync<string>("SELECT status FROM stage_status WHERE stage = 'Text' AND document_id = @id", new { id = tavern.DocumentId }));

            var track = (await search.GetTracksAsync([tavern.DocumentId], Ct))[tavern.DocumentId];
            Assert.Equal(("A Composer", SourceFormats.Mp3), (track.Artist, track.Format));
            Assert.NotNull(track.Duration);
        }

        [Fact]
        public async Task A_folder_of_tracks_is_an_album_with_the_cover_beside_them()
        {
            var album = Path.Combine(_library, "Music", "Tavern Sounds");
            Directory.CreateDirectory(album);
            for (var i = 1; i <= 3; i++)
                File.WriteAllBytes(Path.Combine(album, $"{i:00} Track {i}.mp3"), FakeAudio.Mp3($"Track {i}", "A Composer", "Tavern Sounds", $"{i}/3", frames: 100 + i));
            File.WriteAllBytes(Path.Combine(album, "cover.png"), FakeCodec.Png(500, 500));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            var pack = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
            Assert.True(pack.IsPack);
            Assert.True(pack.IsAlbum, $"{pack.AudioMembers} of {pack.Members} members are tracks");
            Assert.Equal("Album · 3 tracks", CardText.Describe(pack));
            Assert.NotNull(pack.Cover);
        }

        [Fact]
        public async Task Maps_with_a_loop_or_two_beside_them_are_asked_about_not_made_an_album()
        {
            var maps = Path.Combine(_library, "Maps", "Harbor");
            Directory.CreateDirectory(maps);
            for (var i = 1; i <= 5; i++) File.WriteAllBytes(Path.Combine(maps, $"Harbor {i}.png"), FakeCodec.Png(100 + i, 100));
            File.WriteAllBytes(Path.Combine(maps, "Harbor Waves.mp3"), FakeAudio.Mp3("Harbor Waves"));
            File.WriteAllBytes(Path.Combine(maps, "Harbor Gulls.mp3"), FakeAudio.Mp3("Harbor Gulls"));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var cards = await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct);
            Assert.Equal(7, cards.Count);
            Assert.DoesNotContain(cards, c => c.IsPack);
        }

        [Fact]
        public async Task Format_audio_and_the_Audio_kind_find_tracks_and_nothing_else()
        {
            Directory.CreateDirectory(_library);
            File.WriteAllBytes(Path.Combine(_library, "Battle Theme.mp3"), FakeAudio.Mp3("Battle Theme"));
            File.WriteAllBytes(Path.Combine(_library, "Wind.wav"), FakeAudio.Wav(1));
            File.WriteAllBytes(Path.Combine(_library, "Harbor.png"), FakeCodec.Png(640, 480));
            Copy(_pdfs.KnownText, "Known Text.pdf");
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            var search = new LibraryQueries(_index);
            async Task<string[]> Found(string query) =>
                [.. (await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse(query)), new LibraryFilter(Sort: LibrarySort.Title), ct: Ct)).Select(e => e.Title).Order()];
            Assert.Equal(["Battle Theme", "Wind"], await Found("format:audio"));
            Assert.Equal(["Battle Theme"], await Found("format:mp3"));
            Assert.Equal(["Harbor"], await Found("format:image"));
            var audio = await search.ListAsync(new LibraryFilter(Kind: KindFilter.Audio, Sort: LibrarySort.Title), ct: Ct);
            Assert.Equal(["Battle Theme", "Wind"], audio.Select(a => a.Title));
        }
    }
}
