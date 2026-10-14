namespace Bibliotaph.Processing;

/// <summary>
/// What the scanner and the hasher are doing at one moment: whether folders are being looked through, whether
/// online-only files are waiting for space, and the file being read for hashing.
/// </summary>
internal sealed record IndexingActivity(bool IsScanning, bool WaitingForDiskSpace, string? Hashing)
{
    public static readonly IndexingActivity Idle = new(false, false, null);
}

/// <summary>
/// The current <see cref="IndexingActivity"/>, replaced whole, so whoever shows it reads one moment and never half of a
/// change. The scanner and the hasher each change their own part from their own thread; neither loses the other's.
/// </summary>
internal sealed class IndexingActivityState
{
    IndexingActivity _current = IndexingActivity.Idle;

    public IndexingActivity Current => Volatile.Read(ref _current);

    public void Change(Func<IndexingActivity, IndexingActivity> change)
    {
        var seen = Current;
        while (true)
        {
            var before = Interlocked.CompareExchange(ref _current, change(seen), seen);
            if (ReferenceEquals(before, seen)) return;
            seen = before;
        }
    }
}
