using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Pinned Views: one ordered list of pins of every kind, which follow their targets through delete, Undo and Same book.</summary>
public sealed class PinStoreTests : IAsyncLifetime
{
    static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-pins-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(Monday);
    readonly List<ScannedFile> _files = [];
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    PinStore _pins = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _entries = new EntryStore(_contexts);
        _pins = new PinStore(_contexts);
        _root = (await new SourceRootStore(_contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
        // A new catalog starts with Favorites pinned; the tests below start from none.
        Assert.Equal([PinTarget.Favorites], (await _pins.ListAsync(Ct)).Select(p => p.Target));
        await _pins.UnpinAsync(PinTarget.Favorites, Ct);
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
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

    async Task<List<PinTarget>> TargetsAsync() => [.. (await _pins.ListAsync(Ct)).Select(p => p.Target)];

    [Fact]
    public async Task New_pins_go_to_the_bottom_and_each_target_is_pinned_once()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var collection = await new CollectionStore(_contexts, _clock).CreateAsync("Campaign", ct: Ct);

        Assert.True(await _pins.PinAsync(PinTarget.Entry(abbey), Ct));
        Assert.True(await _pins.PinAsync(PinTarget.Favorites, Ct));
        Assert.True(await _pins.PinAsync(PinTarget.Collection(collection.Id), Ct));
        Assert.False(await _pins.PinAsync(PinTarget.Favorites, Ct));
        Assert.False(await _pins.PinAsync(PinTarget.Entry(abbey), Ct));
        Assert.Equal([PinTarget.Entry(abbey), PinTarget.Favorites, PinTarget.Collection(collection.Id)], await TargetsAsync());

        Assert.Equal(1, await _pins.UnpinAsync(PinTarget.Favorites, Ct));
        Assert.Null(await _pins.UnpinAsync(PinTarget.Favorites, Ct));
        Assert.True(await _pins.PinAsync(PinTarget.Favorites, Ct));
        Assert.Equal([PinTarget.Entry(abbey), PinTarget.Collection(collection.Id), PinTarget.Favorites], await TargetsAsync());
    }

    [Fact]
    public async Task A_pin_moves_to_any_place_and_the_others_close_up_around_it()
    {
        var sessions = new SessionStore(_contexts, _clock);
        var binders = new List<PinTarget>();
        for (var i = 1; i <= 4; i++)
        {
            var binder = await sessions.CreateAsync($"Session {i}", ct: Ct);
            binders.Add(PinTarget.Binder(binder.Id));
            await _pins.PinAsync(binders[^1], Ct);
        }
        var (one, two, three, four) = (binders[0], binders[1], binders[2], binders[3]);

        Assert.True(await _pins.MoveAsync(one, 2, Ct));
        Assert.Equal([two, three, one, four], await TargetsAsync());
        Assert.True(await _pins.MoveAsync(four, 0, Ct));
        Assert.Equal([four, two, three, one], await TargetsAsync());
        // Past either end it stops there; where it is already, or not pinned, nothing moves.
        Assert.True(await _pins.MoveAsync(four, 99, Ct));
        Assert.Equal([two, three, one, four], await TargetsAsync());
        Assert.False(await _pins.MoveAsync(two, -1, Ct));
        Assert.False(await _pins.MoveAsync(PinTarget.Favorites, 0, Ct));
        Assert.Equal([0, 1, 2, 3], (await _pins.ListAsync(Ct)).Select(p => p.Position));

        // New pins still go to the bottom, and Undo's place counts in the new order.
        await _pins.PinAsync(PinTarget.Favorites, Ct);
        Assert.Equal([two, three, one, four, PinTarget.Favorites], await TargetsAsync());
        var deleted = (await sessions.DeleteAsync(one.Id!.Value, Ct))!;
        var restored = await sessions.RestoreAsync(deleted, Ct);
        Assert.Equal([two, three, PinTarget.Binder(restored.Id), four, PinTarget.Favorites], await TargetsAsync());
    }

    [Fact]
    public async Task The_database_holds_one_Favorites_pin_and_refuses_a_pin_that_points_at_the_wrong_kind()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');
        await using (var db = _database.CreateContext())
        {
            db.Pins.AddRange(new Pin { Kind = PinKind.Favorites, Position = 0 }, new Pin { Kind = PinKind.Favorites, Position = 1 });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
        await using (var db = _database.CreateContext())
        {
            db.Pins.Add(new Pin { Kind = PinKind.Collection, Position = 0, EntryId = abbey.Value });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
        await using (var db = _database.CreateContext())
        {
            db.Pins.Add(new Pin { Kind = PinKind.Favorites, Position = 0, EntryId = abbey.Value });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task A_removed_book_takes_its_pin_and_undo_puts_it_back_in_its_place()
    {
        var printed = await _entries.AddElsewhereAsync(Ct);
        await _pins.PinAsync(PinTarget.Favorites, Ct);
        await _pins.PinAsync(PinTarget.Entry(printed), Ct);
        var collection = await new CollectionStore(_contexts, _clock).CreateAsync("Campaign", ct: Ct);
        await _pins.PinAsync(PinTarget.Collection(collection.Id), Ct);

        var removed = (await _entries.RemoveElsewhereAsync(printed, Ct))!;
        Assert.Equal(1, removed.PinPosition);
        Assert.Equal([PinTarget.Favorites, PinTarget.Collection(collection.Id)], await TargetsAsync());

        var restored = await _entries.RestoreElsewhereAsync(removed, Ct);
        Assert.Equal([PinTarget.Favorites, PinTarget.Entry(restored), PinTarget.Collection(collection.Id)], await TargetsAsync());
    }

    [Fact]
    public async Task A_deleted_binder_takes_its_pin_and_undo_puts_it_back_in_its_place()
    {
        var sessions = new SessionStore(_contexts, _clock);
        var first = await sessions.CreateAsync("Session 1", ct: Ct);
        var second = await sessions.CreateAsync("Session 2", ct: Ct);
        await _pins.PinAsync(PinTarget.Binder(first.Id), Ct);
        await _pins.PinAsync(PinTarget.Binder(second.Id), Ct);

        var deleted = (await sessions.DeleteAsync(first.Id, Ct))!;
        Assert.Equal([PinTarget.Binder(second.Id)], await TargetsAsync());

        var restored = await sessions.RestoreAsync(deleted, Ct);
        Assert.Equal([PinTarget.Binder(restored.Id), PinTarget.Binder(second.Id)], await TargetsAsync());
        // A duplicate is a new binder, not pinned.
        var copy = (await sessions.DuplicateAsync(second.Id, "Session 3", Ct))!;
        Assert.DoesNotContain(PinTarget.Binder(copy.Id), await TargetsAsync());
    }

    [Fact]
    public async Task Same_book_moves_the_pin_to_the_card_kept_and_not_the_same_book_brings_it_back()
    {
        var (_, original) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (revised, update) = await AddBookAsync("Updates/abbey v2.pdf", 'b');
        await _pins.PinAsync(PinTarget.Favorites, Ct);
        await _pins.PinAsync(PinTarget.Entry(update), Ct);

        await _entries.JoinAsOneBookAsync([original, update], revised, Ct);
        Assert.Equal([PinTarget.Favorites, PinTarget.Entry(original)], await TargetsAsync());

        Assert.Equal(update, await _entries.SplitCopyAsync(original, revised, ct: Ct));
        Assert.Equal([PinTarget.Favorites, PinTarget.Entry(update)], await TargetsAsync());
    }

    [Fact]
    public async Task Same_book_keeps_the_kept_cards_own_pin_and_leaves_the_others_behind_for_not_the_same_book()
    {
        var (_, original) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (revised, update) = await AddBookAsync("Updates/abbey v2.pdf", 'b');
        await _pins.PinAsync(PinTarget.Entry(original), Ct);
        await _pins.PinAsync(PinTarget.Entry(update), Ct);

        await _entries.JoinAsOneBookAsync([original, update], revised, Ct);
        Assert.Equal([PinTarget.Entry(original), PinTarget.Entry(update)], await TargetsAsync());

        await _entries.SplitCopyAsync(original, revised, ct: Ct);
        Assert.Equal([PinTarget.Entry(original), PinTarget.Entry(update)], await TargetsAsync());
    }
}
