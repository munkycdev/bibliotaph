using System.Globalization;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;

namespace Bibliotaph.App.Tests;

public class BinderDialogTests
{
    static readonly IReadOnlyList<Choice<string?>> Systems = [new("dnd5e", "D&D 5e"), new("pf2e", "Pathfinder 2e")];

    static SessionDialogResult? Save(SessionDialogViewModel dialog)
    {
        SessionDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;
        dialog.SaveCommand.Execute(null);
        return result;
    }

    [Fact]
    public void A_new_binder_is_saved_with_its_title_and_the_date_typed()
    {
        var dialog = SessionDialogViewModel.Naming("New binder", "Create", Systems);
        dialog.Title = " Session 4 ";
        dialog.DateText = new DateOnly(2026, 10, 17).ToString("d", CultureInfo.CurrentCulture);

        var result = Save(dialog);

        Assert.Equal(new SessionDialogResult("Session 4", new DateOnly(2026, 10, 17), null), result);
    }

    [Fact]
    public void A_date_that_does_not_read_is_called_out_and_the_binder_cannot_be_saved()
    {
        var dialog = SessionDialogViewModel.Naming("New binder", "Create", Systems);
        dialog.Title = "Session 4";

        dialog.DateText = "next Thursday";

        Assert.NotNull(dialog.DateProblem);
        Assert.False(dialog.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void A_binder_kept_at_hand_has_no_date_even_if_one_was_typed()
    {
        var dialog = SessionDialogViewModel.Naming("New binder", "Create", Systems);
        dialog.Title = "Rules to hand";
        dialog.DateText = "next Thursday";

        dialog.KeepAtHand = true;
        dialog.GameSystem = Systems[1];
        var result = Save(dialog);

        Assert.False(dialog.ShowsDate);
        Assert.Null(dialog.DateProblem);
        Assert.Equal(new SessionDialogResult("Rules to hand", null, null, KeepAtHand: true, GameSystem: "pf2e"), result);
    }

    [Fact]
    public void The_game_system_is_kept_only_for_a_binder_kept_at_hand()
    {
        var dialog = SessionDialogViewModel.Naming("Edit binder", "Save", Systems, "Session 4", keepAtHand: true, gameSystem: "pf2e");

        dialog.KeepAtHand = false;
        var result = Save(dialog);

        Assert.Equal(new SessionDialogResult("Session 4", null, null), result);
    }

    [Fact]
    public void Editing_a_binder_for_a_system_no_longer_in_the_library_still_offers_that_system()
    {
        var dialog = SessionDialogViewModel.Naming("Edit binder", "Save", Systems, "Rules to hand", keepAtHand: true, gameSystem: "mothership");

        Assert.Equal(["Any game system", "D&D 5e", "Pathfinder 2e", "mothership"], dialog.SystemChoices.Select(c => c.Label));
        Assert.Equal("mothership", dialog.GameSystem?.Value);
    }

    [Fact]
    public void A_section_needs_only_a_name()
    {
        var dialog = SessionDialogViewModel.NamingSection("New section", "Add");
        dialog.Title = "Act two";

        var result = Save(dialog);

        Assert.Equal("SECTION", dialog.Eyebrow);
        Assert.False(dialog.ShowsDate);
        Assert.Equal(new SessionDialogResult("Act two", null, null), result);
    }

    [Fact]
    public void The_binder_picker_lists_binders_by_title_and_narrows_as_you_type()
    {
        var mill = new SessionPackInfo(1, "Mill Road", new DateOnly(2026, 11, 2), null, DateTime.UtcNow, 3, []);
        var bell = new SessionPackInfo(2, "The Sunken Bell", null, null, DateTime.UtcNow, 5, []);
        var ashes = new SessionPackInfo(3, "Ashes of Varn", null, null, DateTime.UtcNow, 1, []);
        var dialog = SessionDialogViewModel.Picking("Add to another binder", [mill, bell, ashes]);
        SessionDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;

        var all = dialog.Shown.Select(p => p.Title).ToList();
        dialog.Filter = "road";
        dialog.PickFirstCommand.Execute(null);

        Assert.Equal(["Ashes of Varn", "Mill Road", "The Sunken Bell"], all);
        Assert.Equal(new SessionDialogResult("Mill Road", new DateOnly(2026, 11, 2), 1), result);
    }
}
