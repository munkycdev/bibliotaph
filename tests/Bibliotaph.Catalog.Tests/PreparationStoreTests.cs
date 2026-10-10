using Bibliotaph.Core;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Slice 3a: hearts on books, and when each book was opened and where its reader left it.</summary>
public sealed class PreparationStoreTests : IAsyncLifetime
{
    static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-preparation-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(Monday);
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    FavoriteStore _favorites = null!;
    ReadingStore _reading = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _entries = new EntryStore(_contexts);
        _favorites = new FavoriteStore(_contexts, _clock);
        _reading = new ReadingStore(_contexts, _entries, _clock);
        _root = (await new SourceRootStore(_contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    readonly List<ScannedFile> _files = [];

    async Task<(long Document, EntryId Entry)> AddBookAsync(string path, char hash)
    {
        _files.Add(new ScannedFile(path, 1000, Monday.UtcDateTime, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        var document = (await _library.AttachHashAsync(file, ContentHash.Parse(new string(hash, 64)), Ct))!.Value.DocumentId;
        return (document, (await _entries.GetEntryAsync(document, Ct))!.EntryId);
    }

    [Fact]
    public async Task Hearts_are_set_and_cleared_and_only_changes_are_reported()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var (_, tomb) = await AddBookAsync("tomb.pdf", 'b');

        Assert.Equal([abbey], await _favorites.SetAsync([abbey, abbey, new EntryId(9999)], favorite: true, Ct));
        Assert.Equal([tomb], await _favorites.SetAsync([abbey, tomb], favorite: true, Ct));
        Assert.True(await _favorites.IsFavoriteAsync(abbey, Ct));
        Assert.Equal([abbey, tomb], (await _favorites.GetAsync(ct: Ct)).OrderBy(e => e.Value));
        Assert.Equal([tomb], await _favorites.GetAsync([tomb], Ct));

        Assert.Equal([abbey], await _favorites.SetAsync([abbey], favorite: false, Ct));
        Assert.Empty(await _favorites.SetAsync([abbey], favorite: false, Ct));
        Assert.False(await _favorites.IsFavoriteAsync(abbey, Ct));
    }

    [Fact]
    public async Task An_open_is_recorded_on_its_card_and_the_page_is_kept_for_that_copy_only()
    {
        var (abbeyDoc, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var (tombDoc, tomb) = await AddBookAsync("tomb.pdf", 'b');

        Assert.Equal(abbey, await _reading.RecordOpenAsync(abbeyDoc, Ct));
        await _reading.SavePositionAsync(abbeyDoc, 41, Ct);
        _clock.Step();
        Assert.Equal(tomb, await _reading.RecordOpenAsync(tombDoc, Ct));

        Assert.Equal(41, await _reading.GetPositionAsync(abbeyDoc, Ct));
        Assert.Equal(0, await _reading.GetPositionAsync(tombDoc, Ct));
        var opened = await _reading.GetOpenedAsync(ct: Ct);
        Assert.True(opened[tomb] > opened[abbey]);
        Assert.Equal([abbey], (await _reading.GetOpenedAsync([abbey], Ct)).Keys);

        // Reopening keeps the page; a document the catalog doesn't know is no card at all.
        await _reading.RecordOpenAsync(abbeyDoc, Ct);
        Assert.Equal(41, await _reading.GetPositionAsync(abbeyDoc, Ct));
        Assert.Null(await _reading.RecordOpenAsync(9999, Ct));
        Assert.Equal(0, await _reading.GetPositionAsync(9999, Ct));
    }

    [Fact]
    public async Task A_joined_copy_brings_its_heart_and_place_to_a_card_without_them_and_a_split_takes_them_back()
    {
        var (coreDoc, core) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backupDoc, backup) = await AddBookAsync("Backup/abbey.pdf", 'b');
        await _favorites.SetAsync([backup], favorite: true, Ct);
        await _reading.RecordOpenAsync(backupDoc, Ct);
        await _reading.SavePositionAsync(backupDoc, 12, Ct);

        Assert.Equal(new CopyJoin(core, backup), await _entries.JoinAsCopyAsync(coreDoc, backupDoc, Ct));

        Assert.True(await _favorites.IsFavoriteAsync(core, Ct));
        Assert.False(await _favorites.IsFavoriteAsync(backup, Ct));
        // The card now opens the core file, so the backup's page is not where it opens.
        Assert.Equal([core], (await _reading.GetOpenedAsync(ct: Ct)).Keys);
        Assert.Equal(0, await _reading.GetPositionAsync(coreDoc, Ct));

        Assert.Equal(backup, await _entries.SplitCopyAsync(core, backupDoc, ct: Ct));

        Assert.True(await _favorites.IsFavoriteAsync(backup, Ct));
        Assert.False(await _favorites.IsFavoriteAsync(core, Ct));
        Assert.Equal(12, await _reading.GetPositionAsync(backupDoc, Ct));
    }

    [Fact]
    public async Task A_card_that_has_its_own_heart_keeps_it_when_a_copy_joins()
    {
        var (coreDoc, core) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backupDoc, backup) = await AddBookAsync("Backup/abbey.pdf", 'b');
        await _favorites.SetAsync([core, backup], favorite: true, Ct);
        await _reading.RecordOpenAsync(coreDoc, Ct);
        await _reading.SavePositionAsync(coreDoc, 5, Ct);
        await _reading.RecordOpenAsync(backupDoc, Ct);

        await _entries.JoinAsCopyAsync(coreDoc, backupDoc, Ct);

        Assert.True(await _favorites.IsFavoriteAsync(core, Ct));
        Assert.Equal(5, await _reading.GetPositionAsync(coreDoc, Ct));
        // The joined card's own marks wait on it, out of sight, for Not the same book.
        Assert.True(await _favorites.IsFavoriteAsync(backup, Ct));
    }

    [Fact]
    public async Task Removing_a_favorite_book_owned_elsewhere_and_undoing_brings_its_heart_back()
    {
        var printed = await _entries.AddElsewhereAsync(Ct);
        await _favorites.SetAsync([printed], favorite: true, Ct);

        var removed = await _entries.RemoveElsewhereAsync(printed, Ct);
        Assert.True(removed!.Favorite);
        Assert.Empty(await _favorites.GetAsync(ct: Ct));

        var restored = await _entries.RestoreElsewhereAsync(removed, Ct);
        Assert.True(await _favorites.IsFavoriteAsync(restored, Ct));
    }
}
