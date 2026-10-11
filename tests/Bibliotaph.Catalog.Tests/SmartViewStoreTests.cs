
namespace Bibliotaph.Catalog.Tests;

/// <summary>Slice 3e: saved searches (Smart Views in the code), each a name and a definition the app writes.</summary>
public sealed class SmartViewStoreTests : IAsyncLifetime
{
    static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-views-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(Monday);
    CatalogDatabase _database = null!;
    SmartViewStore _views = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _views = new SmartViewStore(new Factory(_database), _clock);
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Views_list_by_name_with_their_definitions_as_written()
    {
        var winter = await _views.CreateAsync("  winter maps ", """{"query":"type:map"}""", Ct);
        await _views.CreateAsync("Haunted places", """{"query":"tag:haunted"}""", Ct);

        var all = await _views.ListAsync(Ct);

        Assert.Equal(["Haunted places", "winter maps"], all.Select(v => v.Name));
        Assert.Equal("""{"query":"type:map"}""", (await _views.GetAsync(winter.Id, Ct))!.Definition);
    }

    [Fact]
    public async Task A_view_needs_a_name()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _views.CreateAsync("   ", "{}", Ct));
        var view = await _views.CreateAsync("Maps", "{}", Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => _views.RenameAsync(view.Id, "", Ct));
    }

    [Fact]
    public async Task Update_and_rename_change_only_what_they_say()
    {
        var view = await _views.CreateAsync("Maps", """{"query":"type:map"}""", Ct);

        Assert.True(await _views.UpdateAsync(view.Id, """{"query":"type:map system:5e"}""", Ct));
        Assert.True(await _views.RenameAsync(view.Id, " Fifth edition maps ", Ct));
        Assert.False(await _views.UpdateAsync(view.Id + 1, "{}", Ct));

        var after = (await _views.GetAsync(view.Id, Ct))!;
        Assert.Equal("Fifth edition maps", after.Name);
        Assert.Equal("""{"query":"type:map system:5e"}""", after.Definition);
        Assert.Equal(view.CreatedUtc, after.CreatedUtc);
        Assert.True(after.UpdatedUtc > view.UpdatedUtc);
    }

    [Fact]
    public async Task Undo_after_delete_brings_the_view_back_with_its_id()
    {
        var view = await _views.CreateAsync("Maps", """{"query":"type:map"}""", Ct);

        var deleted = await _views.DeleteAsync(view.Id, Ct);
        Assert.Empty(await _views.ListAsync(Ct));
        Assert.Null(await _views.DeleteAsync(view.Id, Ct));

        var back = await _views.RestoreAsync(deleted!, Ct);
        Assert.Equal(view, back);
        Assert.Equal(view, (await _views.ListAsync(Ct)).Single());
    }

    [Fact]
    public async Task A_new_search_is_pinned_at_the_bottom_and_undo_puts_its_pin_back_where_it_was()
    {
        var pins = new PinStore(new Factory(_database));
        await pins.UnpinAsync(PinTarget.Favorites, Ct);
        var maps = await _views.CreateAsync("Maps", "{}", Ct);
        var haunted = await _views.CreateAsync("Haunted places", "{}", Ct);
        Assert.Equal([PinTarget.SavedSearch(maps.Id), PinTarget.SavedSearch(haunted.Id)], (await pins.ListAsync(Ct)).Select(p => p.Target));

        var deleted = (await _views.DeleteAsync(maps.Id, Ct))!;
        Assert.Equal(0, deleted.PinPosition);
        Assert.Equal([PinTarget.SavedSearch(haunted.Id)], (await pins.ListAsync(Ct)).Select(p => p.Target));

        await _views.RestoreAsync(deleted, Ct);
        Assert.Equal([PinTarget.SavedSearch(maps.Id), PinTarget.SavedSearch(haunted.Id)], (await pins.ListAsync(Ct)).Select(p => p.Target));

        // Unpinned, it stays unpinned through delete and undo.
        await pins.UnpinAsync(PinTarget.SavedSearch(haunted.Id), Ct);
        await _views.RestoreAsync((await _views.DeleteAsync(haunted.Id, Ct))!, Ct);
        Assert.Equal([PinTarget.SavedSearch(maps.Id)], (await pins.ListAsync(Ct)).Select(p => p.Target));
    }
}
