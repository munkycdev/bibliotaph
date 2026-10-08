namespace Bibliotaph.Core.Progress;

/// <summary>
/// Time left for a run of work, from how fast it went over the last few minutes. Silent until it has watched for
/// <see cref="WarmUp"/>, so the first few quick (or slow) items don't promise a wild figure. Time with no progress
/// counts, so a stall makes the estimate grow rather than freeze.
/// </summary>
public sealed class RateEstimator(TimeSpan? window = null, TimeSpan? warmUp = null)
{
    readonly List<(DateTimeOffset At, long Done)> _samples = [];

    public TimeSpan Window { get; } = window ?? TimeSpan.FromMinutes(3);

    public TimeSpan WarmUp { get; } = warmUp ?? TimeSpan.FromSeconds(30);

    /// <summary>Forgets what it saw, as when the work pauses; the time paused would otherwise read as slowness.</summary>
    public void Reset() => _samples.Clear();

    /// <summary>Notes how many items are done. A count that goes down means a new run, which starts over.</summary>
    public void Record(DateTimeOffset at, long done)
    {
        if (_samples.Count > 0 && done < _samples[^1].Done) _samples.Clear();
        _samples.Add((at, done));
        // Keep one sample at or beyond the window's edge, so the window stays full.
        while (_samples.Count > 2 && at - _samples[1].At >= Window) _samples.RemoveAt(0);
    }

    /// <summary>Time left for <paramref name="remaining"/> items at the recent rate, or null while there's too little to go on.</summary>
    public TimeSpan? Estimate(DateTimeOffset now, long remaining)
    {
        if (remaining <= 0) return TimeSpan.Zero;
        if (_samples.Count == 0) return null;
        var watched = now - _samples[0].At;
        var done = _samples[^1].Done - _samples[0].Done;
        if (watched < WarmUp || done <= 0) return null;
        return TimeSpan.FromSeconds(Math.Min(remaining * watched.TotalSeconds / done, TimeSpan.FromDays(30).TotalSeconds));
    }
}
