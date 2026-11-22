using Bibliotaph.Presentation.Services;

namespace Bibliotaph.Presentation.Tests;

public class UiTimerTests
{
    static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(700);

    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
    int _ticks;

    [Fact]
    public void It_ticks_once_its_interval_has_passed_and_not_before()
    {
        var timer = new UiTimer(ImmediateUiThread.Instance, _clock, Pause, repeat: false, () => _ticks++);

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
        _ = new UiTimer(ImmediateUiThread.Instance, _clock, Pause, repeat: true, () => _ticks++);

        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _ticks);
    }

    [Fact]
    public void Stopping_it_before_its_time_means_it_never_ticks()
    {
        var timer = new UiTimer(ImmediateUiThread.Instance, _clock, Pause, repeat: false, () => _ticks++);

        timer.Start();
        _clock.Advance(TimeSpan.FromMilliseconds(500));
        timer.Stop();
        _clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, _ticks);
    }

    [Fact]
    public void Starting_it_again_while_it_runs_puts_the_tick_off_by_a_whole_interval()
    {
        var timer = new UiTimer(ImmediateUiThread.Instance, _clock, Pause, repeat: false, () => _ticks++);

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
        var timer = new UiTimer(ImmediateUiThread.Instance, _clock, Pause, repeat: false, () => _ticks++);

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
        var timer = new UiTimer(ImmediateUiThread.Instance, _clock, TimeSpan.FromMilliseconds(250), repeat: true, () => _ticks++);

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
        timer = new UiTimer(ImmediateUiThread.Instance, _clock, TimeSpan.FromMilliseconds(250), repeat: true, () =>
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
        var timer = new UiTimer(ImmediateUiThread.Instance, _clock, Pause, repeat: false, () =>
        {
            _ticks++;
            return Task.CompletedTask;
        });

        timer.Start();
        _clock.Advance(Pause);

        Assert.Equal(1, _ticks);
    }

    [Fact]
    public void Ticks_that_pile_up_while_the_UI_thread_is_busy_run_once_at_the_timers_priority()
    {
        var ui = new QueuedUiThread();
        var timer = new UiTimer(ui, _clock, TimeSpan.FromMilliseconds(250), repeat: true, () => _ticks++, UiPriority.Input);

        timer.Start();
        _clock.Advance(TimeSpan.FromSeconds(1));
        ui.RunAll();

        Assert.Equal([UiPriority.Input], ui.Priorities);
        Assert.Equal(1, _ticks);
    }

    [Fact]
    public void A_tick_still_waiting_for_the_UI_thread_when_the_timer_stops_never_runs()
    {
        var ui = new QueuedUiThread();
        var timer = new UiTimer(ui, _clock, Pause, repeat: false, () => _ticks++);

        timer.Start();
        _clock.Advance(Pause);
        timer.Stop();
        ui.RunAll();

        Assert.Equal(0, _ticks);
    }

    /// <summary>A UI thread that is busy until the test lets it run what was posted.</summary>
    sealed class QueuedUiThread : IUiThread
    {
        readonly Queue<Action> _work = [];

        public List<UiPriority> Priorities { get; } = [];

        public bool IsCurrent => false;

        public void Post(Action work, UiPriority priority = UiPriority.Normal)
        {
            Priorities.Add(priority);
            _work.Enqueue(work);
        }

        public void Post(Func<Task> work, UiPriority priority = UiPriority.Normal, string what = "") => Post(() => work().FireAndForget(what), priority);

        public Task RunAsync(Func<Task> work) => throw new NotSupportedException();

        public void RunAll()
        {
            while (_work.TryDequeue(out var work)) work();
        }
    }
}
