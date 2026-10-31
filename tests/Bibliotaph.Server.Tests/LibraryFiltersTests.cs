using Bibliotaph.Api.Contracts;
using Bibliotaph.Web.Services;

namespace Bibliotaph.Server.Tests;

/// <summary>The web Library's filter chips and how the toolbar counts them.</summary>
public sealed class LibraryFiltersTests
{
    static readonly LibraryMenus Menus = new(
        Systems: [new FilterChoice("5e", "Fifth edition (12)")], Types: [new FilterChoice("Adventure", "Adventure (4)")], Styles: [], Durations: [],
        Folders: [], Owns: [], Stores: [], ShowStyle: false, ShowDuration: false, ShowOwn: false, ShowStore: false, ShowAi: false, ShowCopies: false,
        ShowAdventures: false, ShowCollections: false, Boxes: [], Found: [], ShowFound: false);

    [Fact]
    public void A_chip_shows_its_choice_without_the_count()
    {
        var chip = Assert.Single(LibraryFilters.Chips(new LibraryQuery { System = "5e" }, Menus));
        Assert.Equal("Fifth edition", chip.Label);
        Assert.Equal("Turn off the game system filter", chip.Name);
        Assert.Null(chip.Clear(new LibraryQuery { System = "5e" }).System);
    }

    [Fact]
    public void Common_filters_come_first_and_the_rest_fold_under_more_filters()
    {
        var chips = LibraryFilters.Chips(new LibraryQuery { Kind = "Elsewhere", Type = "Adventure", Level = 3, UnknownLevels = true }, Menus);
        Assert.Equal(["Adventure", "Level 3 or unknown", "Owned elsewhere"], chips.Select(c => c.Label));
        Assert.Equal("Filters · 3", LibraryFilters.FiltersLabel(chips));
        Assert.Equal("More filters · 1", LibraryFilters.MoreFiltersLabel(shown: false, chips));
        Assert.Equal("Fewer filters", LibraryFilters.MoreFiltersLabel(shown: true, chips));
    }

    [Fact]
    public void No_filters_on_reads_plain_labels()
    {
        var chips = LibraryFilters.Chips(new LibraryQuery(), Menus);
        Assert.Empty(chips);
        Assert.Equal("Filters", LibraryFilters.FiltersLabel(chips));
        Assert.Equal("More filters", LibraryFilters.MoreFiltersLabel(shown: false, chips));
    }

    [Fact]
    public void Queries_that_show_the_same_books_compare_equal()
    {
        var saved = new LibraryQuery { Q = "dragon", Sort = "Title" };
        var typed = new LibraryQuery { Q = " dragon ", Sort = "Relevance", UnknownLevels = true };
        Assert.Equal(LibraryFilters.Comparable(saved), LibraryFilters.Comparable(typed));
        Assert.NotEqual(LibraryFilters.Comparable(saved), LibraryFilters.Comparable(typed with { Level = 2 }));
    }
}
