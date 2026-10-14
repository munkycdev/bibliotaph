using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.App.Tests;

/// <summary>The Library's filters: a saved search's filters put back and kept again, the chips, and when the Library is told to refresh.</summary>
public class LibraryFilterStateTests
{
    readonly LibraryFilterState _filters = new();
    int _changes;

    public LibraryFilterStateTests() => _filters.Changed += (_, _) => _changes++;

    /// <summary>A saved search with every filter on, as the Library would have saved it.</summary>
    static SmartViewDefinition Everything()
    {
        IReadOnlyList<string> setting = ["the-marches"];
        return new(Kind: KindFilter.Books, Folder: 7, System: "dnd5e", Type: "adventure", Level: 3, IncludeUnknownLevels: true, Ai: AiFilter.Read,
            Copies: true, Own: "print", Style: "heist", Duration: "one-shot", Players: 4, Store: "itch-io", Collections: CollectionFilter.InOne,
            Picked: PickedValues.Of([KeyValuePair.Create(MetadataFields.Settings.Key, setting)]), Adventures: AdventureCards.Cards);
    }

    [Fact]
    public void A_saved_search_put_back_is_kept_again_as_it_was()
    {
        var view = Everything();

        _filters.Apply(view);
        _filters.ShowFolders([new Choice<long?>(7, "Maps"), new Choice<long?>(9, "Handouts")]);

        Assert.Equal(view, _filters.ToView());
        Assert.Equal(7, _filters.FolderChoice.Value);
        Assert.Equal(["the-marches"], _filters.ChipFilters.Single(b => b.Field == MetadataFields.Settings).Values);
    }

    [Fact]
    public void Putting_a_saved_search_back_does_not_ask_for_a_refresh_for_each_filter()
    {
        _filters.Apply(Everything());
        _filters.ShowFolders([new Choice<long?>(7, "Maps")]);

        Assert.Equal(0, _changes);
    }

    [Fact]
    public void A_counted_value_from_a_saved_search_shows_by_itself_until_the_counts_arrive()
    {
        _filters.Apply(new SmartViewDefinition(System: "dnd5e"));

        Assert.Equal(new Choice<string?>("dnd5e", "dnd5e"), _filters.SystemChoice);
        Assert.Contains(_filters.SystemChoice, _filters.SystemChoices);
    }

    [Fact]
    public void Include_unknown_levels_is_kept_only_with_a_level()
    {
        _filters.IncludeUnknownLevels = true;

        Assert.False(_filters.ToView().IncludeUnknownLevels);
        Assert.Equal(0, _changes);

        _filters.LevelChoice = _filters.LevelChoices.Single(c => c.Value == 3);
        _filters.IncludeUnknownLevels = false;
        _filters.IncludeUnknownLevels = true;

        Assert.True(_filters.ToView().IncludeUnknownLevels);
        Assert.Equal(3, _changes);
    }

    [Fact]
    public void Changing_a_filter_asks_for_one_refresh()
    {
        _filters.KindChoice = _filters.KindChoices.Single(c => c.Value == KindFilter.Packs);

        Assert.Equal(1, _changes);
        Assert.True(_filters.IsFiltering);
    }

    [Fact]
    public void A_menu_that_selects_nothing_falls_back_to_its_first_choice()
    {
        _filters.Apply(new SmartViewDefinition(System: "dnd5e"));

        _filters.SystemChoice = null!;

        Assert.Equal("All systems", _filters.SystemChoice.Label);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void Clear_filters_turns_every_filter_off_and_asks_for_one_refresh()
    {
        _filters.Apply(Everything());
        _filters.ShowFolders([new Choice<long?>(7, "Maps")]);

        _filters.ClearFiltersCommand.Execute(null);

        Assert.Equal(new SmartViewDefinition(), _filters.ToView());
        Assert.False(_filters.IsFiltering);
        Assert.Equal(1, _changes);
    }

    [Fact]
    public void The_chips_show_the_common_filters_first_and_count_those_folded_away()
    {
        _filters.Apply(new SmartViewDefinition(Kind: KindFilter.Books, Level: 3, IncludeUnknownLevels: true, Players: 4, Copies: true));

        Assert.Equal(["Level 3 or unknown", "4 players", "Books", "Books with copies"], _filters.Chips().Select(c => c.Label));

        _filters.ShowChips();

        Assert.Equal("Filters · 4", _filters.FiltersLabel);
        Assert.Equal("More filters · 2", _filters.MoreFiltersLabel);
        Assert.True(_filters.HasSeveralFilters);
    }

    [Fact]
    public void A_chips_x_turns_its_filter_off()
    {
        _filters.Apply(new SmartViewDefinition(Players: 4));
        _filters.ShowChips();

        _filters.ActiveFilters.Single().Clear.Execute(null);
        _filters.ShowChips();

        Assert.Null(_filters.PlayersChoice.Value);
        Assert.Equal(1, _changes);
        Assert.False(_filters.HasActiveFilters);
        Assert.Equal("Filters", _filters.FiltersLabel);
    }

    [Fact]
    public void A_picked_value_no_book_has_any_more_stays_picked_with_a_count_of_none()
    {
        _filters.Apply(new SmartViewDefinition(System: "dnd5e"));

        IReadOnlyList<ValueCount> none = [];
        _filters.ShowFacets(new FacetMenus([new FacetCount("pf2e", "Pathfinder 2e", 3)], [], [], [], [], [], [.. _filters.ChipFilters.Select(_ => none)]));

        Assert.Equal("dnd5e (0)", _filters.SystemChoice.Label);
        Assert.Equal(["All systems", "Pathfinder 2e (3)", "dnd5e (0)"], _filters.SystemChoices.Select(c => c.Label));
        Assert.Equal(["dnd5e"], _filters.ActiveFilters.Select(c => c.Label));
        Assert.Equal(0, _changes);
    }

    [Fact]
    public void The_query_keeps_the_chosen_filters()
    {
        _filters.Apply(Everything());

        var filter = _filters.ToFilter([], LibrarySort.Title, "favorites");

        Assert.Equal(KindFilter.Books, filter.Kind);
        Assert.Equal(LibrarySort.Title, filter.Sort);
        Assert.Equal(["dnd5e"], filter.Systems);
        Assert.Equal(3, filter.Level);
        Assert.Equal("favorites", filter.Group);
        Assert.False(filter.HideParts);
        Assert.False(filter.PartsInBooks);
        Assert.Equal(["the-marches"], filter.Picked?[MetadataFields.Settings.Key]);
    }
}
