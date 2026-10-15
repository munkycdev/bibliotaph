namespace Bibliotaph.Processing;

/// <summary>
/// Whether someone is using AI right now: a question being asked of a book or the library, or a request being read as
/// a search. Background work for AI (reading the library for meaning) waits while they are and for a little after, so
/// a request the user is waiting for never queues behind it on the same model server or graphics card.
/// </summary>
public sealed class AiUse(TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Lock _lock = new();
    int _active;
    long _lastEnded = long.MinValue;

    /// <summary>Marks a request the user is waiting for; disposing the result marks it done.</summary>
    public IDisposable Begin()
    {
        lock (_lock) _active++;
        return new Use(this);
    }

    /// <summary>A request the user is waiting for is running.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_lock) return _active > 0;
        }
    }

    /// <summary>Nothing has been asked for at least <paramref name="span"/>, and nothing is being asked now.</summary>
    public bool QuietFor(TimeSpan span)
    {
        lock (_lock)
            return _active == 0 && (_lastEnded == long.MinValue || _clock.GetElapsedTime(_lastEnded) >= span);
    }

    void End()
    {
        lock (_lock)
        {
            _active--;
            _lastEnded = _clock.GetTimestamp();
        }
    }

    sealed class Use(AiUse owner) : IDisposable
    {
        int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End();
        }
    }
}
