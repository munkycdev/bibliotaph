using Bibliotaph.Core;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Slice 3b: collections, nested to any depth, holding entries.</summary>
public sealed class CollectionStoreTests : IAsyncLifetime
{
    static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-collections-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(Monday);
    readonly List<ScannedFile> _files = [];
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    CollectionStore _collections = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _entries = new EntryStore(_contexts);
        _collections = new CollectionStore(_contexts, _clock);
        _root = (await new SourceRootStore(_contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    async Task<(long Document, EntryId Entry)> AddBookAsync(string path, char hash)
    {
        _files.Add(new ScannedFile(path, 1000, Monday.UtcDateTime, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        var document = (await _library.AttachHashAsync(file, ContentHash.Parse(new string(hash, 64)), Ct))!.Value.DocumentId;
        return (document, (await _entries.GetEntryAsync(document, Ct))!.EntryId);
    }

    async Task<HashSet<string>> ScopesAsync(EntryId entry) =>
        [.. (await _collections.GetScopesAsync([entry], Ct)).Select(s => s.Scope)];

    [Fact]
    public async Task Books_are_added_and_removed_and_only_changes_are_reported()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var (_, tomb) = await AddBookAsync("tomb.pdf", 'b');
        var campaign = await _collections.CreateAsync("  Winter campaign ", description: " The long night ", ct: Ct);
        Assert.Equal("Winter campaign", campaign.Name);
        Assert.Equal("The long night", campaign.Description);

        Assert.Equal([abbey], await _collections.AddAsync(campaign.Id, [abbey, abbey, new EntryId(9999)], Ct));
        Assert.Equal([tomb], await _collections.AddAsync(campaign.Id, [abbey, tomb], Ct));
        Assert.Equal(2, (await _collections.ListAsync(Ct)).Single().OwnCount);
        Assert.Equal([campaign.Id], await _collections.GetForEntryAsync(abbey, Ct));

        Assert.Equal([abbey], await _collections.RemoveAsync(campaign.Id, [abbey], Ct));
        Assert.Empty(await _collections.RemoveAsync(campaign.Id, [abbey], Ct));
        Assert.Empty(await _collections.GetForEntryAsync(abbey, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _collections.CreateAsync(" ", ct: Ct));
    }

    [Fact]
    public async Task A_book_in_a_sub_collection_is_in_every_collection_above_it()
    {
        var (_, map) = await AddBookAsync("map.png", 'a');
        var campaign = await _collections.CreateAsync("Campaign", ct: Ct);
        var maps = await _collections.CreateAsync("Maps", campaign.Id, ct: Ct);
        var city = await _collections.CreateAsync("City", maps.Id, ct: Ct);
        await _collections.AddAsync(city.Id, [map], Ct);

        Assert.Equivalent(
            new[] { ScopeKeys.Collection(campaign.Id), ScopeKeys.Collection(maps.Id), ScopeKeys.Collection(city.Id), ScopeKeys.CollectionOwn(city.Id) },
            await ScopesAsync(map), strict: true);
        Assert.Equal(4, (await _collections.GetScopesAsync(ct: Ct)).Count);
    }

    [Fact]
    public async Task A_move_changes_the_collections_above_and_never_goes_inside_itself()
    {
        var (_, map) = await AddBookAsync("map.png", 'a');
        var campaign = await _collections.CreateAsync("Campaign", ct: Ct);
        var other = await _collections.CreateAsync("Other", ct: Ct);
        var maps = await _collections.CreateAsync("Maps", campaign.Id, ct: Ct);
        await _collections.AddAsync(maps.Id, [map], Ct);

        Assert.Equal([map], await _collections.MoveAsync(maps.Id, other.Id, Ct));
        Assert.Contains(ScopeKeys.Collection(other.Id), await ScopesAsync(map));
        Assert.DoesNotContain(ScopeKeys.Collection(campaign.Id), await ScopesAsync(map));
        Assert.Empty(await _collections.MoveAsync(maps.Id, other.Id, Ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _collections.MoveAsync(other.Id, maps.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _collections.MoveAsync(other.Id, other.Id, Ct));
        Assert.Equal([map], await _collections.MoveAsync(maps.Id, null, Ct));
        Assert.Null((await _collections.ListAsync(Ct)).Single(c => c.Id == maps.Id).ParentId);
    }

    [Fact]
    public async Task Deleting_moves_sub_collections_up_keeps_the_books_and_undo_puts_it_all_back()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var (_, map) = await AddBookAsync("map.png", 'b');
        var campaign = await _collections.CreateAsync("Campaign", ct: Ct);
        var session = await _collections.CreateAsync("Session one", campaign.Id, ct: Ct);
        var maps = await _collections.CreateAsync("Maps", session.Id, ct: Ct);
        await _collections.AddAsync(session.Id, [abbey], Ct);
        await _collections.AddAsync(maps.Id, [map], Ct);
        await _collections.SetPinnedAsync(session.Id, true, Ct);

        var (deleted, changed) = await _collections.DeleteAsync(session.Id, Ct);
        Assert.Equal([abbey, map], changed.OrderBy(e => e.Value));
        Assert.NotNull(deleted);
        Assert.Equal(campaign.Id, (await _collections.ListAsync(Ct)).Single(c => c.Id == maps.Id).ParentId);
        Assert.Empty(await ScopesAsync(abbey));
        Assert.Contains(ScopeKeys.Collection(campaign.Id), await ScopesAsync(map));

        Assert.Equal([abbey, map], (await _collections.RestoreAsync(deleted, Ct)).OrderBy(e => e.Value));
        var restored = (await _collections.ListAsync(Ct)).Single(c => c.Id == session.Id);
        Assert.True(restored.Pinned);
        Assert.Equal(campaign.Id, restored.ParentId);
        Assert.Equal(session.Id, (await _collections.ListAsync(Ct)).Single(c => c.Id == maps.Id).ParentId);
        Assert.Contains(ScopeKeys.CollectionOwn(session.Id), await ScopesAsync(abbey));
    }

    [Fact]
    public async Task A_joined_copy_brings_its_collections_to_the_card_and_a_split_takes_them_back()
    {
        var (coreDoc, core) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backupDoc, backup) = await AddBookAsync("Backup/abbey.pdf", 'b');
        var both = await _collections.CreateAsync("Both", ct: Ct);
        var only = await _collections.CreateAsync("Only the backup", ct: Ct);
        await _collections.AddAsync(both.Id, [core, backup], Ct);
        await _collections.AddAsync(only.Id, [backup], Ct);

        await _entries.JoinAsCopyAsync(coreDoc, backupDoc, Ct);
        Assert.Equal([both.Id, only.Id], (await _collections.GetForEntryAsync(core, Ct)).Order());
        // Its membership of a collection the card is in already waits on it, out of sight.
        Assert.Equal([both.Id], await _collections.GetForEntryAsync(backup, Ct));

        Assert.Equal(backup, await _entries.SplitCopyAsync(core, backupDoc, ct: Ct));
        Assert.Equal([both.Id], await _collections.GetForEntryAsync(core, Ct));
        Assert.Equal([both.Id, only.Id], (await _collections.GetForEntryAsync(backup, Ct)).Order());
    }

    [Fact]
    public async Task Removing_a_book_owned_elsewhere_and_undoing_puts_it_back_in_its_collections()
    {
        var printed = await _entries.AddElsewhereAsync(Ct);
        var campaign = await _collections.CreateAsync("Campaign", ct: Ct);
        await _collections.AddAsync(campaign.Id, [printed], Ct);

        var removed = await _entries.RemoveElsewhereAsync(printed, Ct);
        Assert.Equal([campaign.Id], removed!.Collections);
        Assert.Equal(0, (await _collections.ListAsync(Ct)).Single().OwnCount);

        var restored = await _entries.RestoreElsewhereAsync(removed, Ct);
        Assert.Equal([campaign.Id], await _collections.GetForEntryAsync(restored, Ct));
    }
}
