using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>The Windows cloud files API, as a test says each file is. Every file is local unless told otherwise.</summary>
    sealed class FakeCloud : ICloudFiles
    {
        readonly Lock _lock = new();
        readonly Dictionary<string, CloudFileState> _states = [with(StringComparer.OrdinalIgnoreCase)];
        readonly List<string> _returned = [];

        /// <summary>Called as a file is returned, before anything records it, to look at the library at that moment.</summary>
        public Action<string>? Returning { get; set; }

        public IReadOnlyList<string> Returned
        {
            get { lock (_lock) return [.. _returned]; }
        }

        public void Set(string path, CloudFileState state)
        {
            lock (_lock) _states[path] = state;
        }

        public CloudFileState StateOf(string path)
        {
            lock (_lock) return _states.GetValueOrDefault(path, CloudFileState.Local);
        }

        /// <summary>A file was opened for reading: one that was online-only is on this device now.</summary>
        public void Downloaded(string path)
        {
            lock (_lock)
                if (_states.GetValueOrDefault(path) == CloudFileState.OnlineOnly) _states[path] = CloudFileState.Local;
        }

        public void ReturnToOnlineOnly(string path)
        {
            Returning?.Invoke(path);
            lock (_lock)
            {
                _returned.Add(path);
                _states[path] = CloudFileState.OnlineOnly;
            }
        }
    }

    /// <summary>The read-only reader, telling the stand-in cloud that each file it opens has been downloaded.</summary>
    sealed class DownloadingReader(ISourceFileReader reader, FakeCloud cloud) : ISourceFileReader
    {
        public Stream OpenRead(string path)
        {
            cloud.Downloaded(path);
            return reader.OpenRead(path);
        }
    }

    /// <summary>The disk's free space: the real one, unless a test says otherwise.</summary>
    sealed class FakeDisk : IDiskSpace
    {
        readonly DiskSpace _real = new();

        public Func<long?>? Free { get; set; }

        public long? FreeBytes(string path) => Free is { } free ? free() : _real.FreeBytes(path);
    }

    static async Task WaitForAsync(Func<Task<bool>> condition, string failure)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (!await condition())
        {
            if (timeout.IsCancellationRequested) Assert.Fail(failure);
            await Task.Delay(100, Ct);
        }
    }

    async Task<FileLocation> LocationAsync(string relative) =>
        (await FileLocationsAsync()).Single(l => l.RelativePath == relative);

    string? Status(string title, Stage stage)
    {
        using var c = _index.OpenRead();
        return StatusOf(c, title, stage);
    }

    /// <summary>
    /// "Return files to online-only after indexing": a file that reading downloaded from OneDrive goes back to online-only
    /// once its stages are done with it, through a stand-in for the Windows cloud files API.
    /// </summary>
    public sealed class OnlineOnlyTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_file_online_only_when_read_goes_back_to_online_only_once_its_stages_are_done()
        {
            Copy(_pdfs.KnownText, "Cloud Book.pdf");
            Copy(_pdfs.Scanned, "Local Scan.pdf");
            var cloudBook = Path.Combine(_library, "Cloud Book.pdf");
            _cloud.Set(cloudBook, CloudFileState.OnlineOnly);
            var stagesWhenReturned = new List<string?>();
            _cloud.Returning = _ => stagesWhenReturned.AddRange(
                [Status("Cloud Book", Stage.Probe), Status("Cloud Book", Stage.Text), Status("Cloud Book", Stage.Covers), Status("Cloud Book", Stage.Ocr)]);
            await _roots.AddAsync(_library, Ct);

            await _service.StartAsync(Ct);
            await SettleAsync();
            await WaitForAsync(async () => !(await LocationAsync("Cloud Book.pdf")).ReturnToOnlineOnly, "The online-only file didn't go back.");

            // Only the file that was online-only when it was read, and only once every stage that reads it had finished.
            Assert.Equal([cloudBook], _cloud.Returned);
            Assert.Equal("Complete", stagesWhenReturned[0]);
            Assert.Equal("Complete", stagesWhenReturned[1]);
            Assert.Equal("Complete", stagesWhenReturned[2]);
            Assert.Contains(stagesWhenReturned[3], new[] { "Complete", "Blocked" });
            Assert.Equal(FileLocationState.OnlineOnly, (await LocationAsync("Cloud Book.pdf")).State);
            var local = await LocationAsync("Local Scan.pdf");
            Assert.False(local.ReturnToOnlineOnly);
            Assert.Equal(FileLocationState.Present, local.State);
            // The book stays in the library, searchable from what was read.
            await using var c = _index.OpenRead();
            Assert.Equal(1L, c.ExecuteScalar<long>("SELECT count(*) FROM page_fts WHERE page_fts MATCH '\"owlbear waits\"'"));
        }

        [Fact]
        public async Task Files_stay_on_this_device_while_the_setting_is_off_and_go_back_once_it_is_on()
        {
            Assert.True(await _returns.IsOnAsync(Ct));
            await _returns.SetOnAsync(false, Ct);
            Copy(_pdfs.KnownText, "Cloud Book.pdf");
            var cloudBook = Path.Combine(_library, "Cloud Book.pdf");
            _cloud.Set(cloudBook, CloudFileState.OnlineOnly);
            await _roots.AddAsync(_library, Ct);

            await _service.StartAsync(Ct);
            await SettleAsync();
            await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

            // Marked all the same, so turning the setting on gives back what indexing downloaded meanwhile.
            Assert.Empty(_cloud.Returned);
            Assert.True((await LocationAsync("Cloud Book.pdf")).ReturnToOnlineOnly);

            await _returns.SetOnAsync(true, Ct);
            await WaitForAsync(() => Task.FromResult(_cloud.Returned.Count == 1), "Turning the setting on didn't return the file.");
            Assert.Equal([cloudBook], _cloud.Returned);
        }

        [Fact]
        public async Task A_file_the_user_chose_to_always_keep_on_this_device_stays_there()
        {
            await _returns.SetOnAsync(false, Ct);
            Copy(_pdfs.KnownText, "Cloud Book.pdf");
            var cloudBook = Path.Combine(_library, "Cloud Book.pdf");
            _cloud.Set(cloudBook, CloudFileState.OnlineOnly);
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            // "Always keep on this device", chosen after Bibliotaph read it.
            _cloud.Set(cloudBook, CloudFileState.Pinned);
            await _returns.SetOnAsync(true, Ct);
            await WaitForAsync(async () => !(await LocationAsync("Cloud Book.pdf")).ReturnToOnlineOnly, "The pinned file is still waiting to go back.");

            Assert.Empty(_cloud.Returned);
            Assert.Equal(CloudFileState.Pinned, _cloud.StateOf(cloudBook));
        }

        [Fact]
        public async Task A_book_open_in_the_reader_goes_back_only_once_it_is_closed()
        {
            await _returns.SetOnAsync(false, Ct);
            Copy(_pdfs.KnownText, "Cloud Book.pdf");
            var cloudBook = Path.Combine(_library, "Cloud Book.pdf");
            _cloud.Set(cloudBook, CloudFileState.OnlineOnly);
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();
            var documentId = (await LocationAsync("Cloud Book.pdf")).DocumentId!.Value;

            // The reader holds its file through SourceFiles, as the viewer does while a book is open.
            var open = await _sources.OpenAsync((await _libraryStore.GetSourceAsync(documentId, Ct))!, Ct);
            await _returns.SetOnAsync(true, Ct);
            await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
            Assert.Empty(_cloud.Returned);
            Assert.True(_sources.IsInUse(cloudBook));

            await open.DisposeAsync();
            Assert.False(_sources.IsInUse(cloudBook));
            await WaitForAsync(() => Task.FromResult(_cloud.Returned.Count == 1), "The book didn't go back once the reader closed it.");
        }

        [Fact]
        public async Task A_book_still_waiting_for_its_scanned_pages_to_be_read_keeps_its_file_until_they_are()
        {
            Copy(_pdfs.KnownText, "Cloud Book.pdf");
            var cloudBook = Path.Combine(_library, "Cloud Book.pdf");
            _cloud.Set(cloudBook, CloudFileState.OnlineOnly);
            _service.Pause(Lane.Ocr);
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);

            await WaitForAsync(() => Task.FromResult(Status("Cloud Book", Stage.Covers) == "Complete" && Status("Cloud Book", Stage.Ocr) == "Pending"),
                "The book wasn't read as far as its scanned page.");
            await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);
            Assert.Empty(_cloud.Returned);

            _service.Resume(Lane.Ocr);
            await WaitForAsync(() => Task.FromResult(_cloud.Returned.Count == 1), "The book didn't go back once its scanned page was read.");
            Assert.Contains(Status("Cloud Book", Stage.Ocr), new[] { "Complete", "Blocked" });
        }

        /// <summary>
        /// The low-disk pause and returns together: an online-only file waits for space, and the space a book already
        /// read gives back lets it go on. While files wait for space, a paused lane's books don't keep theirs: the book's
        /// scanned page is read later, downloading the file again.
        /// </summary>
        [Fact]
        public async Task Space_given_back_lets_online_only_files_waiting_for_it_download()
        {
            Copy(_pdfs.KnownText, "Cloud Book.pdf");
            Copy(_pdfs.Scanned, "Waiting Scan.pdf");
            var cloudBook = Path.Combine(_library, "Cloud Book.pdf");
            _cloud.Set(cloudBook, CloudFileState.OnlineOnly);
            // Short of space until the first book has gone back.
            _disk.Free = () => _cloud.Returned.Count > 0 ? 100L << 30 : 1L << 30;
            string? waitingHashWhenReturned = "not returned";
            var waitingForSpaceWhenReturned = false;
            _cloud.Returning = _ =>
            {
                waitingForSpaceWhenReturned = _service.WaitingForDiskSpace;
                waitingHashWhenReturned = LocationAsync("Waiting Scan.pdf").GetAwaiter().GetResult().ContentHash;
            };
            _service.Pause(Lane.Ocr);
            _service.Pause(Lane.Index);
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await WaitForAsync(async () => (await _libraryStore.GetCountsAsync(Ct)).Files == 2, "The folder wasn't scanned.");

            // The scan here can't see OneDrive's attributes, so the second file is made online-only in the catalog: the
            // hasher checks its drive's space before downloading it.
            await using (var db = _catalog.CreateContext())
                await db.FileLocations.Where(f => f.RelativePath == "Waiting Scan.pdf")
                    .ExecuteUpdateAsync(u => u.SetProperty(f => f.State, FileLocationState.OnlineOnly), Ct);
            _service.Resume(Lane.Index);

            await WaitForAsync(async () => (await LocationAsync("Waiting Scan.pdf")).ContentHash is not null, "The waiting file was never read.");
            Assert.Equal([cloudBook], _cloud.Returned);
            Assert.True(waitingForSpaceWhenReturned);
            Assert.Null(waitingHashWhenReturned);
            // Its scanned page waits for the paused lane, which will download it again.
            Assert.Equal("Pending", Status("Cloud Book", Stage.Ocr));
            await WaitForAsync(() => Task.FromResult(!_service.WaitingForDiskSpace), "Online-only files are still waiting for space.");
        }

        [Fact]
        public async Task An_online_only_ZIP_goes_back_once_the_files_inside_it_are_read()
        {
            Zip("Cloud Bundle.zip", Saved,
                ("Cloud Bundle/Known Text.pdf", File.ReadAllBytes(_pdfs.KnownText)),
                ("Cloud Bundle/Harbor Map.png", FakeCodec.Png(800, 600)));
            var zip = Path.Combine(_library, "Cloud Bundle.zip");
            _cloud.Set(zip, CloudFileState.OnlineOnly);
            var statusesWhenReturned = new List<string?>();
            _cloud.Returning = _ => statusesWhenReturned.AddRange([Status("Known Text", Stage.Text), Status("Harbor Map", Stage.Covers)]);
            await _roots.AddAsync(_library, Ct);

            await _service.StartAsync(Ct);
            await SettleAsync();
            await WaitForAsync(() => Task.FromResult(_cloud.Returned.Count == 1), "The ZIP didn't go back.");
            // The catalog is marked after the file goes back, so the rows can still read Present for a moment.
            await WaitForAsync(async () =>
            {
                await using var marking = _catalog.CreateContext();
                return await marking.FileLocations.AllAsync(f => f.State == FileLocationState.OnlineOnly, Ct);
            }, "The ZIP's rows weren't marked online-only.");

            Assert.Equal([zip], _cloud.Returned);
            Assert.Equal(["Complete", "Complete"], statusesWhenReturned);
            await using var db = _catalog.CreateContext();
            var states = await db.FileLocations.AsNoTracking().Select(f => new { f.RelativePath, f.State, f.ReturnToOnlineOnly }).ToListAsync(Ct);
            Assert.Equal(3, states.Count);
            Assert.All(states, s => Assert.Equal(FileLocationState.OnlineOnly, s.State));
            Assert.All(states, s => Assert.False(s.ReturnToOnlineOnly));
            await using var c = _index.OpenRead();
            Assert.Equal(2L, c.ExecuteScalar<long>("SELECT count(*) FROM doc"));
        }
    }
}
