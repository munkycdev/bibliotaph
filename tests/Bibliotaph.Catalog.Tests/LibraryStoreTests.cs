using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

public sealed partial class LibraryStoreTests : IAsyncLifetime
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

        var result = await _library.ReconcileRootAsync(root, [File("a.pdf")], [locked], ct: Ct);

        Assert.Equal(1, result.Missing); // only the folder next to it, whose name merely starts the same
        var missing = Assert.Single(await LocationsAsync(), l => l.State == FileLocationState.Missing);
        Assert.Equal(Path.Combine("Locked Out", "c.pdf"), missing.RelativePath);
        Assert.Equal(0, (await _library.ReconcileRootAsync(root, [], ["."], ct: Ct)).Missing);
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
    public async Task The_library_shows_entries_with_documents_in_folders_still_in_use_and_keeps_those_whose_file_went_missing()
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

        var entries = new EntryStore(_contexts);
        var shared = (await entries.GetEntryAsync(ids["shared.pdf"], Ct))!.EntryId;
        var gone = (await entries.GetEntryAsync(ids["gone.pdf"], Ct))!.EntryId;
        // The book whose file went stays, marked missing (slice 4g plan, choice 1); the one only in the removed folder goes.
        Assert.Equal([shared, gone], (await _library.GetVisibleEntryIdsAsync(ct: Ct)).OrderBy(e => e.Value));
        Assert.Equal([shared, gone], (await _library.GetVisibleEntryIdsAsync(kept, Ct)).OrderBy(e => e.Value));
        Assert.Empty(await _library.GetVisibleEntryIdsAsync(removed, Ct));
        Assert.Equal(new Dictionary<EntryId, EntryAvailability> { [gone] = EntryAvailability.Missing }, await _library.GetUnavailableAsync(Ct));

        var locations = await _library.GetLocationsAsync(ids["shared.pdf"], Ct);
        Assert.Equal([Path.Combine(_dir, "Kept", "shared.pdf")], locations.Select(l => l.FullPath));
    }

    static ArchiveMember Member(string entry, uint crc, long size = 500, string? problem = null) => new(entry, size, Monday, crc, problem);

    [Fact]
    public async Task A_ZIPs_files_are_locations_that_keep_their_hash_until_they_change()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("Bundle.zip"), File("loose.pdf")], ct: Ct);
        var zip = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.IsArchive);
        Assert.Equal(Path.Combine(_dir, "Library", "Bundle.zip"), zip.FullPath);

        var pending = (await _library.ReconcileArchiveAsync(zip, [Member("Book.pdf", 1), Member("Maps/Harbor.png", 2), Member("Extras.zip", 3, problem: "nested")], Ct))!;
        Assert.Equal([("Book.pdf", "pdf"), ("Maps/Harbor.png", "png")], pending.Select(f => (f.EntryPath!, f.Format)));
        Assert.Equal(Path.Combine(_dir, "Library", "Bundle.zip", "Maps", "Harbor.png"), pending[1].FullPath);
        var book = (await _library.AttachHashAsync(pending[0], Hash('a'), Ct))!.Value.DocumentId;
        var map = (await _library.AttachHashAsync(pending[1], Hash('b'), Ct))!.Value.DocumentId;
        Assert.True(await _library.AttachArchiveHashAsync(zip, Hash('f'), Ct));

        // Read: nothing left to hash but the loose file, and the ZIP inside it waits in Files needing attention.
        Assert.Equal(["loose.pdf"], (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Select(f => Path.GetFileName(f.FullPath)));
        var counts = await _library.GetCountsAsync(Ct);
        Assert.Equal((1, 1), (counts.Unhashed, counts.Problems));
        Assert.Equal(["nested"], (await _library.GetProblemsAsync(Ct)).Select(p => p.Problem));
        var source = (await _library.GetSourceAsync(map, Ct))!;
        Assert.Equal((Path.Combine(_dir, "Library", "Bundle.zip"), "Maps/Harbor.png", "Bundle / Maps"),
            (source.ArchivePath, source.EntryPath, source.FolderHint));
        Assert.Equal(Path.Combine(_dir, "Library", "Bundle.zip"), Assert.Single(await _library.GetLocationsAsync(map, Ct)).ExplorerPath);

        // Saved again: the book unchanged, the map replaced, a handout added.
        await _library.ReconcileRootAsync(root, [File("Bundle.zip", size: 2000), File("loose.pdf")], ct: Ct);
        zip = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.IsArchive);
        pending = (await _library.ReconcileArchiveAsync(zip, [Member("Book.pdf", 1), Member("Maps/Harbor.png", 9), Member("Handout.jpg", 4)], Ct))!;
        Assert.Equal(["Maps/Harbor.png", "Handout.jpg"], pending.Select(f => f.EntryPath!));
        var locations = await LocationsAsync();
        Assert.Equal(book, locations.Single(l => l.EntryPath == "Book.pdf").DocumentId);
        var harbor = locations.Single(l => l.EntryPath == "Maps/Harbor.png");
        Assert.Equal((null, map), (harbor.DocumentId, harbor.PreviousDocumentId));
        Assert.Equal(FileLocationState.Missing, locations.Single(l => l.EntryPath == "Extras.zip").State);
    }

    [Fact]
    public async Task A_ZIPs_files_go_missing_with_it_and_it_is_read_again_when_it_comes_back()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("Bundle.zip")], ct: Ct);
        var zip = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single();
        var member = Assert.Single((await _library.ReconcileArchiveAsync(zip, [Member("Book.pdf", 1)], Ct))!);
        var book = (await _library.AttachHashAsync(member, Hash('a'), Ct))!.Value.DocumentId;
        await _library.AttachArchiveHashAsync(zip, Hash('f'), Ct);

        await _library.ReconcileRootAsync(root, [], ct: Ct);
        Assert.All(await LocationsAsync(), l => Assert.Equal(FileLocationState.Missing, l.State));
        Assert.Null(await _library.GetSourceAsync(book, Ct));

        // Back as it was: listed again before its files count as there.
        await _library.ReconcileRootAsync(root, [File("Bundle.zip")], ct: Ct);
        zip = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single();
        Assert.True(zip.IsArchive);
        Assert.Null(await _library.GetSourceAsync(book, Ct));
        Assert.Empty((await _library.ReconcileArchiveAsync(zip, [Member("Book.pdf", 1)], Ct))!);
        Assert.Equal("Book.pdf", (await _library.GetSourceAsync(book, Ct))!.EntryPath);

        // Online-only: its files are too.
        await _library.ReconcileRootAsync(root, [File("Bundle.zip", onlineOnly: true)], ct: Ct);
        Assert.All(await LocationsAsync(), l => Assert.Equal(FileLocationState.OnlineOnly, l.State));
    }

    [Fact]
    public async Task A_ZIP_that_changed_while_it_was_read_is_left_for_the_next_scan()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("Bundle.zip")], ct: Ct);
        var zip = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single();
        await _library.ReconcileRootAsync(root, [File("Bundle.zip", size: 5)], ct: Ct);

        Assert.Null(await _library.ReconcileArchiveAsync(zip, [Member("Book.pdf", 1)], Ct));
        Assert.False(await _library.AttachArchiveHashAsync(zip, Hash('f'), Ct));
        Assert.Single(await LocationsAsync());
    }
}
