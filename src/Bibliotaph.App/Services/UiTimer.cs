using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Bibliotaph.App.Services;

/// <summary>
/// A timer that ticks on the thread that made it, as a DispatcherTimer does: one made on the UI thread ticks there, at
/// its priority, behind input and layout unless it says otherwise.
/// Its time comes from a <see cref="TimeProvider"/>, so a test can move it on by hand; one made where there is no
/// <see cref="SynchronizationContext"/>, as in a test, ticks inline when its time comes.
/// </summary>
/// <remarks>
/// It holds a timer only while it runs, and Stop lets that go, so there is nothing to dispose: a page stops its
/// timers when it is left. Being disposable would make every page that owns one disposable too, and the host keeps a
/// disposable page it made for as long as the app runs.
/// </remarks>
public sealed class UiTimer
{
    readonly TimeProvider _time;
    readonly SynchronizationContext? _context;
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

    /// <param name="time">Where the time comes from.</param>
    /// <param name="interval">How long after Start it ticks, and between ticks.</param>
    /// <param name="repeat">True to tick every <paramref name="interval"/> until stopped; false to tick once and stop.</param>
    /// <param name="tick">The work to do, on the thread that made the timer.</param>
    /// <param name="priority">Where a tick waits among the UI thread's other work.</param>
    public UiTimer(TimeProvider time, TimeSpan interval, bool repeat, Action tick, DispatcherPriority priority = DispatcherPriority.Background)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(tick);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _time = time;
        _interval = interval;
        _repeat = repeat;
        _tick = tick;
        // The UI thread's own context posts at Normal, ahead of input; this keeps the priority a DispatcherTimer had.
        _context = SynchronizationContext.Current is DispatcherSynchronizationContext
            ? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher, priority)
            : SynchronizationContext.Current;
    }

    /// <summary>A timer whose tick is work it doesn't wait for, logged if it fails (<see cref="BackgroundWork.FireAndForget"/>).</summary>
    public UiTimer(TimeProvider time, TimeSpan interval, bool repeat, Func<Task> tick, DispatcherPriority priority = DispatcherPriority.Background,
        [CallerArgumentExpression(nameof(tick))] string what = "")
        : this(time, interval, repeat, Logged(tick, what), priority)
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
        if (_context is null) Tick();
        else if (Interlocked.Exchange(ref _posted, 1) == 0) _context.Post(static state => ((UiTimer)state!).Tick(), this);
    }

    void Tick()
    {
        Volatile.Write(ref _posted, 0);
        if (Volatile.Read(ref _fired) != _generation) return;
        if (!_repeat) Stop();
        _tick();
    }
}
