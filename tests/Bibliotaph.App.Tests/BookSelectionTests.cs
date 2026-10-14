using System.IO;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.App.Tests;

public class BookSelectionTests
{
    // The cards never ask for a cover in these tests, so the cache folder is never read.
    static readonly CoverImages Covers = new(new CoverCache(new AppPaths(Path.GetTempPath())), NullLogger<CoverImages>.Instance);

    readonly BookSelection _selection = new();
    readonly List<LibraryItemViewModel> _shown = [.. new[] { "Mill Road", "The Sunken Bell", "Ashes of Varn", "The Lantern Fair", "Cold Harbour" }.Select(Book)];

    public BookSelectionTests()
    {
        _selection.Start();
        _selection.SetShown(_shown);
    }

    static LibraryItemViewModel Book(string title, int index) =>
        new(new LibraryEntry(new EntryId(1000 + index), index + 1, title, "pdf", 32, null, null, DateTime.UtcNow, Searchable: true), Covers);

    [Fact]
    public void Clicking_a_book_ticks_it()
    {
        _selection.Toggle(_shown[1]);

        Assert.True(_shown[1].IsSelected);
        Assert.Equal(1, _selection.Count);
        Assert.Equal("1 selected", _selection.Summary);
    }

    [Fact]
    public void Clicking_a_ticked_book_again_unticks_it()
    {
        _selection.Toggle(_shown[1]);
        _selection.Toggle(_shown[1]);

        Assert.False(_shown[1].IsSelected);
        Assert.False(_selection.HasAny);
        Assert.StartsWith("Click books to select them.", _selection.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Shift_click_ticks_every_book_between_the_last_click_and_this_one_in_either_direction()
    {
        _selection.Toggle(_shown[3]);
        _selection.SelectRange(_shown, _shown[1]);

        Assert.Equal([false, true, true, true, false], _shown.Select(b => b.IsSelected));
        Assert.Equal(3, _selection.Count);
    }

    [Fact]
    public void Shift_click_with_nothing_clicked_before_ticks_just_that_book()
    {
        _selection.SelectRange(_shown, _shown[2]);

        Assert.Equal([_shown[2].EntryId], _selection.EntryIds);
    }

    [Fact]
    public void Ticks_stay_when_the_results_change_and_are_counted_as_not_shown()
    {
        _selection.Toggle(_shown[0]);
        _selection.Toggle(_shown[4]);

        _selection.SetShown(_shown.Take(2));

        Assert.Equal(2, _selection.Count);
        Assert.Equal(1, _selection.NotShown);
        Assert.Equal("2 selected, 1 not shown", _selection.Summary);
    }

    [Fact]
    public void Select_all_adds_what_is_shown_to_the_ticks_from_an_earlier_search()
    {
        _selection.Toggle(_shown[4]);
        _selection.SetShown(_shown.Take(2));

        _selection.SelectAll(_shown.Take(2));

        Assert.Equal([_shown[4].EntryId, _shown[0].EntryId, _shown[1].EntryId], _selection.EntryIds);
        Assert.Equal("3 selected, 1 not shown", _selection.Summary);
    }

    [Fact]
    public void Leaving_select_mode_unticks_every_book()
    {
        _selection.SelectAll(_shown);

        _selection.Stop();

        Assert.False(_selection.IsActive);
        Assert.Equal(0, _selection.Count);
        Assert.All(_shown, b => Assert.False(b.IsSelected));
    }

    [Fact]
    public void Clear_forgets_the_last_click_so_shift_click_ticks_one_book()
    {
        _selection.Toggle(_shown[0]);
        _selection.Clear();

        _selection.SelectRange(_shown, _shown[3]);

        Assert.Equal([false, false, false, true, false], _shown.Select(b => b.IsSelected));
    }

    [Fact]
    public void Starting_select_mode_twice_tells_listeners_once()
    {
        var selection = new BookSelection();
        var changes = 0;
        selection.Changed += (_, _) => changes++;

        selection.Start();
        selection.Start();

        Assert.True(selection.IsActive);
        Assert.Equal(1, changes);
    }
}
