using System.Collections.Concurrent;
using Bibliotaph.Processing;

namespace Bibliotaph.Storage.OneDrive;

/// <summary>
/// Watches library folders on OneDrives through Graph's change notifications instead of the file system's. A
/// notification only says that something in a drive changed, so every watched folder on that drive hears of it and is
/// scanned again; the scanner's quiet period folds a burst of changes into one scan.
/// </summary>
public sealed class DriveChangeFeed : IFolderWatcher
{
    readonly ConcurrentDictionary<Subscription, string> _watches = new();

    public IDisposable Watch(string folder, Action changed, Action stopped)
    {
        if (!DrivePath.TryParse(folder, out var driveId, out _)) throw new PlatformNotSupportedException("Only folders on a OneDrive are watched here.");
        var watch = new Subscription(this, changed);
        _watches[watch] = driveId;
        return watch;
    }

    /// <summary>Something in this drive changed, or changes may have been missed.</summary>
    public void Notify(string driveId)
    {
        foreach (var (watch, drive) in _watches)
            if (drive == driveId) watch.Changed();
    }

    /// <summary>Every watched drive, so a library can be told of changes it may have missed while it was closed.</summary>
    public void NotifyAll()
    {
        foreach (var watch in _watches.Keys) watch.Changed();
    }

    sealed class Subscription(DriveChangeFeed feed, Action changed) : IDisposable
    {
        public void Changed() => changed();

        public void Dispose() => feed._watches.TryRemove(this, out _);
    }
}
