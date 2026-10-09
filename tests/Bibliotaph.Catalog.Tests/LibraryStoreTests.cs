using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

public sealed class LibraryStoreTests : IAsyncLifetime
{
    static readonly DateTime Monday = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-library-").FullName;
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    SourceRootStore _roots = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _roots = new SourceRootStore(_contexts);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static ScannedFile File(string path, long size = 1000, bool onlineOnly = false, DateTime? modified = null) =>
        new(path, size, modified ?? Monday, onlineOnly);

    static ContentHash Hash(char c) => ContentHash.Parse(new string(c, 64));

    async Task<long> RootAsync(string name = "Library") => (await _roots.AddAsync(Path.Combine(_dir, name), Ct)).Id;

    async Task<FileLocation[]> LocationsAsync()
    {
        await using var db = _contexts.CreateDbContext();
        return await db.FileLocations.AsNoTracking().OrderBy(f => f.RelativePath).ToArrayAsync(Ct);
    }

    [Fact]
    public async Task Reconciling_adds_new_files_keeps_unchanged_ones_and_marks_the_rest_missing()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf"), File("c.pdf")], ct: Ct);
        var hashed = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith("a.pdf", StringComparison.Ordinal));
        await _library.AttachHashAsync(hashed, Hash('a'), Ct);

        var result = await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf", size: 2000), File("d.pdf", onlineOnly: true)], ct: Ct);

        Assert.Equal(new ReconcileResult(Added: 1, Changed: 1, Unchanged: 1, Missing: 1), result);
        var locations = await LocationsAsync();
        Assert.Equal(["a.pdf", "b.pdf", "c.pdf", "d.pdf"], locations.Select(l => l.RelativePath));
        Assert.Equal(new string('a', 64), locations[0].ContentHash);
        Assert.Equal(
            [FileLocationState.Present, FileLocationState.Present, FileLocationState.Missing, FileLocationState.OnlineOnly],
            locations.Select(l => l.State));
        Assert.Equal(new LibraryCounts(Files: 3, OnlineOnly: 1, Missing: 1, Unhashed: 2, Documents: 1, UnhashedOnlineOnly: 1, UnhashedOnlineOnlyBytes: 1000),
            await _library.GetCountsAsync(Ct));
    }

    [Fact]
    public async Task Hashing_reads_local_files_first_and_small_files_before_large_ones()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("big.pdf", 9000), File("cloud.pdf", 10, onlineOnly: true), File("small.png", 10)], ct: Ct);

        var all = await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct);
        var localOnly = await _library.NextUnhashedAsync(10, includeOnlineOnly: false, Ct);

        Assert.Equal(["small.png", "big.pdf", "cloud.pdf"], all.Select(f => Path.GetFileName(f.FullPath)));
        Assert.Equal(["png", "pdf", "pdf"], all.Select(f => f.Format));
        Assert.Equal(["small.png", "big.pdf"], localOnly.Select(f => Path.GetFileName(f.FullPath)));
    }

    [Fact]
    public async Task Two_copies_of_the_same_content_are_one_document()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("Core/book.pdf"), File("Backup/book copy.pdf")], ct: Ct);
        var files = await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct);

        var first = await _library.AttachHashAsync(files[0], Hash('b'), Ct);
        var second = await _library.AttachHashAsync(files[1], Hash('b'), Ct);

        Assert.True(first?.IsNew);
        Assert.False(second?.IsNew);
        Assert.Equal(first?.DocumentId, second?.DocumentId);
        // A new document is shown by one whole-document entry; the copy adds no other.
        var entries = new EntryStore(_contexts);
        var entry = await entries.GetEntryAsync(first!.Value.DocumentId, Ct);
        Assert.NotNull(entry);
        Assert.Equal(new string('b', 64), entry.ContentHash);
        Assert.Equal([new EntryDocument(entry.EntryId, first.Value.DocumentId, EntryKind.Whole)], await entries.GetCurrentAsync(ct: Ct));
        Assert.Equal(first.Value.DocumentId, await entries.GetCurrentDocumentAsync(entry.EntryId, Ct));
        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Equal(new LibraryCounts(Files: 2, OnlineOnly: 0, Missing: 0, Unhashed: 0, Documents: 1), await _library.GetCountsAsync(Ct));
    }

    [Fact]
    public async Task A_file_that_changed_while_it_was_hashed_is_not_attached()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("map.png")], ct: Ct);
        var file = (await _library.NextUnhashedAsync(1, includeOnlineOnly: true, Ct)).Single();
        await _library.ReconcileRootAsync(root, [File("map.png", modified: Monday.AddHours(1))], ct: Ct);

        Assert.Null(await _library.AttachHashAsync(file, Hash('c'), Ct));
    }

    [Fact]
    public async Task A_document_is_read_from_a_local_copy_before_an_online_only_one()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File(Path.Combine("Cloud", "book.pdf"), onlineOnly: true), File(Path.Combine("Setting", "Maps", "book.pdf"), size: 1000)], ct: Ct);
        long documentId = 0;
        foreach (var file in await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct))
            documentId = (await _library.AttachHashAsync(file, Hash('d'), Ct))!.Value.DocumentId;

        var source = await _library.GetSourceAsync(documentId, Ct);

        Assert.NotNull(source);
        Assert.Equal(Path.Combine(_dir, "Library", "Setting", "Maps", "book.pdf"), source.FullPath);
        Assert.Equal("Setting / Maps", source.FolderHint);
        Assert.Equal(2, source.AllPaths.Count);
    }

    [Fact]
    public async Task An_offline_root_keeps_its_files_but_offers_none_to_read_or_hash()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf")], ct: Ct);
        var file = (await _library.NextUnhashedAsync(1, includeOnlineOnly: true, Ct)).Single();
        var documentId = (await _library.AttachHashAsync(file, Hash('e'), Ct))!.Value.DocumentId;

        await _library.SetRootAvailabilityAsync(root, SourceRootAvailability.Offline, Ct);

        Assert.Null(await _library.GetSourceAsync(documentId, Ct));
        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Equal(0, (await _library.GetCountsAsync(Ct)).Missing);

        // A completed scan means the root is reachable again.
        await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf")], ct: Ct);
        Assert.NotNull(await _library.GetSourceAsync(documentId, Ct));
    }

    [Fact]
    public async Task Files_in_a_folder_the_scan_could_not_open_are_not_marked_missing()
    {
        var root = await RootAsync();
        var locked = Path.Combine("Locked", "Sub");
        await _library.ReconcileRootAsync(root, [File("a.pdf"), File(Path.Combine(locked, "b.pdf")), File(Path.Combine("Locked Out", "c.pdf"))], ct: Ct);

        var result = await _library.ReconcileRootAsync(root, [File("a.pdf")], [locked], Ct);

        Assert.Equal(1, result.Missing); // only the folder next to it, whose name merely starts the same
        var missing = Assert.Single(await LocationsAsync(), l => l.State == FileLocationState.Missing);
        Assert.Equal(Path.Combine("Locked Out", "c.pdf"), missing.RelativePath);
        Assert.Equal(0, (await _library.ReconcileRootAsync(root, [], ["."], Ct)).Missing);
    }

    [Fact]
    public async Task Readable_documents_are_those_with_a_file_that_can_be_read_now()
    {
        var here = await RootAsync("Here");
        var away = await RootAsync("Away");
        await _library.ReconcileRootAsync(here, [File("present.pdf"), File("gone.pdf")], ct: Ct);
        await _library.ReconcileRootAsync(away, [File("offline.pdf")], ct: Ct);
        var ids = new Dictionary<string, long>();
        var hash = 'a';
        foreach (var file in await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct))
            ids[Path.GetFileName(file.FullPath)] = (await _library.AttachHashAsync(file, Hash(hash++), Ct))!.Value.DocumentId;
        await _library.ReconcileRootAsync(here, [File("present.pdf")], ct: Ct);
        await _library.SetRootAvailabilityAsync(away, SourceRootAvailability.Offline, Ct);

        Assert.Equal([ids["present.pdf"]], await _library.GetReadableAsync([.. ids.Values, 999], Ct));
        Assert.Empty(await _library.GetReadableAsync([], Ct));
    }

    [Fact]
    public async Task The_library_shows_entries_with_documents_in_folders_still_in_use()
    {
        var kept = await RootAsync("Kept");
        var removed = await RootAsync("Removed");
        await _library.ReconcileRootAsync(kept, [File("shared.pdf"), File("gone.pdf"), File("unhashed.pdf")], ct: Ct);
        await _library.ReconcileRootAsync(removed, [File("shared copy.pdf"), File("only here.pdf")], ct: Ct);
        var ids = new Dictionary<string, long>();
        foreach (var file in await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct))
        {
            var name = Path.GetFileName(file.FullPath);
            if (name == "unhashed.pdf") continue;
            var hash = Hash(name switch { "shared.pdf" or "shared copy.pdf" => 'a', "gone.pdf" => 'b', _ => 'c' });
            ids[name] = (await _library.AttachHashAsync(file, hash, Ct))!.Value.DocumentId;
        }
        await _library.ReconcileRootAsync(kept, [File("shared.pdf"), File("unhashed.pdf")], ct: Ct); // gone.pdf goes missing
        await _roots.RemoveAsync(removed, Ct);

        var shared = (await new EntryStore(_contexts).GetEntryAsync(ids["shared.pdf"], Ct))!.EntryId;
        Assert.Equal([shared], await _library.GetVisibleEntryIdsAsync(ct: Ct));
        Assert.Equal([shared], await _library.GetVisibleEntryIdsAsync(kept, Ct));
        Assert.Empty(await _library.GetVisibleEntryIdsAsync(removed, Ct));

        var locations = await _library.GetLocationsAsync(ids["shared.pdf"], Ct);
        Assert.Equal([Path.Combine(_dir, "Kept", "shared.pdf")], locations.Select(l => l.FullPath));
    }
}
