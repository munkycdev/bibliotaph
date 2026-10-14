using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Index;

namespace Bibliotaph.App.Tests;

public class SmartViewDefinitionTests
{
    [Fact]
    public void A_saved_view_reads_back_exactly_as_it_was_saved()
    {
        IReadOnlyList<string> settings = ["eberron", "greyhawk"];
        var view = new SmartViewDefinition("dragon -map", Scope: "collection:4", OnlyAddedHere: true, Kind: KindFilter.Books, Level: 3, IncludeUnknownLevels: true,
            Sort: LibrarySort.Title, Layout: LibraryLayout.List, Tab: ResultsTab.Pages, Players: 4, Collections: CollectionFilter.NotInOne,
            Picked: PickedValues.Of([KeyValuePair.Create("setting", settings)]), Adventures: AdventureCards.Cards);

        Assert.Equal(view, SmartViewDefinition.Parse(view.ToJson()));
    }

    [Fact]
    public void Choices_are_stored_by_name_so_a_reordered_list_in_a_later_app_reads_them_the_same()
    {
        var json = new SmartViewDefinition(Kind: KindFilter.Packs, Sort: LibrarySort.Publisher).ToJson();

        Assert.Contains("\"kind\":\"Packs\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sort\":\"Publisher\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_view_that_cannot_be_read_shows_the_whole_library()
    {
        Assert.Equal(new SmartViewDefinition(), SmartViewDefinition.Parse("{\"query\": "));
    }

    [Fact]
    public void A_view_saved_before_a_filter_existed_reads_with_that_filter_off()
    {
        var view = SmartViewDefinition.Parse("{\"version\":2,\"query\":\"heist\",\"kind\":\"Books\"}");

        Assert.Equal(new SmartViewDefinition("heist", Kind: KindFilter.Books), view);
    }

    [Fact]
    public void An_older_view_that_hid_adventures_keeps_hiding_them()
    {
        var view = SmartViewDefinition.Parse("{\"version\":1,\"query\":\"heist\",\"hideParts\":true}");

        Assert.Equal(AdventureCards.Hidden, view.Adventures);
        Assert.False(view.HideParts);
        Assert.Equal(SmartViewDefinition.CurrentVersion, view.Version);
    }
}
