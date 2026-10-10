using System.Text.Json;
using System.Text.Json.Nodes;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Processing.Tests;

/// <summary>Slice 4j: a backup restored on another computer, with its library folders somewhere else (A15).</summary>
public sealed partial class PipelineTests
{
    /// <summary>The data folder <see cref="InitializeAsync"/> builds on; a test changes it to start again as another computer.</summary>
    string _dataFolder = "data";

    /// <summary>Closes everything, as quitting the app does, so the test can start it again on another data folder.</summary>
    async Task CloseAsync()
    {
        await _service.StopAsync(CancellationToken.None);
        _service.Dispose();
        await _writer.StopAsync(CancellationToken.None);
        _writer.Dispose();
        await _workers.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Every row of every table of a catalog, as the JSON export writes them, less the columns a restore is meant to
    /// change: where each library folder is and whether it can be reached.
    /// </summary>
    static async Task<Dictionary<string, List<string>>> TablesAsync(string catalog)
    {
        using var stream = new MemoryStream();
        await new CatalogExport(new CatalogFile(catalog)).WriteJsonAsync(stream, "test", DateTime.UnixEpoch, Ct);
        var tables = JsonNode.Parse(stream.ToArray())!["tables"]!.AsObject();
        return tables.ToDictionary(t => t.Key, t => t.Value!.AsArray().Select(row =>
        {
            var columns = row!.AsObject().DeepClone().AsObject();
            if (t.Key == "source_root") foreach (var changed in new[] { "path", "availability" }) columns.Remove(changed);
            return columns.ToJsonString();
        }).ToList());
    }

    [Fact]
    public async Task A_backup_restored_on_another_computer_brings_back_all_user_work_follows_a_moved_folder_and_marks_a_missing_one_offline()
    {
        // Two library folders on this computer, each on its disk.
        Copy(pdfs.KnownText, Path.Combine("Adventures", "Known Text.pdf"));
        File.WriteAllBytes(Path.Combine(_library, "Handout Map.png"), FakeCodec.Png(640, 480));
        var maps = Directory.CreateDirectory(Path.Combine(_dir, "Map Library")).FullName;
        File.WriteAllBytes(Path.Combine(maps, "Harbor Map.png"), FakeCodec.Png(800, 600));
        _identity.Disk(_library, "1234ABCD");
        _identity.Disk(maps, "9876FEDC");
        var root = (await _roots.AddAsync(_library, Ct)).Id;
        var second = (await _roots.AddAsync(maps, Ct)).Id;
        await _service.StartAsync(Ct);
        await SettleAsync();

        // The user's work: a collection, a session with a page of the book, a note, a heart, a tag, a book owned elsewhere.
        var cards = await new LibraryQueries(_index).ListAsync(new LibraryFilter(), ct: Ct);
        var book = cards.Single(c => c.Format == SourceFormats.Pdf);
        var harbor = cards.Single(c => c.Title == "Harbor Map");
        var collection = await _collections.CreateAsync("Winter campaign", ct: Ct);
        await _collections.AddAsync(collection.Id, [book.EntryId, harbor.EntryId], Ct);
        var pack = await _sessions.CreateAsync("Session 1", new DateOnly(2026, 11, 7), Ct);
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(book.EntryId, new PageRange(book.DocumentId, SyntheticPdfs.KnownPhrasePage, SyntheticPdfs.KnownPhrasePage), "The lair")], ct: Ct);
        await _notes.SetEntryNoteAsync(book.EntryId, "The owlbear is a red herring.", Ct);
        await _favorites.SetAsync([book.EntryId], true, Ct);
        Assert.Null(await _metadata.SetAsync(book.EntryId, MetadataFields.Tags, "Friday game", Ct));
        var (boxed, problem) = await _elsewhere.AddAsync(new ElsewhereDraft("Boxed Set of the Marches", AlsoOwn: ["Print"]), Ct);
        Assert.Null(problem);
        var locations = (await FileLocationsAsync()).Where(l => l.SourceRootId == root).ToList();

        // Back up now, then this computer is done with.
        var backup = Path.Combine(_dir, "USB stick", "Bibliotaph backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await _service.StopAsync(Ct);
        using (var backups = new BackupService(_paths, _catalog, _index, _settings))
            await backups.BackUpAsync(backup, Ct);
        var before = await TablesAsync(_paths.CatalogDatabase);
        await CloseAsync();

        // The other computer: the main folder is on another disk at another path, and the maps folder isn't there.
        var moved = Path.Combine(_dir, "New Disk", "RPG Library");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(_library, moved);
        Directory.Delete(maps, recursive: true);
        _dataFolder = "other computer";
        var other = new AppPaths(Path.Combine(_dir, _dataFolder));
        foreach (var directory in other.Directories) Directory.CreateDirectory(directory);
        // Its first start made an empty catalog; then Settings > Backup > Restore.
        var fresh = new CatalogDatabase(other.CatalogDatabase, other.Backups);
        await fresh.MigrateAsync(Ct);
        using (var restoring = new BackupService(other, fresh, IndexDatabase.ForFile(other.IndexDatabase), new SettingsStore(new Factory(fresh))))
        {
            var preview = await restoring.PreviewRestoreAsync(backup, Ct);
            Assert.True(preview.CanRestore, preview.Problem);
            Assert.False(preview.IncludesIndex);
            Assert.Equal([(root, false), (second, false)], preview.Folders.Select(f => (f.Id, f.Found)));
            Assert.Equal((4, 1), (preview.Counts!.Books, preview.Counts.Notes));
            var places = new Dictionary<long, string?> { [root] = moved };
            Assert.Equal(RootRelocation.Relocated, BackupService.CheckPlace(preview, places, root, moved));
            Assert.Null(await restoring.StageRestoreAsync(preview, places, Ct));
        }
        // Restart: the swap happens before anything opens the databases.
        SqliteConnection.ClearAllPools();
        var outcome = await PendingRestore.ApplyIfRequestedAsync(other, ct: Ct);
        Assert.True(outcome!.Restored, outcome.Problem);
        Assert.Equal(1, outcome.Request!.OfflineFolders);

        // Every row of every table came back, ids and all.
        var after = await TablesAsync(other.CatalogDatabase);
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), after.Keys.Order(StringComparer.Ordinal));
        foreach (var (table, kept) in before) Assert.True(kept.SequenceEqual(after[table]), $"{table} didn't come back as it was.");
        Assert.Contains(before["note"], n => n.Contains("red herring", StringComparison.Ordinal));

        // The app starts on the restored catalog with a new index.db. Reading is held back to see what the scan does alone.
        await InitializeAsync();
        _identity.Disk(moved, "5555AAAA");
        _service.Pause(Lane.Index);
        await _service.StartAsync(Ct);
        Assert.True((await NextScanAsync(root, null)).Reachable);
        Assert.Equal(OfflineReason.Unreachable, (await NextScanAsync(second, null)).Offline);

        // The moved folder's files are the same rows with the same hashes, so nothing is hashed again.
        var folders = await _roots.ListAsync(Ct);
        Assert.Equal((moved, SourceRootAvailability.Online), folders.Where(r => r.Id == root).Select(r => (r.Path, r.Availability)).Single());
        Assert.Equal(SourceRootAvailability.Offline, folders.Single(r => r.Id == second).Availability);
        Assert.Equal("5555AAAA", folders.Single(r => r.Id == root).VolumeSerial);
        var now = (await FileLocationsAsync()).Where(l => l.SourceRootId == root).ToList();
        Assert.Equal(locations.Select(l => (l.Id, l.RelativePath, l.ContentHash, l.DocumentId, l.State)), now.Select(l => (l.Id, l.RelativePath, l.ContentHash, l.DocumentId, l.State)));
        Assert.Empty(await _libraryStore.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        // The missing folder's map stays in the library, marked offline.
        Assert.Equal(EntryAvailability.Offline, (await _libraryStore.GetUnavailableAsync(Ct)).GetValueOrDefault(harbor.EntryId));

        // Then the index is rebuilt from the files in the background: search finds the book's page again, and the map
        // whose folder is missing is listed by its file name, to be read once its folder is pointed to its new place.
        _service.Resume(Lane.Index);
        var search = new LibraryQueries(_index);
        var filter = new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            timeout.CancelAfter(Patience);
            while ((await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("owlbear")), filter, ct: timeout.Token)).Entries is not [{ Entry.EntryId: var found }]
                || found != book.EntryId || (await search.ListAsync(new LibraryFilter(), ct: timeout.Token)).All(c => c.EntryId != harbor.EntryId))
                await Task.Delay(200, timeout.Token);
        }

        // And the user's work is on the same cards: collection, session page, note, heart, tag, the book owned elsewhere.
        cards = await search.ListAsync(new LibraryFilter(), ct: Ct);
        Assert.Contains(cards, c => c.EntryId == book.EntryId && c.Favorite);
        Assert.Contains(cards, c => c.EntryId == boxed!.Value && c is { IsElsewhere: true, Title: "Boxed Set of the Marches" });
        Assert.Equal([collection.Id], await _collections.GetForEntryAsync(book.EntryId, Ct));
        Assert.Equal("The owlbear is a red herring.", await _notes.GetEntryNoteAsync(book.EntryId, Ct));
        var item = Assert.Single((await _sessions.GetAsync(pack.Id, Ct))!.Items);
        var sessions = new SessionsService(_sessions, _entries, _libraryStore, _queries, _projector, _places);
        Assert.Equal(SessionItemState.Ready, (await sessions.ResolveAsync([item], Ct))[item.Id].State);
        Assert.Equal(["Friday game"], (await _metadataStore.GetAsync(book.EntryId, Ct)).Compute()[MetadataFields.Tags].Values.Select(v => v.Value));

        // The CSV lists every card with where its file is now, or was, and the JSON has every table.
        var export = new ExportService(_libraryStore, _entries, _metadataStore, _vocabulary, new PackStore(new Factory(_catalog)), new CatalogExport(new Factory(_catalog)));
        var rows = await export.GetCsvRowsAsync(ct: Ct);
        Assert.Equal(cards.Count, rows.Count);
        Assert.Equal(Path.Combine(moved, "Adventures", "Known Text.pdf"), rows.Single(r => r[5] == "Friday game")[7]);
        Assert.Equal("Winter campaign", rows.Single(r => r[5] == "Friday game")[6]);
        Assert.Equal(Path.Combine(maps, "Harbor Map.png"), rows.Single(r => r[0] == "Harbor Map")[7]);
        Assert.Null(rows.Single(r => r[0] == "Boxed Set of the Marches")[7]);
        var json = Path.Combine(_dir, "library.json");
        await export.WriteJsonAsync(json, Ct);
        using var exported = JsonDocument.Parse(await File.ReadAllBytesAsync(json, Ct));
        Assert.Equal(before.Keys.Order(StringComparer.Ordinal), exported.RootElement.GetProperty("tables").EnumerateObject().Select(t => t.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_backup_with_its_index_is_searchable_at_once_after_a_restore_and_a_folder_left_offline_can_be_pointed_to_its_place_later()
    {
        Copy(pdfs.KnownText, "Known Text.pdf");
        var maps = Directory.CreateDirectory(Path.Combine(_dir, "Map Library")).FullName;
        File.WriteAllBytes(Path.Combine(maps, "Harbor Map.png"), FakeCodec.Png(800, 600));
        await _roots.AddAsync(_library, Ct);
        var second = (await _roots.AddAsync(maps, Ct)).Id;
        await _service.StartAsync(Ct);
        await SettleAsync();
        var backup = Path.Combine(_dir, "backup.zip");
        await _service.StopAsync(Ct);
        using (var backups = new BackupService(_paths, _catalog, _index, _settings))
        {
            await backups.SetIncludeIndexAsync(true, Ct);
            await backups.BackUpAsync(backup, Ct);
        }
        var jobs = await JobsAsync();
        await CloseAsync();

        // On the other computer the library folder is where it was, and the maps are somewhere the user finds later.
        var later = Path.Combine(_dir, "Found later", "Map Library");
        Directory.CreateDirectory(Path.GetDirectoryName(later)!);
        Directory.Move(maps, later);
        _dataFolder = "other computer";
        var other = new AppPaths(Path.Combine(_dir, _dataFolder));
        foreach (var directory in other.Directories) Directory.CreateDirectory(directory);
        using (var restoring = new BackupService(other, new CatalogDatabase(other.CatalogDatabase, other.Backups), IndexDatabase.ForFile(other.IndexDatabase), _settings))
        {
            var preview = await restoring.PreviewRestoreAsync(backup, Ct);
            Assert.True(preview.IncludesIndex);
            Assert.Equal([true, false], preview.Folders.Select(f => f.Found));
            Assert.Null(await restoring.StageRestoreAsync(preview, new Dictionary<long, string?>(), Ct));
        }
        SqliteConnection.ClearAllPools();
        Assert.True((await PendingRestore.ApplyIfRequestedAsync(other, ct: Ct))!.Restored);

        await InitializeAsync();
        await _service.StartAsync(Ct);
        await NextScanAsync(second, null);
        var search = new LibraryQueries(_index);
        var filter = new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
        Assert.Single((await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("owlbear")), filter, ct: Ct)).Entries);
        var harbor = (await search.ListAsync(new LibraryFilter(), ct: Ct)).Single(c => c.Title == "Harbor Map");
        Assert.Equal(EntryAvailability.Offline, (await _libraryStore.GetUnavailableAsync(Ct)).GetValueOrDefault(harbor.EntryId));
        // Nothing was read again: the index came with the backup.
        Assert.Equal(jobs, await JobsAsync());

        // Settings > Library > Point to its new place, as 4g offers for an offline folder.
        Assert.Equal(RootRelocation.Relocated, await _service.RelocateRootAsync(second, later, Ct));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);
        while ((await _libraryStore.GetUnavailableAsync(timeout.Token)).ContainsKey(harbor.EntryId)) await Task.Delay(100, timeout.Token);
        Assert.Equal(jobs, await JobsAsync());
    }
}
