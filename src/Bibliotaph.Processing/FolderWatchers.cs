using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Processing;

/// <summary>
/// Watches a library folder for files arriving, changing, going or being renamed in it, so it is scanned again soon
/// after without anyone asking.
/// </summary>
public interface IFolderWatcher
{
    /// <summary>
    /// Starts watching a folder and everything under it, until the result is disposed. <paramref name="changed"/> is
    /// called, from any thread, when something in it changes or changes may have been missed. <paramref name="stopped"/>
    /// is called just before it when watching has stopped for good, as when the folder's drive went away.
    /// </summary>
    /// <exception cref="ArgumentException">The folder doesn't exist.</exception>
    /// <exception cref="IOException">The folder can't be watched, as when its drive or share is offline.</exception>
    /// <exception cref="PlatformNotSupportedException">Folders can't be watched here.</exception>
    IDisposable Watch(string folder, Action changed, Action stopped);
}

/// <summary>The file system's own change notifications, through <see cref="FileSystemWatcher"/>.</summary>
public sealed class FileSystemFolderWatcher : IFolderWatcher
{
    public IDisposable Watch(string folder, Action changed, Action stopped)
    {
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
        };
        void Changed(object? sender, EventArgs e) => changed();
        watcher.Created += Changed;
        watcher.Changed += Changed;
        watcher.Deleted += Changed;
        watcher.Renamed += Changed;
        // A buffer overflow loses events; a rescan catches up with whatever they were. Any other error, as when its
        // drive goes away, stops the watcher for good.
        watcher.Error += (_, e) =>
        {
            if (e.GetException() is not InternalBufferOverflowException) stopped();
            changed();
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }
}

/// <summary>
/// One watcher per library folder. A change in a folder scans it again once the folder has been quiet for
/// <see cref="IndexingOptions.RescanDelay"/>, so a big copy causes one scan. A watcher that stopped, as when its drive
/// went away, and one whose folder came back online or moved, is made again at the next <see cref="Sync"/>.
/// <paramref name="rescan"/> asks for a folder to be scanned again, by its root ID.
/// </summary>
internal sealed class FolderWatchers(IFolderWatcher watcher, TimeSpan rescanDelay, ILogger log, TimeProvider clock, Action<long> rescan)
{
    readonly Lock _lock = new();
    readonly Dictionary<long, (IDisposable Watcher, ITimer Debounce)> _watchers = [];
    /// <summary>Roots whose watcher stopped (its drive went away) or that came back online: watched afresh at the next sync.</summary>
    readonly HashSet<long> _stale = [];

    /// <summary>Watches a folder afresh at the next sync: its watcher watched a drive that went away, or a path it left.</summary>
    public void Renew(long rootId)
    {
        lock (_lock) _stale.Add(rootId);
    }

    /// <summary>
    /// One watcher per root. One that stopped, or whose folder came back online, is made again. Returns the offline
    /// folders that could be watched again, which want a scan.
    /// </summary>
    public List<long> Sync(IReadOnlyList<SourceRoot> current)
    {
        var reachable = new List<long>();
        lock (_lock)
        {
            foreach (var gone in _watchers.Keys.Where(id => current.All(r => r.Id != id) || _stale.Contains(id)).ToList())
            {
                _watchers[gone].Watcher.Dispose();
                _watchers[gone].Debounce.Dispose();
                _watchers.Remove(gone);
            }
            _stale.Clear();
            foreach (var root in current.Where(r => !_watchers.ContainsKey(r.Id)))
            {
                var rootId = root.Id;
                try
                {
                    var debounce = clock.CreateTimer(_ => rescan(rootId), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    void Changed() => debounce.Change(rescanDelay, Timeout.InfiniteTimeSpan);
                    // The rescan that follows finds the folder offline, and the watcher is made again once it can be.
                    void Stopped()
                    {
                        lock (_lock) _stale.Add(rootId);
                    }
                    _watchers[rootId] = (watcher.Watch(root.Path, Changed, Stopped), debounce);
                    if (root.Availability == SourceRootAvailability.Offline) reachable.Add(rootId);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
                {
                    // An offline root can't be watched; the next scan after it returns tries again.
                    log.LogDebug(ex, "Not watching library folder {RootId}", rootId);
                }
            }
        }
        return reachable;
    }

    /// <summary>Stops watching every folder; the next sync watches them afresh.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var (watching, debounce) in _watchers.Values)
            {
                watching.Dispose();
                debounce.Dispose();
            }
            _watchers.Clear();
        }
    }
}
