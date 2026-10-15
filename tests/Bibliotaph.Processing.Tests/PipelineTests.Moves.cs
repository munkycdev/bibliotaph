using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.EntityFrameworkCore;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>
    /// File IDs and volume serials as Windows gives them, for any platform: a file gets an ID the first time it is
    /// asked about and keeps it when the test moves it (<see cref="Move"/>), a copy gets its own, and a library folder
    /// is on the disk the test says (<see cref="Disk"/>). A folder it hasn't been told about has no disk to ask, as off Windows.
    /// </summary>
    sealed class FakeIdentity : IFileIdentity
    {
        readonly Lock _lock = new();
        readonly Dictionary<string, string> _ids = [];
        readonly Dictionary<string, (string Serial, bool IsLocal, bool HasFileIds)> _disks = [];
        int _next;

        /// <summary>
        /// Puts a library folder on a disk; <paramref name="network"/> makes it a network share's, and without
        /// <paramref name="fileIds"/> it is a FAT or exFAT drive, which keeps no IDs.
        /// </summary>
        public void Disk(string folder, string serial, bool network = false, bool fileIds = true)
        {
            lock (_lock) _disks[Path.TrimEndingDirectorySeparator(folder)] = (serial, !network, fileIds);
        }

        /// <summary>Moves a file as Explorer would: same file, same ID, new path.</summary>
        public void Move(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            lock (_lock)
                if (_ids.Remove(from, out var id)) _ids[to] = id;
        }

        /// <summary>
        /// Another file now at this path, as when an app saves by writing a new file and renaming it over the old one:
        /// a new ID, whatever its size and date.
        /// </summary>
        public void Renew(string path)
        {
            lock (_lock) _ids.Remove(path);
        }

        public VolumeIdentity? Volume(string folder)
        {
            lock (_lock)
                return _disks.TryGetValue(Path.TrimEndingDirectorySeparator(folder), out var disk) ? new VolumeIdentity(disk.Serial, disk.HasFileIds, disk.IsLocal) : null;
        }

        public string? FileId(string path)
        {
            if (!File.Exists(path)) return null;
            lock (_lock) return _ids.TryGetValue(path, out var id) ? id : _ids[path] = $"V:{++_next}";
        }
    }

    /// <summary>Scans a folder and waits until that scan is recorded.</summary>
    async Task<RootScan> ScanAsync(long rootId)
    {
        var before = _service.Scans.FirstOrDefault(s => s.RootId == rootId);
        _service.RequestScan(rootId);
        return await NextScanAsync(rootId, before);
    }

    async Task<RootScan> NextScanAsync(long rootId, RootScan? before)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while (true)
        {
            if (_service.Scans.FirstOrDefault(s => s.RootId == rootId) is { } scan && !ReferenceEquals(scan, before)) return scan;
            await Task.Delay(50, timeout.Token);
        }
    }

    async Task<FileLocation[]> FileLocationsAsync()
    {
        await using var db = _catalog.CreateContext();
        return await db.FileLocations.AsNoTracking().OrderBy(f => f.Id).ToArrayAsync(Ct);
    }

    /// <summary>Books moved in Explorer keep their cards, and folders that can't be read are offline, never emptied.</summary>
    public sealed class MovesTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_book_moved_within_and_between_library_folders_keeps_its_card_and_everything_on_it_without_being_read_again()
        {
            Copy(_pdfs.KnownText, Path.Combine("Adventures", "Known Text.pdf"));
            // Something stays behind: a folder that lists as empty though it held files reads as offline.
            File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
            var other = Directory.CreateDirectory(Path.Combine(_dir, "Second Library")).FullName;
            _identity.Disk(_library, "1234ABCD");
            _identity.Disk(other, "1234ABCD");
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            var second = (await _roots.AddAsync(other, Ct)).Id;
            await _service.StartAsync(Ct);
            await SettleAsync();

            var card = (await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct)).Single(c => c.Format == SourceFormats.Pdf);
            var collection = await _collections.CreateAsync("Winter campaign", ct: Ct);
            await _collections.AddAsync(collection.Id, [card.EntryId], Ct);
            var pack = await _sessions.CreateAsync("Session 1", ct: Ct);
            await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(card.EntryId, new PageRange(card.DocumentId, 0, 0))], ct: Ct);
            await _notes.SetEntryNoteAsync(card.EntryId, "The owlbear is a red herring.", Ct);
            var sessions = new SessionsService(_sessions, _entries, _libraryStore, _queries, _projector, _places);
            var original = (await FileLocationsAsync()).Single(l => l.DocumentId == card.DocumentId);
            var cards = await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct);

            async Task AssertKeptAsync(long rootId, string relativePath)
            {
                var location = Assert.Single(await FileLocationsAsync(), l => l.ContentHash == original.ContentHash);
                Assert.Equal((original.Id, original.ContentHash, original.DocumentId), (location.Id, location.ContentHash, location.DocumentId));
                Assert.Equal((rootId, relativePath, FileLocationState.Present), (location.SourceRootId, location.RelativePath, location.State));
                Assert.Empty(await _libraryStore.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
                Assert.Equal(cards, await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
                Assert.Empty(await _libraryStore.GetUnavailableAsync(Ct));
                Assert.Equal([collection.Id], await _collections.GetForEntryAsync(card.EntryId, Ct));
                Assert.Equal("The owlbear is a red herring.", await _notes.GetEntryNoteAsync(card.EntryId, Ct));
                var item = Assert.Single((await _sessions.GetAsync(pack.Id, Ct))!.Items);
                Assert.Equal(card.EntryId, item.EntryId);
                Assert.Equal(SessionItemState.Ready, (await sessions.ResolveAsync([item], Ct))[item.Id].State);
            }

            // Renamed and moved into another folder in the same library folder.
            var renamed = Path.Combine("Read next", "Known Text (renamed).pdf");
            _identity.Move(Path.Combine(_library, "Adventures", "Known Text.pdf"), Path.Combine(_library, renamed));
            Assert.Equal(1, (await ScanAsync(root)).Changes!.Moved);
            await AssertKeptAsync(root, renamed);

            // Then into the other library folder, as a watcher there would see it first: the folder it came from is looked
            // at in the same pass, before anything reads it. Indexing is paused, so a read would leave it unhashed.
            _service.Pause(Lane.Index);
            _identity.Move(Path.Combine(_library, renamed), Path.Combine(other, "Known Text.pdf"));
            var left = _service.Scans.Single(s => s.RootId == root);
            await ScanAsync(second);
            Assert.Equal(1, (await NextScanAsync(root, left)).Changes!.Moved);
            await AssertKeptAsync(second, "Known Text.pdf");
            _service.Resume(Lane.Index);
        }

        [Fact]
        public async Task On_a_drive_without_file_ids_a_moved_book_and_a_moved_pack_keep_their_cards_without_being_read_again()
        {
            Copy(_pdfs.KnownText, Path.Combine("Adventures", "Known Text.pdf"));
            var tokens = Directory.CreateDirectory(Path.Combine(_library, "Tokens", "Undead")).FullName;
            for (var i = 1; i <= PackStore.AutomaticMinimum; i++)
                File.WriteAllBytes(Path.Combine(tokens, $"Zombie {i}.png"), FakeCodec.Png(100 + i, 100));
            _identity.Disk(_library, "1234ABCD", fileIds: false);
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            await _service.StartAsync(Ct);
            await SettleAsync();
            var cards = await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct);
            var book = cards.Single(c => c.Format == SourceFormats.Pdf);
            var pack = cards.Single(c => c.IsPack);
            var collection = await _collections.CreateAsync("Crypt crawl", ct: Ct);
            await _collections.AddAsync(collection.Id, [book.EntryId, pack.EntryId], Ct);
            await _favorites.SetAsync([pack.EntryId], favorite: true, Ct);
            var locations = (await FileLocationsAsync()).Select(l => l.Id).Order().ToList();

            // Folders moved in Explorer on a USB stick: the files keep their names, sizes and dates. Indexing is paused,
            // so a read would leave them unhashed.
            _service.Pause(Lane.Index);
            Directory.Move(Path.Combine(_library, "Adventures"), Path.Combine(_library, "Read next"));
            Directory.CreateDirectory(Path.Combine(_library, "Monsters"));
            Directory.Move(tokens, Path.Combine(_library, "Monsters", "Undead"));
            Assert.Equal(21, (await ScanAsync(root)).Changes!.Moved);
            Assert.Empty(await _libraryStore.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
            Assert.Equal(locations, (await FileLocationsAsync()).Select(l => l.Id).Order());
            _service.Resume(Lane.Index);

            // The pack follows its images when packs are next looked at.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                timeout.CancelAfter(Patience);
                while ((await _packs.GetPlaceAsync(pack.EntryId, timeout.Token))!.FullPath != Path.Combine(_library, "Monsters", "Undead"))
                    await Task.Delay(50, timeout.Token);
            }
            await SettleAsync();
            var after = await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct);
            Assert.Equal(new[] { book.EntryId, pack.EntryId }.OrderBy(e => e.Value), after.Select(c => c.EntryId).OrderBy(e => e.Value));
            Assert.Equal((20, "Undead"), (after.Single(c => c.IsPack).Members, after.Single(c => c.IsPack).Title));
            Assert.Equal(Path.Combine(_library, "Read next", "Known Text.pdf"), (await _libraryStore.GetSourceAsync(book.DocumentId, Ct))!.FullPath);
            Assert.Equal([collection.Id], await _collections.GetForEntryAsync(pack.EntryId, Ct));
            Assert.True(await _favorites.IsFavoriteAsync(pack.EntryId, Ct));
        }

        [Fact]
        public async Task A_ZIP_moved_to_another_library_folder_keeps_what_it_holds_without_being_read_again()
        {
            Zip("Bundle.zip", Saved, ("Known Text.pdf", File.ReadAllBytes(_pdfs.KnownText)), ("Maps/Harbor Map.png", FakeCodec.Png(800, 600)));
            // Something stays behind: a folder that lists as empty though it held files reads as offline.
            File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
            var other = Directory.CreateDirectory(Path.Combine(_dir, "Second Library")).FullName;
            _identity.Disk(_library, "1234ABCD");
            _identity.Disk(other, "1234ABCD");
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            var second = (await _roots.AddAsync(other, Ct)).Id;
            await _service.StartAsync(Ct);
            await SettleAsync();
            var map = (await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct)).Single(c => c.Title == "Harbor Map");
            var cards = await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct);
            var zip = (await FileLocationsAsync()).Single(l => l.RelativePath == "Bundle.zip");

            // Moved as a watcher in the folder it went to would see it first; indexing is paused, so a read would show.
            _service.Pause(Lane.Index);
            _identity.Move(Path.Combine(_library, "Bundle.zip"), Path.Combine(other, "Bundle.zip"));
            var left = _service.Scans.Single(s => s.RootId == root);
            await ScanAsync(second);
            Assert.Equal(1, (await NextScanAsync(root, left)).Changes!.Moved);

            Assert.Empty(await _libraryStore.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
            var moved = (await FileLocationsAsync()).Single(l => l.Id == zip.Id);
            Assert.Equal((second, "Bundle.zip", zip.ContentHash), (moved.SourceRootId, moved.RelativePath, moved.ContentHash));
            Assert.Equal(Path.Combine(other, "Bundle.zip"), (await _libraryStore.GetSourceAsync(map.DocumentId, Ct))!.ArchivePath);
            Assert.Equal(cards, await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
            Assert.Empty(await _libraryStore.GetUnavailableAsync(Ct));
            _service.Resume(Lane.Index);
        }

        [Fact]
        public async Task A_library_folder_unplugged_swapped_for_another_disk_or_listing_empty_is_offline_marks_nothing_missing_and_comes_back()
        {
            Copy(_pdfs.KnownText, "Known Text.pdf");
            _identity.Disk(_library, "1234ABCD");
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            await _service.StartAsync(Ct);
            await SettleAsync();
            var card = Assert.Single(await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct));
            var location = Assert.Single(await FileLocationsAsync());
            Assert.Equal("1234ABCD", Assert.Single(await _roots.ListAsync(Ct)).VolumeSerial);

            async Task OfflineAsync(OfflineReason reason)
            {
                Assert.Equal(reason, (await ScanAsync(root)).Offline);
                Assert.Equal(SourceRootAvailability.Offline, Assert.Single(await _roots.ListAsync(Ct)).Availability);
                var kept = Assert.Single(await FileLocationsAsync());
                Assert.Equal((location.Id, FileLocationState.Present), (kept.Id, kept.State));
                Assert.Equal([card.EntryId], await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
                Assert.Equal(EntryAvailability.Offline, (await _libraryStore.GetUnavailableAsync(Ct))[card.EntryId]);
                Assert.Equal(1, (await _libraryStore.GetCountsAsync(Ct)).OfflineFolders);
            }

            // Plugged back in: looked at again as when Windows says a drive arrived, without Look for changes.
            async Task BackAsync()
            {
                var before = _service.Scans.Single(s => s.RootId == root);
                await _service.RescanOfflineAsync(ct: Ct);
                Assert.True((await NextScanAsync(root, before)).Reachable);
                Assert.Equal(SourceRootAvailability.Online, Assert.Single(await _roots.ListAsync(Ct)).Availability);
                Assert.Equal(location.Id, Assert.Single(await FileLocationsAsync()).Id);
                Assert.Empty(await _libraryStore.GetUnavailableAsync(Ct));
                Assert.NotNull(await _libraryStore.GetSourceAsync(card.DocumentId, Ct));
            }

            var unplugged = _library + " (unplugged)";
            Directory.Move(_library, unplugged);
            await OfflineAsync(OfflineReason.Unreachable);
            Directory.Move(unplugged, _library);
            await BackAsync();

            // Another disk in the same drive, with a folder of the same name on it.
            _identity.Disk(_library, "99999999");
            await OfflineAsync(OfflineReason.DifferentDisk);
            _identity.Disk(_library, "1234ABCD");
            await BackAsync();

            var aside = Path.Combine(_dir, "Known Text.pdf");
            File.Move(Path.Combine(_library, "Known Text.pdf"), aside);
            await OfflineAsync(OfflineReason.LooksEmpty);
            File.Move(aside, Path.Combine(_library, "Known Text.pdf"));
            await BackAsync();
        }

        [Fact]
        public async Task A_book_that_cant_be_opened_says_why_naming_its_folder_and_opens_once_a_look_finds_it_again()
        {
            Copy(_pdfs.KnownText, Path.Combine("Adventures", "Known Text.pdf"));
            File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
            _identity.Disk(_library, "1234ABCD");
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            await _service.StartAsync(Ct);
            await SettleAsync();
            var card = (await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct)).Single(c => c.Format == SourceFormats.Pdf);
            var pack = await _sessions.CreateAsync("Session 1", ct: Ct);
            await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(card.EntryId)], ct: Ct);
            var sessions = new SessionsService(_sessions, _entries, _libraryStore, _queries, _projector, _places);
            async Task<SessionItemTarget> ItemAsync() => (await sessions.ResolveAsync((await _sessions.GetAsync(pack.Id, Ct))!.Items, Ct)).Values.Single();

            // Its drive unplugged: offline, naming the folder, and the binder item says so too.
            var unplugged = _library + " (unplugged)";
            Directory.Move(_library, unplugged);
            await ScanAsync(root);
            Assert.Null(await _libraryStore.GetSourceAsync(card.DocumentId, Ct));
            var away = await _places.AwayAsync(card.DocumentId, Ct);
            Assert.Equal((AwayReason.Offline, _library), (away.Reason, away.Folder));
            Assert.Equal([root], away.FoldersToLook);
            var item = await ItemAsync();
            Assert.Equal((SessionItemState.Unreachable, AwayReason.Offline), (item.State, item.Away?.Reason));
            Assert.Contains(_library, item.Reason, StringComparison.Ordinal);

            // Plugged back in, Try again looks at the folder and the book opens at once.
            Directory.Move(unplugged, _library);
            await _service.ScanNowAsync(away.FoldersToLook, Ct);
            Assert.NotNull(await _libraryStore.GetSourceAsync(card.DocumentId, Ct));
            Assert.Equal(SessionItemState.Ready, (await ItemAsync()).State);

            // Moved out of the library: missing, with where it was last.
            var original = Path.Combine(_library, "Adventures", "Known Text.pdf");
            var aside = Path.Combine(_dir, "Known Text.pdf");
            File.Move(original, aside);
            await ScanAsync(root);
            var missing = await _places.AwayAsync(card.DocumentId, Ct);
            Assert.Equal((AwayReason.Missing, original, Path.Combine(_library, "Adventures")), (missing.Reason, missing.LastPath, missing.LastFolder));
            Assert.Null(missing.FoldersToLook);
            Assert.Equal(SessionItemState.Unreachable, (await ItemAsync()).State);

            // Put back, Find it looks through every folder and finds it.
            File.Move(aside, original);
            await _service.ScanNowAsync(missing.FoldersToLook, Ct);
            Assert.NotNull(await _libraryStore.GetSourceAsync(card.DocumentId, Ct));
        }

        [Fact]
        public async Task A_library_folder_pointed_to_its_new_place_keeps_its_books_without_reading_them_again()
        {
            Copy(_pdfs.KnownText, Path.Combine("Adventures", "Known Text.pdf"));
            _identity.Disk(_library, "1234ABCD");
            var root = (await _roots.AddAsync(_library, Ct)).Id;
            var neighbour = Directory.CreateDirectory(Path.Combine(_dir, "Neighbour", "Inside")).Parent!.FullName;
            await _roots.AddAsync(neighbour, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();
            var card = Assert.Single(await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct));
            var original = Assert.Single(await FileLocationsAsync());

            // Copied to a new disk, and the old one gone.
            var moved = Path.Combine(_dir, "New Disk", "RPG Library");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            Directory.Move(_library, moved);
            _identity.Disk(moved, "5678EF01");
            Assert.Equal(OfflineReason.Unreachable, (await ScanAsync(root)).Offline);

            Assert.Equal(RootRelocation.NotFound, await _service.RelocateRootAsync(root, Path.Combine(_dir, "Nowhere"), Ct));
            Assert.Equal(RootRelocation.Overlaps, await _service.RelocateRootAsync(root, Path.Combine(neighbour, "Inside"), Ct));
            Assert.Equal(RootRelocation.Overlaps, await _service.RelocateRootAsync(root, _dir, Ct));
            var before = _service.Scans.Single(s => s.RootId == root);
            Assert.Equal(RootRelocation.Relocated, await _service.RelocateRootAsync(root, moved, Ct));
            Assert.True((await NextScanAsync(root, before)).Reachable);

            var folder = (await _roots.ListAsync(Ct)).Single(r => r.Id == root);
            Assert.Equal((moved, "5678EF01", SourceRootAvailability.Online), (folder.Path, folder.VolumeSerial, folder.Availability));
            var location = Assert.Single(await FileLocationsAsync());
            Assert.Equal((original.Id, original.RelativePath, original.ContentHash, original.DocumentId),
                (location.Id, location.RelativePath, location.ContentHash, location.DocumentId));
            Assert.NotNull(location.NtfsFileId);
            Assert.Empty(await _libraryStore.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
            Assert.Equal([card.EntryId], await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
            Assert.Empty(await _libraryStore.GetUnavailableAsync(Ct));
            Assert.Equal(Path.Combine(moved, "Adventures", "Known Text.pdf"), (await _libraryStore.GetSourceAsync(card.DocumentId, Ct))!.FullPath);
        }
    }
}
