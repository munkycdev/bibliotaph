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
        await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf"), File("c.pdf")], Ct);
        var hashed = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith("a.pdf", StringComparison.Ordinal));
        await _library.AttachHashAsync(hashed, Hash('a'), Ct);

        var result = await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf", size: 2000), File("d.pdf", onlineOnly: true)], Ct);

        Assert.Equal(new ReconcileResult(Added: 1, Changed: 1, Unchanged: 1, Missing: 1), result);
        var locations = await LocationsAsync();
        Assert.Equal(["a.pdf", "b.pdf", "c.pdf", "d.pdf"], locations.Select(l => l.RelativePath));
        Assert.Equal(new string('a', 64), locations[0].ContentHash);
        Assert.Equal(
            [FileLocationState.Present, FileLocationState.Present, FileLocationState.Missing, FileLocationState.OnlineOnly],
            locations.Select(l => l.State));
    }

    [Fact]
    public async Task Hashing_reads_local_files_first_and_small_files_before_large_ones()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("big.pdf", 9000), File("cloud.pdf", 10, onlineOnly: true), File("small.png", 10)], Ct);

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
        await _library.ReconcileRootAsync(root, [File("Core/book.pdf"), File("Backup/book copy.pdf")], Ct);
        var files = await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct);

        var first = await _library.AttachHashAsync(files[0], Hash('b'), Ct);
        var second = await _library.AttachHashAsync(files[1], Hash('b'), Ct);

        Assert.True(first?.IsNew);
        Assert.False(second?.IsNew);
        Assert.Equal(first?.DocumentId, second?.DocumentId);
        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Equal(new LibraryCounts(Files: 2, OnlineOnly: 0, Missing: 0, Unhashed: 0, Documents: 1), await _library.GetCountsAsync(Ct));
    }

    [Fact]
    public async Task A_file_that_changed_while_it_was_hashed_is_not_attached()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("map.png")], Ct);
        var file = (await _library.NextUnhashedAsync(1, includeOnlineOnly: true, Ct)).Single();
        await _library.ReconcileRootAsync(root, [File("map.png", modified: Monday.AddHours(1))], Ct);

        Assert.Null(await _library.AttachHashAsync(file, Hash('c'), Ct));
    }

    [Fact]
    public async Task A_document_is_read_from_a_local_copy_before_an_online_only_one()
    {
        var root = await RootAsync();
        await _library.ReconcileRootAsync(root, [File("Cloud/book.pdf", onlineOnly: true), File("Setting/Maps/book.pdf", size: 1000)], Ct);
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
        await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf")], Ct);
        var file = (await _library.NextUnhashedAsync(1, includeOnlineOnly: true, Ct)).Single();
        var documentId = (await _library.AttachHashAsync(file, Hash('e'), Ct))!.Value.DocumentId;

        await _library.SetRootAvailabilityAsync(root, SourceRootAvailability.Offline, Ct);

        Assert.Null(await _library.GetSourceAsync(documentId, Ct));
        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Equal(0, (await _library.GetCountsAsync(Ct)).Missing);

        // A completed scan means the root is reachable again.
        await _library.ReconcileRootAsync(root, [File("a.pdf"), File("b.pdf")], Ct);
        Assert.NotNull(await _library.GetSourceAsync(documentId, Ct));
    }
}
