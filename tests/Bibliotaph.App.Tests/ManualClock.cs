namespace Bibliotaph.App.Tests;

/// <summary>A clock the test moves by hand, whose timers fire inline as it passes their time.</summary>
sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    readonly List<ManualTimer> _scheduled = [];
    DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock on, firing each timer at its time, earliest first, as many times as its time comes.</summary>
    public void Advance(TimeSpan by)
    {
        var end = _now + by;
        while (_scheduled.Where(t => t.Due <= end).MinBy(t => t.Due) is { } next)
        {
            _now = next.Due;
            next.Fire();
        }
        _now = end;
    }

    sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        TimeSpan _period;

        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock._scheduled.Remove(this);
            if (dueTime == Timeout.InfiniteTimeSpan) return true;
            Due = clock._now + dueTime;
            _period = period;
            clock._scheduled.Add(this);
            return true;
        }

        public void Fire()
        {
            // A period of zero or infinite means the timer fires once, as a System.Threading.Timer's does.
            if (_period > TimeSpan.Zero) Due += _period;
            else clock._scheduled.Remove(this);
            callback(state);
        }

        public void Dispose() => clock._scheduled.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
