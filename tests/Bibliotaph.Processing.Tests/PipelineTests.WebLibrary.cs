using System.Collections.Concurrent;
using System.Security.Cryptography;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>The web library as a connected PC reads it, in memory: its list of files, what it read from each, covers and files.</summary>
    sealed class FakeWebBooks : IWebBooks
    {
        public bool IsOn { get; set; } = true;

        public WebShelf? Shelf { get; set; }

        public ConcurrentDictionary<string, SharedReading> Readings { get; } = [];

        public ConcurrentDictionary<string, byte[]> Covers { get; } = [];

        public ConcurrentDictionary<string, byte[]> Files { get; } = [];

        int _downloads;

        /// <summary>How many files were downloaded.</summary>
        public int Downloads => _downloads;

        public Task<WebShelfRead> ReadShelfAsync(string? version, CancellationToken ct) => Task.FromResult(
            Shelf is null ? WebShelfRead.Unreached : Shelf.Version == version ? WebShelfRead.Unchanged : new WebShelfRead(true, Shelf));

        public Task<SharedReading?> ReadReadingAsync(string contentHash, CancellationToken ct) => Task.FromResult(Readings.GetValueOrDefault(contentHash));

        public Task<byte[]?> ReadCoverAsync(string name, CancellationToken ct) => Task.FromResult(Covers.GetValueOrDefault(name));

        public Task<Stream> OpenFileAsync(string contentHash, CancellationToken ct)
        {
            if (!Files.TryGetValue(contentHash, out var bytes)) throw new FileNotFoundException("The file isn't in your web library any more.");
            Interlocked.Increment(ref _downloads);
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    /// <summary>
    /// The web library's books on a connected PC: listed from the server's list of files, put in the library and made
    /// searchable from what the server read, and downloaded only to be opened.
    /// </summary>
    public sealed class WebLibraryTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        static readonly DateTime Modified = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

        static WebShelfFolder Folder(long id, string name, params WebShelfFile[] files) => new(id, name, $"OneDrive · Dana › {name}", files);

        static SharedReading Reading(string hash, string text) => new("pdf", "The Sunken Lantern", 2, null, null, false, true,
            "The Sunken Lantern", "Ana Ruiz", null, null, null, "None", """{"canCopy":true,"canPrint":true,"encrypted":false}""", hash + ".jpg",
            [new StoredPage(0, "i", 612, 792, text, "pdf", 0.95, false, null), new StoredPage(1, "ii", 612, 792, "Charts of the old harbor", "pdf", 0.9, false, null)],
            [new OutlineRow("The harbor", 1, 0)], "Complete", null);

        WebShelfSync Sync() => new(_web, _shelf, _roots, _service);

        [Fact]
        public async Task A_web_library_book_is_listed_searchable_and_covered_without_being_downloaded()
        {
            var hash = new string('a', 64);
            _web.Shelf = new WebShelf("v1", [Folder(7, "RPG", new WebShelfFile("Adventures/Lantern.pdf", hash, 1234, Modified))]);
            _web.Readings[hash] = Reading(hash, "The lantern waits beneath the drowned bell tower");
            _web.Covers[hash + ".jpg"] = [0xFF, 0xD8, 1, 2, 0xFF, 0xD9];

            await _service.StartAsync(Ct);
            await Sync().SyncNowAsync(Ct);
            await SettleAsync();

            var root = Assert.Single(await _roots.ListAsync(Ct));
            var entry = Assert.Single(await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct));
            var found = await new LibraryQueries(_index).SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("drowned")), new LibraryFilter(), ct: Ct);
            var source = (await _libraryStore.GetSourceAsync(entry.DocumentId, Ct))!;
            await using var c = _index.OpenRead();

            Assert.Equal(WebLibraryPaths.Of(7), root.Path);
            Assert.Equal(("The Sunken Lantern", 2), (entry.Title, entry.PageCount));
            var hit = Assert.Single(Assert.Single(found.Entries).Pages);
            Assert.Equal((entry.DocumentId, 0, "i"), (hit.DocumentId, hit.PdfPage, hit.Label));
            Assert.All(new[] { Stage.Probe, Stage.Text, Stage.Covers }, stage => Assert.Equal("Complete", StatusOf(c, "The Sunken Lantern", stage)));
            Assert.Equal(hash + ".jpg", entry.Cover);
            Assert.Equal(_web.Covers[hash + ".jpg"], await File.ReadAllBytesAsync(new CoverCache(_paths).PathFor(hash + ".jpg"), Ct));
            Assert.Equal("Adventures", source.FolderHint);
            Assert.Equal("bibliotaph.online › OneDrive · Dana › RPG › Adventures › Lantern.pdf", _shelf.Show(source.FullPath));
            Assert.Equal(0, _web.Downloads);
        }

        [Fact]
        public async Task A_book_the_web_library_hasnt_read_yet_waits_then_is_read()
        {
            var hash = new string('b', 64);
            _web.Shelf = new WebShelf("v1", [Folder(7, "RPG", new WebShelfFile("Lantern.pdf", hash, 1234, Modified))]);

            await _service.StartAsync(Ct);
            await Sync().SyncNowAsync(Ct);
            var documentId = await WaitForAsync(() => _libraryStore.FindDocumentAsync(ContentHash.Parse(hash), Ct));
            // Asked a few times, at the short wait these tests use.
            await Task.Delay(1000, Ct);
            var before = await _queries.GetTitleAsync(documentId, Ct);
            _web.Readings[hash] = Reading(hash, "The lantern waits");
            _web.Covers[hash + ".jpg"] = [0xFF, 0xD8, 0xFF, 0xD9];
            await SettleAsync();

            Assert.Null(before);
            Assert.Equal("The Sunken Lantern", await _queries.GetTitleAsync(documentId, Ct));
            Assert.Equal(0, _web.Downloads);
        }

        [Fact]
        public async Task The_web_librarys_folders_follow_it_and_go_when_this_PC_is_disconnected()
        {
            var sync = Sync();
            async Task<string[]> FoldersAsync() => [.. (await _roots.ListAsync(Ct)).Select(r => r.Path)];

            _web.Shelf = new WebShelf("v1", [Folder(7, "RPG"), Folder(8, "Maps")]);
            await sync.SyncNowAsync(Ct);
            var first = await FoldersAsync();
            // Removed here, it stays removed while the web library still has it.
            await _roots.RemoveAsync((await _roots.ListAsync(Ct)).Single(r => r.Path == WebLibraryPaths.Of(8)).Id, Ct);
            _web.Shelf = new WebShelf("v2", [Folder(7, "RPG"), Folder(8, "Maps"), Folder(9, "Handouts")]);
            await sync.SyncNowAsync(Ct);
            var second = await FoldersAsync();
            // Gone from the web library, it goes here.
            _web.Shelf = new WebShelf("v3", [Folder(8, "Maps"), Folder(9, "Handouts")]);
            await sync.SyncNowAsync(Ct);
            var third = await FoldersAsync();
            _web.IsOn = false;
            await sync.SyncNowAsync(Ct);
            var disconnected = await FoldersAsync();
            var shelfKept = File.Exists(_paths.WebShelf);
            var forgotten = _shelf.Current is null;
            // Connecting again starts afresh: every folder the web library has comes back.
            _web.IsOn = true;
            await sync.SyncNowAsync(Ct);
            var reconnected = await FoldersAsync();

            Assert.Equal([WebLibraryPaths.Of(7), WebLibraryPaths.Of(8)], first);
            Assert.Equal([WebLibraryPaths.Of(7), WebLibraryPaths.Of(9)], second);
            Assert.Equal([WebLibraryPaths.Of(9)], third);
            Assert.Empty(disconnected);
            Assert.False(shelfKept);
            Assert.True(forgotten);
            Assert.Equal([WebLibraryPaths.Of(8), WebLibraryPaths.Of(9)], reconnected);
        }

        [Fact]
        public async Task A_web_library_book_is_downloaded_to_open_and_a_copy_on_this_PC_opens_instead()
        {
            var bytes = await File.ReadAllBytesAsync(_pdfs.Injected, Ct);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            _web.Shelf = new WebShelf("v1", [Folder(7, "RPG", new WebShelfFile("Lantern.pdf", hash, bytes.Length, Modified))]);
            _web.Readings[hash] = Reading(hash, "The lantern waits");
            _web.Covers[hash + ".jpg"] = [0xFF, 0xD8, 0xFF, 0xD9];
            _web.Files[hash] = bytes;
            await _service.StartAsync(Ct);
            await Sync().SyncNowAsync(Ct);
            await SettleAsync();
            var documentId = (await _libraryStore.FindDocumentAsync(ContentHash.Parse(hash), Ct))!.Value;

            var web = (await _libraryStore.GetSourceAsync(documentId, Ct))!;
            byte[] opened;
            await using (var file = await _sources.OpenAsync(web, Ct)) opened = await File.ReadAllBytesAsync(file.Path, Ct);
            Copy(_pdfs.Injected, "Lantern.pdf");
            await _roots.AddAsync(_library, Ct);
            await _service.ScanNowAsync(ct: Ct);
            await SettleAsync();
            var local = (await _libraryStore.GetSourceAsync(documentId, Ct))!;

            Assert.True(WebLibraryPaths.IsWebPath(web.FullPath));
            Assert.Equal(bytes, opened);
            Assert.Equal(1, _web.Downloads);
            Assert.Equal(Path.Combine(_library, "Lantern.pdf"), local.FullPath);
        }

        static async Task<long> WaitForAsync(Func<Task<long?>> found)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(Patience);
            while (true)
            {
                if (await found() is { } value) return value;
                await Task.Delay(100, timeout.Token);
            }
        }
    }
}
