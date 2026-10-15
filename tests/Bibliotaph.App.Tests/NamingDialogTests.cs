using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Tests;

public class NamingDialogTests
{
    [Fact]
    public void A_smart_view_is_saved_under_its_name_without_the_spaces_around_it()
    {
        var dialog = new SmartViewDialogViewModel("Save view", "Save");
        string? closed = null;
        dialog.Closed += (_, name) => closed = name;

        dialog.Name = "  Winter prep  ";
        dialog.SaveCommand.Execute(null);

        Assert.Equal("Winter prep", closed);
    }

    [Fact]
    public void A_smart_view_cannot_be_saved_until_it_has_a_name()
    {
        var dialog = new SmartViewDialogViewModel("Save view", "Save");
        var changes = 0;
        dialog.SaveCommand.CanExecuteChanged += (_, _) => changes++;

        dialog.Name = "   ";
        var blank = dialog.SaveCommand.CanExecute(null);
        dialog.Name = "Maps";

        Assert.False(blank);
        Assert.True(dialog.SaveCommand.CanExecute(null));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Cancelling_a_smart_view_closes_with_no_name()
    {
        var dialog = new SmartViewDialogViewModel("Rename view", "Rename", "Maps");
        var closed = false;
        string? name = "not closed";
        dialog.Closed += (_, n) => (closed, name) = (true, n);

        dialog.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Null(name);
    }

    [Fact]
    public void A_new_collection_keeps_its_description_only_when_one_was_typed()
    {
        var dialog = CollectionDialogViewModel.Naming("New collection", "Create");
        CollectionDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;

        dialog.Name = " Mill Road ";
        dialog.Description = "   ";
        dialog.SaveCommand.Execute(null);

        Assert.Equal(new CollectionDialogResult("Mill Road", null, null), result);
    }

    [Fact]
    public void Renaming_a_collection_starts_from_its_name_and_description()
    {
        var dialog = CollectionDialogViewModel.Naming("Rename collection", "Rename", "Mill Road", "The village arc");
        CollectionDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;

        dialog.SaveCommand.Execute(null);

        Assert.True(dialog.IsNaming);
        Assert.Equal(new CollectionDialogResult("Mill Road", "The village arc", null), result);
    }
}
