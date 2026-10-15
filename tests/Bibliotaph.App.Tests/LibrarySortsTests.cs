using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.App.Tests;

/// <summary>
/// The Library's remembered orders, as far as they go without a library: what a saved value reads as, and which
/// picks are kept. Writing them to catalog.db is the smoke test's.
/// </summary>
public class LibrarySortsTests
{
    sealed class NoCatalog : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => throw new InvalidOperationException("No library here.");
    }

    readonly LibrarySorts _sorts = new(new SettingsStore(new NoCatalog()), NullLogger<LibrarySorts>.Instance);

    [Theory]
    [InlineData("Title", false, LibrarySort.Title)]
    [InlineData("RecentlyOpened", true, LibrarySort.RecentlyOpened)]
    [InlineData("Relevance", true, LibrarySort.Relevance)]
    [InlineData("Relevance", false, null)]
    [InlineData("Shelf", false, null)]
    [InlineData("42", true, null)]
    [InlineData(null, true, null)]
    public void A_saved_order_reads_back_unless_it_means_nothing_there(string? saved, bool searching, LibrarySort? expected) =>
        Assert.Equal(expected, LibrarySorts.Parse(saved, searching));

    [Fact]
    public void It_starts_with_the_usual_orders()
    {
        Assert.Equal(LibrarySort.RecentlyAdded, _sorts.For(searching: false));
        Assert.Equal(LibrarySort.Relevance, _sorts.For(searching: true));
    }

    [Fact]
    public async Task Browsing_and_searching_each_keep_their_own_pick_even_when_it_cant_be_saved()
    {
        await _sorts.RememberAsync(searching: false, LibrarySort.Title);
        await _sorts.RememberAsync(searching: true, LibrarySort.RecentlyOpened);

        Assert.Equal(LibrarySort.Title, _sorts.Browse);
        Assert.Equal(LibrarySort.RecentlyOpened, _sorts.Search);
    }

    [Fact]
    public async Task Best_match_is_never_kept_for_browsing()
    {
        await _sorts.RememberAsync(searching: false, LibrarySort.Publisher);
        await _sorts.RememberAsync(searching: false, LibrarySort.Relevance);

        Assert.Equal(LibrarySort.Publisher, _sorts.Browse);
    }

    [Fact]
    public async Task Initialize_keeps_the_usual_orders_when_the_settings_cant_be_read()
    {
        await _sorts.InitializeAsync();

        Assert.Equal(LibrarySort.RecentlyAdded, _sorts.Browse);
        Assert.Equal(LibrarySort.Relevance, _sorts.Search);
    }
}
