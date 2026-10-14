using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>
    /// Folder watching as a test drives it: each folder watched is remembered, and <see cref="Change"/> and
    /// <see cref="Stop"/> report on it as the file system would. A folder that doesn't exist can't be watched, as with
    /// the real watcher.
    /// </summary>
    sealed class FakeWatcher : IFolderWatcher
    {
        readonly Lock _lock = new();
        readonly List<Watching> _watches = [];

        public IDisposable Watch(string folder, Action changed, Action stopped)
        {
            if (!Directory.Exists(folder)) throw new ArgumentException($"The directory name '{folder}' does not exist.", nameof(folder));
            var watch = new Watching(Path.TrimEndingDirectorySeparator(folder), changed, stopped);
            lock (_lock) _watches.Add(watch);
            return watch;
        }

        /// <summary>How many watchers were ever made for a folder.</summary>
        public int Made(string folder) => Of(folder).Count;

        /// <summary>How many watchers are watching a folder now.</summary>
        public int Active(string folder) => Of(folder).Count(w => !w.Disposed);

        /// <summary>Something in the folder changed.</summary>
        public void Change(string folder)
        {
            foreach (var watch in Of(folder).Where(w => !w.Disposed)) watch.Changed();
        }

        /// <summary>Watching the folder stopped for good, as when its drive went away.</summary>
        public void Stop(string folder)
        {
            foreach (var watch in Of(folder).Where(w => !w.Disposed))
            {
                watch.Stopped();
                watch.Changed();
            }
        }

        List<Watching> Of(string folder)
        {
            folder = Path.TrimEndingDirectorySeparator(folder);
            lock (_lock) return [.. _watches.Where(w => w.Folder == folder)];
        }

        sealed class Watching(string folder, Action changed, Action stopped) : IDisposable
        {
            volatile bool _disposed;

            public string Folder { get; } = folder;
            public Action Changed { get; } = changed;
            public Action Stopped { get; } = stopped;
            public bool Disposed => _disposed;

            public void Dispose() => _disposed = true;
        }
    }

    /// <summary>Watched library folders: a change in one scans that folder again, without anyone asking.</summary>
    public sealed class WatchingTests : PipelineTests
    {
        public WatchingTests(SyntheticPdfs synthetic) : base(synthetic) => _watchFolders = true;

        [Fact]
        public async Task A_change_in_a_watched_folder_scans_that_folder_again_once_it_has_been_quiet()
        {
            Directory.CreateDirectory(_library);
            File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
            var other = Directory.CreateDirectory(Path.Combine(_dir, "Second Library")).FullName;
            File.WriteAllBytes(Path.Combine(other, "Dungeon Map.png"), FakeCodec.Png(320, 240));
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            var second = (await _roots.AddAsync(other, Ct)).Id;
            await _service.StartAsync(Ct);
            await SettleAsync();
            Assert.Equal((1, 1), (_watcher.Active(_library), _watcher.Active(other)));
            var before = _service.Scans.Single(s => s.RootId == root);
            var otherBefore = _service.Scans.Single(s => s.RootId == second);

            // A file arrives as a copy does, with a change reported for each step of it.
            File.WriteAllBytes(Path.Combine(_library, "Town Map.png"), FakeCodec.Png(800, 600));
            for (var i = 0; i < 5; i++) _watcher.Change(_library);
            var scan = await NextScanAsync(root, before);

            Assert.Equal(1, scan.Changes!.Added);
            Assert.Equal(3, (await SettleAsync()).Documents);
            // One scan for all of it, and none of the folder nothing changed in.
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Assert.Same(scan, _service.Scans.Single(s => s.RootId == root));
            Assert.Same(otherBefore, _service.Scans.Single(s => s.RootId == second));
        }

        [Fact]
        public async Task A_watcher_that_stopped_is_made_again_when_its_folder_is_scanned()
        {
            Directory.CreateDirectory(_library);
            File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            await _service.StartAsync(Ct);
            await SettleAsync();
            Assert.Equal(1, _watcher.Made(_library));

            // Its drive went away for a moment; the scan that follows watches it afresh.
            _watcher.Stop(_library);
            var scan = await NextScanAsync(root, _service.Scans.Single(s => s.RootId == root));
            Assert.True(scan.Reachable);
            Assert.Equal((2, 1), (_watcher.Made(_library), _watcher.Active(_library)));

            // The new watcher's changes are heard.
            File.WriteAllBytes(Path.Combine(_library, "Town Map.png"), FakeCodec.Png(800, 600));
            _watcher.Change(_library);
            Assert.Equal(1, (await NextScanAsync(root, scan)).Changes!.Added);
        }
    }
}
