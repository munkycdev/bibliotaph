using Bibliotaph.Web.Services;

namespace Bibliotaph.Server.Tests;

/// <summary>Select mode in the web Library ticks books by click, Shift+click and Ctrl+A.</summary>
public sealed class LibrarySelectionTests
{
    static readonly long[] Shown = [10, 20, 30, 40, 50];

    static LibrarySelection Selecting()
    {
        var select = new LibrarySelection(new ApiClient(new HttpClient()));
        select.Start();
        return select;
    }

    [Fact]
    public void A_click_ticks_a_book_and_a_second_takes_it_away()
    {
        var select = Selecting();
        select.Click(20, shift: false, Shown);
        Assert.Equal([20L], select.Ticked);
        select.Click(20, shift: false, Shown);
        Assert.Empty(select.Ticked);
    }

    [Fact]
    public void Shift_click_ticks_every_book_from_the_last_one_clicked()
    {
        var select = Selecting();
        select.Click(40, shift: false, Shown);
        select.Click(20, shift: true, Shown);
        Assert.Equal([40L, 20L, 30L], select.Ticked);
    }

    [Fact]
    public void Shift_click_with_nothing_clicked_yet_ticks_just_that_book()
    {
        var select = Selecting();
        select.Click(30, shift: true, Shown);
        Assert.Equal([30L], select.Ticked);
    }

    [Fact]
    public void Ticks_from_an_earlier_search_stay_when_all_shown_are_ticked()
    {
        var select = Selecting();
        select.Click(99, shift: false, [99]);
        select.TickAll(Shown);
        Assert.Equal([99L, 10L, 20L, 30L, 40L, 50L], select.Ticked);
    }

    [Fact]
    public void Clear_forgets_the_last_book_clicked()
    {
        var select = Selecting();
        select.Click(10, shift: false, Shown);
        select.Clear();
        select.Click(30, shift: true, Shown);
        Assert.Equal([30L], select.Ticked);
    }

    [Fact]
    public void Leaving_select_mode_drops_the_ticks()
    {
        var select = Selecting();
        select.Click(10, shift: false, Shown);
        select.Stop();
        Assert.False(select.On);
        Assert.Empty(select.Ticked);
        Assert.Null(select.Info);
    }
}
