using Bibliotaph.Core;

namespace Bibliotaph.Catalog.Tests;

// Moves and offline folders (slice 4g): a file found at a new path by its file ID keeps its location, hash and card.
public sealed partial class LibraryStoreTests
{
    /// <summary>File IDs as the scanner's caller would read them, by relative path, recording which paths were asked about.</summary>
    sealed class FileIds(Dictionary<string, string> ids)
    {
        public List<string> Asked { get; } = [];

        public string? Read(string path)
        {
            Asked.Add(path);
            return ids.GetValueOrDefault(path);
        }
    }

    async Task<long> HashedAsync(long root, string path, char hash, string? fileId = null)
    {
        await _library.ReconcileRootAsync(root, [File(path)], fileId: fileId is null ? null : _ => fileId, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        return (await _library.AttachHashAsync(file, Hash(hash), Ct))!.Value.DocumentId;
    }

    [Fact]
    public async Task A_file_renamed_or_moved_within_its_folder_keeps_its_location_and_hash_and_isnt_read_again()
    {
        var root = await RootAsync();
        var book = await HashedAsync(root, "Book.pdf", 'a', fileId: "V:1");
        var before = Assert.Single(await LocationsAsync());
        var entry = Assert.Single(await _library.GetVisibleEntryIdsAsync(ct: Ct));

        var ids = new FileIds(new() { [Path.Combine("Adventures", "Renamed.pdf")] = "V:1" });
        var result = await _library.ReconcileRootAsync(root, [File(Path.Combine("Adventures", "Renamed.pdf"))], fileId: ids.Read, ct: Ct);

        Assert.Equal(new ReconcileResult(Added: 0, Changed: 0, Unchanged: 0, Missing: 0, Moved: 1), result);
        var after = Assert.Single(await LocationsAsync());
        Assert.Equal((before.Id, book, Hash('a').Hex, FileLocationState.Present), (after.Id, after.DocumentId, after.ContentHash, after.State));
        Assert.Equal(Path.Combine("Adventures", "Renamed.pdf"), after.RelativePath);
        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Equal([entry], await _library.GetVisibleEntryIdsAsync(ct: Ct));
        Assert.Empty(await _library.GetUnavailableAsync(Ct));
        // Only the new path was asked about.
        Assert.Equal([Path.Combine("Adventures", "Renamed.pdf")], ids.Asked);
    }

    [Fact]
    public async Task File_ids_are_read_for_new_and_changed_paths_and_once_for_a_location_without_one()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("old.pdf")], ct: Ct); // indexed before file IDs were kept
        var ids = new FileIds(new() { ["old.pdf"] = "V:1", ["new.pdf"] = "V:2", ["saved.pdf"] = "V:3" });

        await _library.ReconcileRootAsync(root, [File("old.pdf"), File("new.pdf"), File("saved.pdf")], fileId: ids.Read, ct: Ct);
        await _library.ReconcileRootAsync(root, [File("old.pdf"), File("new.pdf"), File("saved.pdf", size: 5)], fileId: ids.Read, ct: Ct);

        Assert.Equal(["old.pdf", "new.pdf", "saved.pdf", "saved.pdf"], ids.Asked);
        Assert.Equal(["V:2", "V:1", "V:3"], (await LocationsAsync()).Select(l => l.NtfsFileId));
    }

    [Fact]
    public async Task A_file_moved_to_another_library_folder_keeps_its_location_whichever_folder_is_scanned_first()
    {
        var here = await RootAsync("Here");
        var there = await RootAsync("There");
        var first = await HashedAsync(here, "First.pdf", 'a', fileId: "V:1");
        await _library.ReconcileRootAsync(here, [File("First.pdf"), File("Second.pdf")], fileId: p => p == "First.pdf" ? "V:1" : "V:2", ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single();
        var second = (await _library.AttachHashAsync(file, Hash('b'), Ct))!.Value.DocumentId;
        var locations = (await LocationsAsync()).ToDictionary(l => l.DocumentId!.Value, l => l.Id);
        var ids = new Dictionary<string, string> { ["First.pdf"] = "V:1", ["Second.pdf"] = "V:2" };

        // The folder it left first: missing for a moment, its card still in the library.
        await _library.ReconcileRootAsync(here, [File("Second.pdf")], fileId: ids.GetValueOrDefault, ct: Ct);
        Assert.Equal(EntryAvailability.Missing, Assert.Single(await _library.GetUnavailableAsync(Ct)).Value);
        Assert.Equal(2, (await _library.GetVisibleEntryIdsAsync(ct: Ct)).Count);
        var moved = await _library.ReconcileRootAsync(there, [File("First.pdf")], fileId: ids.GetValueOrDefault, ct: Ct);
        Assert.Equal((0, 1), (moved.Added, moved.Moved));

        // The folder it went to first: it is new there until the folder it left is scanned, which the scan asks for.
        var arrived = await _library.ReconcileRootAsync(there, [File("First.pdf"), File("Second.pdf")], fileId: ids.GetValueOrDefault, ct: Ct);
        Assert.Equal([here], arrived.MovedFrom);
        Assert.Equal(1, (await _library.ReconcileRootAsync(here, [], fileId: ids.GetValueOrDefault, ct: Ct)).Moved);

        var after = await LocationsAsync();
        Assert.Equal(2, after.Length);
        Assert.All(after, l => Assert.Equal((there, FileLocationState.Present), (l.SourceRootId, l.State)));
        Assert.Equal(locations, after.ToDictionary(l => l.DocumentId!.Value, l => l.Id));
        Assert.Equal([first, second], after.OrderBy(l => l.RelativePath).Select(l => l.DocumentId!.Value));
        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Empty(await _library.GetUnavailableAsync(Ct));
    }

    [Fact]
    public async Task A_file_moved_to_another_drive_is_hashed_and_then_its_missing_location_is_forgotten()
    {
        var here = await RootAsync("Here");
        var there = await RootAsync("There");
        var book = await HashedAsync(here, "Book.pdf", 'a', fileId: "C:1");
        var entry = Assert.Single(await _library.GetVisibleEntryIdsAsync(ct: Ct));

        // A copy has an ID of its own, so it is read like any new file.
        await _library.ReconcileRootAsync(here, [], fileId: _ => null, ct: Ct);
        await _library.ReconcileRootAsync(there, [File("Book.pdf")], fileId: _ => "D:7", ct: Ct);
        Assert.Equal([entry], await _library.GetVisibleEntryIdsAsync(ct: Ct));
        var file = Assert.Single(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Equal(book, (await _library.AttachHashAsync(file, Hash('a'), Ct))!.Value.DocumentId);

        var location = Assert.Single(await LocationsAsync());
        Assert.Equal((there, FileLocationState.Present), (location.SourceRootId, location.State));
        Assert.Equal([entry], await _library.GetVisibleEntryIdsAsync(ct: Ct));
        Assert.Empty(await _library.GetUnavailableAsync(Ct));
    }

    [Fact]
    public async Task A_missing_location_in_a_folder_that_is_offline_is_kept_when_its_content_turns_up_elsewhere()
    {
        var away = await RootAsync("Away");
        var here = await RootAsync("Here");
        await HashedAsync(away, "Book.pdf", 'a');
        await _library.ReconcileRootAsync(away, [File("Other.pdf")], ct: Ct); // went missing before its disk went
        await _library.SetRootAvailabilityAsync(away, SourceRootAvailability.Offline, Ct);

        await HashedAsync(here, "Book.pdf", 'a');

        Assert.Equal([(away, FileLocationState.Missing), (here, FileLocationState.Present)],
            (await LocationsAsync()).Where(l => l.ContentHash is not null).OrderBy(l => l.SourceRootId).Select(l => (l.SourceRootId, l.State)));
    }

    [Fact]
    public async Task A_renamed_ZIP_keeps_its_files_and_their_hashes()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("Bundle.zip")], fileId: _ => "V:9", ct: Ct);
        var zip = Assert.Single(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        var member = Assert.Single((await _library.ReconcileArchiveAsync(zip, [Member("Maps/Harbor.png", 1)], Ct))!);
        var map = (await _library.AttachHashAsync(member, Hash('a'), Ct))!.Value.DocumentId;
        await _library.AttachArchiveHashAsync(zip, Hash('f'), Ct);

        Assert.Equal(1, (await _library.ReconcileRootAsync(root, [File("Harbor Maps.zip")], fileId: _ => "V:9", ct: Ct)).Moved);

        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        var source = (await _library.GetSourceAsync(map, Ct))!;
        Assert.Equal((Path.Combine(_dir, "Library", "Harbor Maps.zip"), "Maps/Harbor.png"), (source.ArchivePath, source.EntryPath));
        Assert.Equal(Path.Combine(_dir, "Library", "Harbor Maps.zip", "Maps", "Harbor.png"), source.FullPath);
    }

    [Fact]
    public async Task A_book_is_offline_while_its_folder_is_and_missing_once_every_file_of_it_is_gone()
    {
        var away = await RootAsync("Away");
        var here = await RootAsync("Here");
        await _library.ReconcileRootAsync(away, [File("Offline.pdf"), File("Copied copy.pdf")], ct: Ct);
        await _library.ReconcileRootAsync(here, [File("Copied.pdf"), File("Gone.pdf")], ct: Ct);
        var documents = new Dictionary<string, long>();
        foreach (var file in await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct))
        {
            var name = Path.GetFileName(file.FullPath);
            documents[name] = (await _library.AttachHashAsync(file, Hash(name switch { "Offline.pdf" => 'a', "Gone.pdf" => 'c', _ => 'b' }), Ct))!.Value.DocumentId;
        }
        await _library.ReconcileRootAsync(here, [File("Copied.pdf")], ct: Ct);
        await _library.SetRootAvailabilityAsync(away, SourceRootAvailability.Offline, Ct);

        // The book copied to both folders opens from the one still here.
        var entries = await new EntryStore(_contexts).GetEntriesAsync([documents["Offline.pdf"], documents["Gone.pdf"]], Ct);
        Assert.Equal(
            new Dictionary<EntryId, EntryAvailability>
            {
                [entries[documents["Offline.pdf"]].EntryId] = EntryAvailability.Offline,
                [entries[documents["Gone.pdf"]].EntryId] = EntryAvailability.Missing,
            },
            await _library.GetUnavailableAsync(Ct));
        Assert.Equal(3, (await _library.GetVisibleEntryIdsAsync(ct: Ct)).Count);
        Assert.Equal(1, (await _library.GetCountsAsync(Ct)).OfflineFolders);
    }

    [Fact]
    public async Task A_folders_volume_serial_is_kept_from_its_first_scan()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("a.pdf")], volumeSerial: "1234ABCD", ct: Ct);
        await _library.ReconcileRootAsync(root, [File("a.pdf")], volumeSerial: "99999999", ct: Ct);

        Assert.Equal("1234ABCD", Assert.Single(await _roots.ListAsync(Ct)).VolumeSerial);
    }

    [Fact]
    public async Task A_folder_pointed_to_its_new_place_keeps_its_files_and_their_hashes()
    {
        var root = await RootAsync("Old Disk");
        var removed = await RootAsync("Removed");
        await _roots.RemoveAsync(removed, Ct);
        var book = await HashedAsync(root, Path.Combine("Adventures", "Book.pdf"), 'a', fileId: "C:1");
        await _library.ReconcileRootAsync(root, [File(Path.Combine("Adventures", "Book.pdf"))], volumeSerial: "1234ABCD", ct: Ct);
        await _library.SetRootAvailabilityAsync(root, SourceRootAvailability.Offline, Ct);

        Assert.False(await _roots.RelocateAsync(root, Path.Combine(_dir, "Removed"), Ct));
        Assert.True(await _roots.RelocateAsync(root, Path.Combine(_dir, "New Disk") + Path.DirectorySeparatorChar, Ct));

        var moved = Assert.Single(await _roots.ListAsync(Ct));
        Assert.Equal((Path.Combine(_dir, "New Disk"), null, SourceRootAvailability.Offline), (moved.Path, moved.VolumeSerial, moved.Availability));
        Assert.Null(Assert.Single(await LocationsAsync()).NtfsFileId);
        // Its first scan there matches its files by path: nothing to read, and the book is back.
        var result = await _library.ReconcileRootAsync(root, [File(Path.Combine("Adventures", "Book.pdf"))], fileId: _ => "D:5", volumeSerial: "5678EF01", ct: Ct);
        Assert.Equal(1, result.Unchanged);
        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Equal(Path.Combine(_dir, "New Disk", "Adventures", "Book.pdf"), (await _library.GetSourceAsync(book, Ct))!.FullPath);
        var scanned = Assert.Single(await _roots.ListAsync(Ct));
        Assert.Equal(("5678EF01", SourceRootAvailability.Online), (scanned.VolumeSerial, scanned.Availability));
        Assert.Equal("D:5", Assert.Single(await LocationsAsync()).NtfsFileId);
    }
}
