using System.Runtime.CompilerServices;

namespace Bibliotaph.App.Services;

/// <summary>
/// A timer that ticks on the UI thread, as a DispatcherTimer does, at its priority: behind input and layout unless it
/// says otherwise. Its time comes from a <see cref="TimeProvider"/>, so a test can move it on by hand; with an
/// <see cref="ImmediateUiThread"/>, as in a test, it ticks inline when its time comes.
/// </summary>
/// <remarks>
/// It holds a timer only while it runs, and Stop lets that go, so there is nothing to dispose: a page stops its
/// timers when it is left. Being disposable would make every page that owns one disposable too, and the host keeps a
/// disposable page it made for as long as the app runs.
/// </remarks>
public sealed class UiTimer
{
    readonly IUiThread _ui;
    readonly TimeProvider _time;
    readonly UiPriority _priority;
    readonly TimeSpan _interval;
    readonly Action _tick;
    readonly bool _repeat;
    ITimer? _timer;
    // Every Start and Stop moves this on, so a tick already on its way to the UI thread from before is dropped.
    int _generation;
    // The newest timer to fire, and whether a tick is already waiting for the UI thread: while the UI thread is busy,
    // ticks that pile up run once, as a DispatcherTimer's do.
    int _fired;
    int _posted;

    /// <param name="ui">The thread it ticks on.</param>
    /// <param name="time">Where the time comes from.</param>
    /// <param name="interval">How long after Start it ticks, and between ticks.</param>
    /// <param name="repeat">True to tick every <paramref name="interval"/> until stopped; false to tick once and stop.</param>
    /// <param name="tick">The work to do, on the UI thread.</param>
    /// <param name="priority">Where a tick waits among the UI thread's other work.</param>
    public UiTimer(IUiThread ui, TimeProvider time, TimeSpan interval, bool repeat, Action tick, UiPriority priority = UiPriority.Background)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _ui = ui;
        _time = time;
        _interval = interval;
        _repeat = repeat;
        _tick = tick;
        _priority = priority;
    }

    /// <summary>A timer whose tick is work it doesn't wait for, logged if it fails (<see cref="BackgroundWork.FireAndForget"/>).</summary>
    public UiTimer(IUiThread ui, TimeProvider time, TimeSpan interval, bool repeat, Func<Task> tick, UiPriority priority = UiPriority.Background,
        [CallerArgumentExpression(nameof(tick))] string what = "")
        : this(ui, time, interval, repeat, Logged(tick, what), priority)
    {
    }

    /// <summary>Starts the timer. If it is already running, its interval starts again from now, which puts off the tick.</summary>
    public void Start()
    {
        _timer?.Dispose();
        var generation = ++_generation;
        _timer = _time.CreateTimer(static state =>
        {
            var (owner, generation) = ((UiTimer, int))state!;
            owner.Fire(generation);
        }, (this, generation), _interval, _repeat ? _interval : Timeout.InfiniteTimeSpan);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        _generation++;
    }

    static Action Logged(Func<Task> tick, string what)
    {
        ArgumentNullException.ThrowIfNull(tick);
        return () => tick().FireAndForget(what);
    }

    void Fire(int generation)
    {
        Volatile.Write(ref _fired, generation);
        if (Interlocked.Exchange(ref _posted, 1) == 0) _ui.Post(Tick, _priority);
    }

    void Tick()
    {
        Volatile.Write(ref _posted, 0);
        if (Volatile.Read(ref _fired) != _generation) return;
        if (!_repeat) Stop();
        _tick();
    }
}
