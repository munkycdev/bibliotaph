using Bibliotaph.Core;
using Bibliotaph.Web.Services;

namespace Bibliotaph.Server.Tests;

/// <summary>A Summary figure on the web opens the Library's address with the filters the Windows app opens it with.</summary>
public sealed class SummaryLinksTests
{
    [Theory]
    [InlineData("system", "fantasy", "library?system=fantasy")]
    [InlineData("type", "adventure", "library?type=adventure")]
    [InlineData("own", "print", "library?own=print")]
    [InlineData("store", "shop", "library?store=shop")]
    [InlineData("setting", "isles", "library?picked=%7B%22setting%22%3A%5B%22isles%22%5D%7D")]
    public void A_value_opens_the_library_filtered_to_it(string field, string value, string href) =>
        Assert.Equal(href, SummaryLinks.Href(new SummaryLink(Filter: new SummaryFilter(field, value))));

    [Fact]
    public void Other_figures_open_their_filters_their_search_or_their_page()
    {
        Assert.Equal("library", SummaryLinks.Href(SummaryPage.Everything));
        Assert.Equal("library?q=opened%3Ano", SummaryLinks.Href(SummaryPage.NeverOpened));
        Assert.Equal("library?folder=4", SummaryLinks.Href(new SummaryLink(Filter: new SummaryFilter(Folder: 4))));
        Assert.Equal("library?level=3&adventures=Cards", SummaryLinks.Href(new SummaryLink(Filter: new SummaryFilter(Level: 3, AdventureCards: true))));
        Assert.Equal("library?kind=Packs", SummaryLinks.Href(new SummaryLink(Filter: new SummaryFilter(Kind: "Packs"))));
        Assert.Equal("library?ai=Read", SummaryLinks.Href(new SummaryLink(Filter: new SummaryFilter(ReadByAi: true))));
        Assert.Equal("library?copies=true", SummaryLinks.Href(new SummaryLink(Filter: new SummaryFilter(Copies: true))));
        Assert.Equal("collections", SummaryLinks.Href(new SummaryLink(Goal: SummaryGoal.Collections)));
        Assert.Equal("binders", SummaryLinks.Href(new SummaryLink(Goal: SummaryGoal.Binders)));
    }

    [Theory]
    [InlineData("OneDrive · Dana › RPG › Maps", "Maps")]
    [InlineData("OneDrive · Dana", "OneDrive · Dana")]
    public void A_folder_is_named_after_the_last_part_of_where_it_is(string where, string name) => Assert.Equal(name, SummaryLinks.FolderName(where));
}
