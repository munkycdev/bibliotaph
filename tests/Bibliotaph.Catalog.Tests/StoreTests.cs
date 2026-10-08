using Bibliotaph.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

public sealed class StoreTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-stores-").FullName;
    CatalogDatabase _database = null!;
    Factory _contexts = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Settings_round_trip_and_overwrite()
    {
        var settings = new SettingsStore(_contexts);
        Assert.Null(await settings.GetAsync(SettingKeys.Appearance, TestContext.Current.CancellationToken));

        await settings.SetAsync(SettingKeys.Appearance, nameof(ThemePreference.Dark), TestContext.Current.CancellationToken);
        await settings.SetAsync(SettingKeys.Appearance, nameof(ThemePreference.Light), TestContext.Current.CancellationToken);

        Assert.Equal("Light", await settings.GetAsync(SettingKeys.Appearance, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Adding_the_same_folder_twice_keeps_one_root()
    {
        var roots = new SourceRootStore(_contexts);
        var folder = Path.Combine(_dir, "RPG Library");

        var first = await roots.AddAsync(folder, TestContext.Current.CancellationToken);
        var second = await roots.AddAsync(folder + Path.DirectorySeparatorChar, TestContext.Current.CancellationToken);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await roots.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Removing_a_root_hides_it_and_adding_it_again_brings_the_same_row_back()
    {
        var roots = new SourceRootStore(_contexts);
        var added = await roots.AddAsync(Path.Combine(_dir, "Adventures"), TestContext.Current.CancellationToken);

        await roots.RemoveAsync(added.Id, TestContext.Current.CancellationToken);
        Assert.Empty(await roots.ListAsync(TestContext.Current.CancellationToken));

        var again = await roots.AddAsync(Path.Combine(_dir, "Adventures"), TestContext.Current.CancellationToken);
        Assert.Equal(added.Id, again.Id);
        Assert.Equal(SourceRootAvailability.Online, again.Availability);
    }

    sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
    }
}
