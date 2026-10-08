using Bibliotaph.Core.Progress;

namespace Bibliotaph.Core.Tests;

public class ProgressTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 8, 20, 0, 0, TimeSpan.Zero);

    static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void An_estimate_waits_for_enough_to_go_on_then_follows_the_rate()
    {
        var rate = new RateEstimator();
        rate.Record(At(0), 0);
        rate.Record(At(10), 5);
        Assert.Null(rate.Estimate(At(10), 100)); // ten seconds is too soon

        rate.Record(At(30), 15);
        Assert.Equal(TimeSpan.FromSeconds(200), rate.Estimate(At(30), 100)); // half an item a second
        Assert.Equal(TimeSpan.Zero, rate.Estimate(At(30), 0));
    }

    [Fact]
    public void A_stall_makes_the_estimate_grow()
    {
        var rate = new RateEstimator();
        rate.Record(At(0), 0);
        rate.Record(At(30), 30);

        Assert.Equal(TimeSpan.FromSeconds(10), rate.Estimate(At(30), 10));
        Assert.Equal(TimeSpan.FromSeconds(20), rate.Estimate(At(60), 10));
    }

    [Fact]
    public void Only_the_recent_window_counts()
    {
        var rate = new RateEstimator(window: TimeSpan.FromMinutes(1));
        rate.Record(At(0), 0);
        rate.Record(At(30), 300);   // a quick start
        rate.Record(At(60), 310);
        rate.Record(At(120), 320);  // then one item every six seconds

        Assert.Equal(TimeSpan.FromSeconds(60), rate.Estimate(At(120), 10));
    }

    [Fact]
    public void A_count_going_down_or_a_reset_starts_over()
    {
        var rate = new RateEstimator();
        rate.Record(At(0), 0);
        rate.Record(At(60), 60);
        rate.Record(At(61), 2);
        Assert.Null(rate.Estimate(At(61), 10));

        rate.Reset();
        Assert.Null(rate.Estimate(At(200), 10));
    }

    [Theory]
    [InlineData(20, "less than a minute")]
    [InlineData(70, "about a minute")]
    [InlineData(7 * 60 + 20, "about 7 minutes")]
    [InlineData(23 * 60, "about 25 minutes")]
    [InlineData(58 * 60, "about an hour")]
    [InlineData(82 * 60, "about 1 hour 20 minutes")]
    [InlineData(5 * 3600 + 1000, "about 5 hours")]
    [InlineData(3 * 86400, "about 3 days")]
    public void Time_left_reads_as_a_rough_figure(int seconds, string expected) =>
        Assert.Equal(expected, TimeLeft.Describe(TimeSpan.FromSeconds(seconds)));
}
