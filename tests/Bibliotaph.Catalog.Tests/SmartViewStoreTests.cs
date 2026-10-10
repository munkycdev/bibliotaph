using Microsoft.Data.Sqlite;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Slice 3e: saved Smart Views, each a name and a definition the app writes.</summary>
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
        SqliteConnection.ClearAllPools();
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
}
