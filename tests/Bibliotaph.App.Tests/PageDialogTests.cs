using System.Globalization;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.App.Tests;

/// <summary>A note on pages, and Add pages… to a binder: pages typed as the book numbers them.</summary>
public class PageDialogTests
{
    // A 40-page book whose first four pages are numbered i to iv, and the rest 1 to 36.
    static readonly IReadOnlyList<string?> Labels = [.. new[] { "i", "ii", "iii", "iv" }, .. Enumerable.Range(1, 36).Select(n => n.ToString(CultureInfo.CurrentCulture))];

    static int? FindPage(string typed) => PageNumbers.Find(typed, Labels, Labels.Count);

    static PageNoteDialogViewModel Note(string from, string to, string text) => new("Note on pages", "Save", from, to, text, FindPage);

    [Fact]
    public void A_note_on_one_page_needs_no_To()
    {
        var dialog = Note("ii", "", "The lever opens the vault");
        PageNoteDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;

        dialog.SaveCommand.Execute(null);

        Assert.Equal(new PageNoteDialogResult(1, 1, "The lever opens the vault"), result);
    }

    [Fact]
    public void Pages_typed_the_wrong_way_round_are_put_in_order()
    {
        var dialog = Note("12", "10", "  The bell tower  ");
        PageNoteDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;

        dialog.SaveCommand.Execute(null);

        Assert.Equal(new PageNoteDialogResult(13, 15, "The bell tower"), result);
    }

    [Fact]
    public void A_page_the_book_does_not_have_is_called_out_and_cannot_be_saved()
    {
        var dialog = Note("3", "", "The bell tower");

        dialog.To = "99";

        Assert.NotNull(dialog.PagesProblem);
        Assert.False(dialog.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void A_note_needs_some_text()
    {
        var dialog = Note("3", "", "The bell tower");

        dialog.Text = "  ";

        Assert.Null(dialog.PagesProblem);
        Assert.False(dialog.SaveCommand.CanExecute(null));
    }

    static readonly SessionPackInfo MillRoad = Pack(7, "Mill Road");
    static readonly SessionPackInfo Sunken = Pack(3, "The Sunken Bell");

    static SessionPackInfo Pack(long id, string title) => new(id, title, null, null, DateTime.UtcNow, 0, []);

    static SessionDialogViewModel AddPages(Func<long, Task<IReadOnlyList<SessionSectionInfo>>>? sections = null) =>
        SessionDialogViewModel.Pages([MillRoad, Sunken], "3", "5", "", FindPage, sections ?? (_ => Task.FromResult<IReadOnlyList<SessionSectionInfo>>([])));

    [Fact]
    public void Add_pages_goes_to_the_end_of_the_first_pack_offered_unless_another_is_picked()
    {
        var dialog = AddPages();
        SessionDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;

        dialog.SaveCommand.Execute(null);

        Assert.Equal(new SessionDialogResult("", null, MillRoad.Id, 6, 8, Label: null, SectionId: null), result);
    }

    [Fact]
    public void A_pack_with_sections_offers_its_end_before_the_first_section_and_each_section()
    {
        var dialog = AddPages(id => Task.FromResult<IReadOnlyList<SessionSectionInfo>>(id == Sunken.Id ? [new(31, "Act one", 0), new(32, "Act two", 1)] : []));

        dialog.Pack = dialog.PackChoices.Single(c => c.Value == Sunken.Id);

        Assert.Equal(["At the end", "Before the first section", "Act one", "Act two"], dialog.SectionChoices.Select(c => c.Label));
        long?[] sections = [null, SessionStore.NoSection, 31, 32];
        Assert.Equal(sections, dialog.SectionChoices.Select(c => c.Value));
        Assert.Null(dialog.Section?.Value);
    }

    [Fact]
    public void Pages_are_added_with_their_label_and_section()
    {
        var dialog = AddPages(_ => Task.FromResult<IReadOnlyList<SessionSectionInfo>>([new(31, "Act one", 0)]));
        SessionDialogResult? result = null;
        dialog.Closed += (_, r) => result = r;
        dialog.Pack = dialog.PackChoices[1];
        dialog.Section = dialog.SectionChoices.Single(c => c.Value == 31);
        dialog.Label = " Bell tower map ";
        dialog.From = "iv";
        dialog.To = "";

        dialog.SaveCommand.Execute(null);

        Assert.Equal(new SessionDialogResult("", null, Sunken.Id, 3, 3, "Bell tower map", 31), result);
    }

    [Fact]
    public void Pages_the_book_does_not_have_cannot_be_added()
    {
        var dialog = AddPages();

        dialog.From = "xx";

        Assert.NotNull(dialog.PagesProblem);
        Assert.False(dialog.SaveCommand.CanExecute(null));
    }
}
