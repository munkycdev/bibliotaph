namespace Bibliotaph.Pdf.Host;

/// <summary>
/// Which viewer worker each lease uses. A new lease gets a worker of its own while fewer than the limit are running;
/// after that it shares the one with the fewest leases (the earliest started, on a tie). A worker is forgotten when
/// its last lease is released. Only the bookkeeping, so the rule can be tested without processes; not thread-safe.
/// </summary>
public sealed class ViewerLeases<T>(int maxWorkers) where T : class
{
    readonly List<Entry> _workers = [];

    sealed class Entry(T worker)
    {
        public T Worker { get; } = worker;
        public int Leases { get; set; }
    }

    public int MaxWorkers { get; } = maxWorkers > 0 ? maxWorkers : throw new ArgumentOutOfRangeException(nameof(maxWorkers));

    /// <summary>The workers that have a lease, earliest started first.</summary>
    public IReadOnlyList<T> Workers => [.. _workers.Select(e => e.Worker)];

    /// <summary>How many leases a worker has; 0 for one that isn't running.</summary>
    public int LeasesOn(T worker) => Find(worker)?.Leases ?? 0;

    /// <summary>Takes a lease, calling <paramref name="start"/> for a new worker when it gets one of its own.</summary>
    public T Acquire(Func<T> start)
    {
        Entry entry;
        if (_workers.Count < MaxWorkers)
        {
            entry = new Entry(start());
            _workers.Add(entry);
        }
        else
        {
            // MinBy keeps the first of equals, which is the earliest started.
            entry = _workers.MinBy(e => e.Leases)!;
        }
        entry.Leases++;
        return entry.Worker;
    }

    /// <summary>
    /// Gives a lease back. True when it was the worker's last: the worker is forgotten and the caller stops it.
    /// False for a worker with leases left, or one this doesn't know.
    /// </summary>
    public bool Release(T worker)
    {
        if (Find(worker) is not { } entry) return false;
        if (--entry.Leases > 0) return false;
        _workers.Remove(entry);
        return true;
    }

    Entry? Find(T worker) => _workers.Find(e => ReferenceEquals(e.Worker, worker));
}
