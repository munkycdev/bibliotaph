namespace Bibliotaph.Processing;

/// <summary>
/// An auto-reset signal: any number of Sets before a wait wake it once. With <see cref="WorkInHand"/>, the loop waiting
/// on it counts as having work in hand except while it waits, and from the moment it is set: so work handed from one
/// loop to another is never between the two, and indexing can tell when it has nothing left to do.
/// </summary>
internal sealed class Signal(WorkInHand? work = null)
{
    readonly Lock _lock = new();
    bool _set;
    TaskCompletionSource<bool>? _waiter;
    bool _waiterIdle;
    /// <summary>Whether its loop counts in <see cref="WorkInHand"/> now: from <see cref="Begin"/> to <see cref="End"/>, but for idle waits.</summary>
    bool _busy;

    /// <summary>Its loop has started, and counts as busy until it first waits.</summary>
    public void Begin()
    {
        lock (_lock) MarkBusy(true);
    }

    /// <summary>Its loop has ended, however it was doing.</summary>
    public void End()
    {
        lock (_lock) MarkBusy(false);
    }

    void MarkBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        if (busy) work?.Start();
        else work?.Stop();
    }

    public void Set()
    {
        TaskCompletionSource<bool> waiter;
        lock (_lock)
        {
            if (_waiter is null)
            {
                _set = true;
                return;
            }
            waiter = _waiter;
            _waiter = null;
            if (_waiterIdle) MarkBusy(true);
        }
        waiter.TrySetResult(true);
    }

    /// <summary>
    /// Waits to be set, or for <paramref name="timeout"/>. Returns whether it was set. The wait counts as idle unless
    /// <paramref name="idle"/> is false, for a loop that is only pausing a moment before work it already has.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct, bool idle = true)
    {
        TaskCompletionSource<bool> waiter;
        lock (_lock)
        {
            if (_set)
            {
                _set = false;
                return true;
            }
            waiter = _waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiterIdle = idle;
            if (idle) MarkBusy(false);
        }
        try
        {
            return await waiter.Task.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            lock (_lock)
            {
                // Set got there first and counted the loop busy again.
                if (_waiter != waiter) return true;
                _waiter = null;
                if (idle && ex is TimeoutException) MarkBusy(true);
            }
            if (ex is TimeoutException) return false;
            throw;
        }
    }
}

/// <summary>
/// How many of indexing's loops and errands have work in hand, so a caller can wait until none has. A loop counts from
/// its start until it waits on its <see cref="Signal"/>, and again from when that is set.
/// </summary>
internal sealed class WorkInHand
{
    readonly Lock _lock = new();
    int _busy;
    TaskCompletionSource? _idle;

    public void Start()
    {
        lock (_lock) _busy++;
    }

    public void Stop()
    {
        TaskCompletionSource? idle = null;
        lock (_lock)
        {
            if (--_busy == 0) (idle, _idle) = (_idle, null);
        }
        idle?.TrySetResult();
    }

    /// <summary>Completes once nothing has work in hand.</summary>
    public Task WhenIdleAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (_busy == 0) return Task.CompletedTask;
            _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _idle.Task.WaitAsync(ct);
        }
    }
}
