using Bibliotaph.App.Services;

namespace Bibliotaph.App.Tests;

public class UiTimerTests
{
    static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    int _ticks;

    [Fact]
    public void It_ticks_once_its_interval_has_passed_and_not_before()
    {
        var timer = new UiTimer(_clock, Pause, repeat: false, () => _ticks++);

        timer.Start();
        _clock.Advance(Pause - TimeSpan.FromMilliseconds(1));
        var early = _ticks;
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(0, early);
        Assert.Equal(1, _ticks);
    }

    [Fact]
    public void It_does_nothing_until_it_is_started()
    {
        _ = new UiTimer(_clock, Pause, repeat: true, () => _ticks++);

        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _ticks);
    }

    [Fact]
    public void Stopping_it_before_its_time_means_it_never_ticks()
    {
        var timer = new UiTimer(_clock, Pause, repeat: false, () => _ticks++);

        timer.Start();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        timer.Stop();
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _ticks);
    }

    [Fact]
    public void Starting_it_again_while_it_runs_puts_the_tick_off_by_a_whole_interval()
    {
        var timer = new UiTimer(_clock, Pause, repeat: false, () => _ticks++);

        timer.Start();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        timer.Start();
        _clock.Advance(TimeSpan.FromMilliseconds(699));
        var early = _ticks;
        _clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(0, early);
        Assert.Equal(1, _ticks);
    }

    [Fact]
    public void One_that_ticks_once_stays_stopped_until_started_again()
    {
        var timer = new UiTimer(_clock, Pause, repeat: false, () => _ticks++);

        timer.Start();
        _clock.Advance(TimeSpan.FromSeconds(10));
        var once = _ticks;
        timer.Start();
        _clock.Advance(Pause);

        Assert.Equal(1, once);
        Assert.Equal(2, _ticks);
    }

    [Fact]
    public void One_that_repeats_ticks_every_interval_until_it_is_stopped()
    {
        var timer = new UiTimer(_clock, TimeSpan.FromMilliseconds(250), repeat: true, () => _ticks++);

        timer.Start();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var running = _ticks;
        timer.Stop();
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(4, running);
        Assert.Equal(4, _ticks);
    }

    [Fact]
    public void A_tick_can_stop_the_timer_that_repeats()
    {
        UiTimer? timer = null;
        timer = new UiTimer(_clock, TimeSpan.FromMilliseconds(250), repeat: true, () =>
        {
            if (++_ticks == 2) timer!.Stop();
        });

        timer.Start();
        _clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(2, _ticks);
    }

    [Fact]
    public void Work_it_does_not_wait_for_runs_at_the_tick()
    {
        var timer = new UiTimer(_clock, Pause, repeat: false, () =>
        {
            _ticks++;
            return Task.CompletedTask;
        });

        timer.Start();
        _clock.Advance(Pause);

        Assert.Equal(1, _ticks);
    }
}
