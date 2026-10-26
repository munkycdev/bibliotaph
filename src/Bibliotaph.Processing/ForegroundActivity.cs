namespace Bibliotaph.Processing;

/// <summary>
/// Requests someone is waiting on, so background work steps aside for them. On a server with one CPU, a book being read
/// in the background would otherwise take turns with every page someone opens; between its jobs, indexing waits for a
/// quiet moment instead. It never waits long, so a library that's always in use is still read.
/// </summary>
public sealed class ForegroundActivity(TimeProvider? clock = null)
{
    /// <summary>
    /// How long after the last request background work still counts the user as busy: a page load is several calls in
    /// quick succession. Short, so a page that asks for something every second or so doesn't hold the work back.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest background work waits for a quiet moment before going ahead anyway.</summary>
    public static readonly TimeSpan MostWait = TimeSpan.FromSeconds(2);

    static readonly TimeSpan Look = TimeSpan.FromMilliseconds(25);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    int _inFlight;
    long _lastEnded;

    /// <summary>Marks a request being answered until the result is disposed.</summary>
    public IDisposable Begin()
    {
        Interlocked.Increment(ref _inFlight);
        return new Request(this);
    }

    public bool IsQuiet =>
        Volatile.Read(ref _inFlight) == 0 && _clock.GetTimestamp() - Volatile.Read(ref _lastEnded) >= Settle.TotalSeconds * _clock.TimestampFrequency;

    /// <summary>Waits until no request has been answered for a moment, or <see cref="MostWait"/> at the most.</summary>
    public async Task WaitForQuietAsync(CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        while (!IsQuiet && _clock.GetElapsedTime(started) < MostWait)
            await Task.Delay(Look, _clock, ct);
    }

    void End()
    {
        Volatile.Write(ref _lastEnded, _clock.GetTimestamp());
        Interlocked.Decrement(ref _inFlight);
    }

    sealed class Request(ForegroundActivity owner) : IDisposable
    {
        int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) owner.End();
        }
    }
}
