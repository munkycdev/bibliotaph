using Bibliotaph.Core.Layout;

namespace Bibliotaph.Core.Tests;

public class WindowPlacementTests
{
    // A 1080p laptop screen with the taskbar at the bottom, and a 4K monitor to its right.
    static readonly ScreenRect Laptop = new(0, 0, 1920, 1032);
    static readonly ScreenRect Monitor = new(1920, -200, 3840, 2112);

    static WindowPlacement At(int left, int top, int width, int height, bool maximized = false) =>
        new(new ScreenRect(left, top, width, height), maximized);

    [Fact]
    public void A_window_on_a_screen_that_is_still_there_stays_put()
    {
        var onMonitor = At(2400, 100, 1400, 1800);

        Assert.Equal(onMonitor, onMonitor.Fit([Laptop, Monitor]));
    }

    [Fact]
    public void A_window_flush_with_the_top_keeps_its_invisible_border_above_the_screen()
    {
        var flush = At(-7, -7, 1000, 900);

        Assert.Equal(flush, flush.Fit([Laptop]));
    }

    [Fact]
    public void A_window_across_two_screens_stays_where_it_was()
    {
        var straddling = At(1500, 100, 800, 700);

        Assert.Equal(straddling, straddling.Fit([Laptop, Monitor]));
    }

    [Fact]
    public void A_window_whose_monitor_was_unplugged_moves_onto_the_nearest_screen_and_shrinks_to_fit()
    {
        var onMonitor = At(2400, 100, 1400, 1800, maximized: true);

        var fitted = onMonitor.Fit([Laptop]);

        Assert.Equal(At(520, 0, 1400, 1032, maximized: true), fitted);
    }

    [Fact]
    public void A_window_mostly_off_the_side_of_a_smaller_screen_is_pulled_inside_it()
    {
        // Saved on a wider screen at the same place; now three quarters of it hang off the right.
        var offSide = At(1700, 200, 900, 600);

        Assert.Equal(At(1020, 200, 900, 600), offSide.Fit([Laptop]));
    }

    [Fact]
    public void A_window_with_its_title_bar_above_the_screen_comes_down()
    {
        var tooHigh = At(300, -400, 900, 1000);

        Assert.Equal(At(300, 0, 900, 1000), tooHigh.Fit([Laptop]));
    }

    [Fact]
    public void A_window_below_the_bottom_of_the_screen_comes_up()
    {
        var tooLow = At(300, 1010, 900, 600);

        Assert.Equal(At(300, 432, 900, 600), tooLow.Fit([Laptop]));
    }

    [Fact]
    public void With_no_screens_to_go_by_nothing_changes()
    {
        var anywhere = At(-5000, -5000, 800, 600);

        Assert.Equal(anywhere, anywhere.Fit([]));
    }

    [Fact]
    public void A_cascaded_window_that_runs_off_the_screen_is_pulled_back()
    {
        var corner = At(1500, 700, 800, 500).Cascade(32).Cascade(32);

        Assert.Equal(At(1120, 532, 800, 500), corner.Fit([Laptop]));
    }

    [Theory]
    [InlineData("10,-20,800,600,0", 10, -20, 800, 600, false)]
    [InlineData("-1913,8,1400,1000,1", -1913, 8, 1400, 1000, true)]
    public void Reads_back_what_it_saved(string text, int left, int top, int width, int height, bool maximized)
    {
        var placement = WindowPlacement.Parse(text);

        Assert.Equal(At(left, top, width, height, maximized), placement);
        Assert.Equal(text, placement!.Format());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10,20,800,600")]
    [InlineData("10,20,0,600,0")]
    [InlineData("10,20,800,600,2")]
    [InlineData("a,b,c,d,e")]
    public void Ignores_a_saved_value_that_is_not_a_placement(string? text) => Assert.Null(WindowPlacement.Parse(text));
}
