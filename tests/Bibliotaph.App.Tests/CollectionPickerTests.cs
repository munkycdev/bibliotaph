using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Tests;

public class CollectionPickerTests
{
    static readonly IReadOnlyList<CollectionChoice> Tree =
    [
        new(1, "Winter campaign", "Winter campaign", 0),
        new(2, "Mill Road", "Winter campaign › Mill Road", 1),
        new(3, "Maps", "Winter campaign › Mill Road › Maps", 2),
        new(4, "Maps", "Maps", 0),
    ];

    readonly CollectionDialogViewModel _dialog = CollectionDialogViewModel.Picking("Move to", Tree);
    CollectionDialogResult? _result;

    public CollectionPickerTests() => _dialog.Closed += (_, r) => _result = r;

    [Fact]
    public void Filtering_keeps_the_collections_whose_path_has_every_word_and_lists_them_by_path()
    {
        _dialog.Filter = "mill MAPS";

        var shown = Assert.Single(_dialog.Shown);
        Assert.Equal(3, shown.Id);
        Assert.Equal("Winter campaign › Mill Road › Maps", shown.Name);
        Assert.Equal(0, shown.Depth);
    }

    [Fact]
    public void A_filter_that_matches_nothing_says_so()
    {
        _dialog.Filter = "Sunken";

        Assert.Empty(_dialog.Shown);
        Assert.True(_dialog.HasNoMatch);
    }

    [Fact]
    public void Clearing_the_filter_brings_back_the_tree()
    {
        _dialog.Filter = "maps";
        _dialog.Filter = " ";

        Assert.Equal(Tree, _dialog.Shown);
    }

    [Fact]
    public void Enter_in_the_filter_picks_the_first_collection_it_leaves()
    {
        _dialog.Filter = "maps";

        _dialog.PickFirstCommand.Execute(null);

        Assert.Equal(3, _result?.Choice?.Id);
    }

    [Fact]
    public void Enter_with_nothing_left_picks_nothing_and_keeps_the_dialog_open()
    {
        _dialog.Filter = "Sunken";

        _dialog.PickFirstCommand.Execute(null);

        Assert.Null(_result);
    }

    [Fact]
    public void Clicking_a_collection_picks_it()
    {
        _dialog.PickCommand.Execute(Tree[1]);

        Assert.Equal(Tree[1], _result?.Choice);
    }
}
