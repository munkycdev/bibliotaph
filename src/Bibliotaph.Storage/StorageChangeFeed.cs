using System.Collections.Concurrent;
using Bibliotaph.Processing;

namespace Bibliotaph.Storage;

/// <summary>
/// Watches library folders in connected storage through the provider's own change signals instead of the file system's.
/// A signal only says that something in an account changed, so every watched folder there hears of it and is scanned
/// again; the scanner's quiet period folds a burst of changes into one scan.
/// </summary>
public sealed class StorageChangeFeed : IFolderWatcher
{
    readonly ConcurrentDictionary<Subscription, (string Provider, string Account)> _watches = new();

    public IDisposable Watch(string folder, Action changed, Action stopped)
    {
        if (!StoragePath.TryParse(folder, out var location)) throw new PlatformNotSupportedException("Only folders in connected storage are watched here.");
        var watch = new Subscription(this, changed);
        _watches[watch] = (location.Provider, location.Account);
        return watch;
    }

    /// <summary>Something in this account changed, or changes may have been missed.</summary>
    public void Notify(string provider, string account)
    {
        foreach (var (watch, where) in _watches)
            if (where == (provider, account)) watch.Changed();
    }

    /// <summary>Every watched account, so a library can be told of changes it may have missed while it was closed.</summary>
    public void NotifyAll()
    {
        foreach (var watch in _watches.Keys) watch.Changed();
    }

    sealed class Subscription(StorageChangeFeed feed, Action changed) : IDisposable
    {
        public void Changed() => changed();

        public void Dispose() => feed._watches.TryRemove(this, out _);
    }
}
