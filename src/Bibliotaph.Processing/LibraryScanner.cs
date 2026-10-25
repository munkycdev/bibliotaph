using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Processing;

/// <summary>
/// Looks through library folders for new, changed, moved and missing files, as asked and when a watched folder
/// changes, and records what each scan found. A folder that reads as offline (unreachable, another disk in its drive,
/// or empty though it held files) keeps its files; one that comes back is scanned and watched again.
/// <paramref name="changed"/> is called when something a progress display would show has moved.
/// </summary>
internal sealed class LibraryScanner(SourceRootStore roots, LibraryStore library, ISourceLister lister, IFileIdentity identity, FolderWatchers watchers, HashingLoop hashing,
    LaneRunner lanes, IndexingActivityState activity, IndexingOptions options, ILogger log, TimeProvider clock, Action changed) : IDisposable
{
    readonly Signal _signal = new();
    readonly Lock _lock = new();
    HashSet<long>? _pendingScans; // null: every root
    bool _scanAll = true;
    /// <summary>Those waiting in <see cref="ScanNowAsync"/> for the next pass to finish.</summary>
    List<TaskCompletionSource>? _scanWaits;
    readonly Dictionary<long, RootScan> _scans = [];

    /// <summary>The last scan of each library folder.</summary>
    public IReadOnlyList<RootScan> Scans
    {
        get { lock (_lock) return [.. _scans.Values]; }
    }

    /// <summary>Wakes the scanner to take whatever was asked for.</summary>
    public void Wake() => _signal.Set();

    /// <summary>Scans one root, or every root when <paramref name="rootId"/> is null, as soon as the scanner is free.</summary>
    public void RequestScan(long? rootId = null)
    {
        lock (_lock)
        {
            if (rootId is null) _scanAll = true;
            else (_pendingScans ??= []).Add(rootId.Value);
        }
        _signal.Set();
    }

    /// <summary>Scans these roots, or every root when <paramref name="rootIds"/> is null, and returns once that scan has finished.</summary>
    public async Task ScanNowAsync(IReadOnlyCollection<long>? rootIds = null, CancellationToken ct = default)
    {
        if (rootIds is { Count: 0 }) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            // Asked for under the same lock the scan loop takes them under, so the pass that takes these roots takes this wait too.
            if (rootIds is null) _scanAll = true;
            else (_pendingScans ??= []).UnionWith(rootIds);
            (_scanWaits ??= []).Add(done);
        }
        _signal.Set();
        await done.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Looks again at every library folder that is offline, and at those on <paramref name="drives"/> (drive letters),
    /// as when a disk is plugged in or taken out.
    /// </summary>
    public async Task RescanOfflineAsync(IReadOnlyCollection<char>? drives = null, CancellationToken ct = default)
    {
        foreach (var root in await roots.ListAsync(ct))
            if (root.Availability == SourceRootAvailability.Offline || drives?.Any(d => OnDrive(root.Path, d)) == true) RequestScan(root.Id);
    }

    static bool OnDrive(string path, char drive) =>
        Path.GetPathRoot(path) is { Length: >= 2 } root && root[1] == ':' && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(drive);

    /// <summary>
    /// Points a library folder to its new place, checked as a folder to add would be (it exists, and isn't another
    /// library folder, inside one or holding one), then scans it there.
    /// </summary>
    public async Task<RootRelocation> RelocateRootAsync(long rootId, string path, CancellationToken ct = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) return RootRelocation.NotFound;
        var others = (await roots.ListAsync(ct)).Where(r => r.Id != rootId).Select(r => r.Path);
        if (others.Any(other => SourceScanner.IsUnder(full, other) || SourceScanner.IsUnder(other, full))) return RootRelocation.Overlaps;
        if (!await roots.RelocateAsync(rootId, full, ct)) return RootRelocation.WasRemoved;
        log.LogInformation("Library folder {RootId} pointed to its new place", rootId);
        lock (_lock) _scans.Remove(rootId);
        watchers.Renew(rootId);
        RequestScan(rootId);
        changed();
        return RootRelocation.Relocated;
    }

    /// <summary>Scans what was asked for each time it is woken, then has what it found hashed.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (true)
        {
            await _signal.WaitAsync(Timeout.InfiniteTimeSpan, ct);
            bool all;
            HashSet<long>? some;
            List<TaskCompletionSource>? waits;
            lock (_lock)
            {
                (all, some, waits) = (_scanAll, _pendingScans, _scanWaits);
                (_scanAll, _pendingScans, _scanWaits) = (false, null, null);
            }

            try
            {
                var current = await roots.ListAsync(ct);
                // A folder that was offline and can be watched again is scanned now: a watcher only reports changes from then on.
                if (options.WatchFolders) foreach (var rootId in watchers.Sync(current)) (some ??= []).Add(rootId);
                activity.Change(a => a with { IsScanning = true });
                changed();
                try
                {
                    // A folder a file may have moved from is scanned in the same pass, before the file is read at its new place.
                    var pass = current.Where(r => all || some?.Contains(r.Id) == true).ToList();
                    for (var i = 0; i < pass.Count; i++)
                        foreach (var from in await ScanRootAsync(pass[i], current, ct))
                            if (pass.All(r => r.Id != from) && current.FirstOrDefault(r => r.Id == from) is { } source) pass.Add(source);
                }
                finally
                {
                    activity.Change(a => a with { IsScanning = false });
                    changed();
                }
            }
            finally
            {
                // Done or failed, the pass is over: nobody waits on it for ever.
                foreach (var wait in waits ?? []) wait.TrySetResult();
            }
            // A folder back online is watched again now rather than at the next scan.
            if (options.WatchFolders) foreach (var rootId in watchers.Sync(await roots.ListAsync(ct))) RequestScan(rootId);
            await lanes.ReleaseReachableAsync(ct);
            hashing.Scanned();
        }
    }

    /// <summary>Every few minutes, offline folders are looked at again, for a network share that has come back.</summary>
    public async Task OfflineLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(options.OfflineRecheck, clock, ct);
            await RescanOfflineAsync(ct: ct);
        }
    }

    /// <summary>
    /// Scans a root, unless it reads as offline: it can't be listed, its drive holds another disk than at its first
    /// scan, or it lists as empty though the catalog has files in it. Then nothing is marked missing. File IDs are
    /// read only where its disk keeps them, so a moved file is followed rather than read.
    /// </summary>
    /// <returns>The other folders a file new here may have moved from (<see cref="ReconcileResult.MovedFrom"/>).</returns>
    async Task<IReadOnlyList<long>> ScanRootAsync(SourceRoot root, IReadOnlyList<SourceRoot> all, CancellationToken ct)
    {
        var others = all.Where(r => r.Id != root.Id).Select(r => r.Path).ToList();
        var result = await SourceScanner.ScanAsync(lister, root.Path, others, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        // A folder whose own listing fails is as good as unreachable.
        if (result is null || result.Summary.Inaccessible.Contains("."))
        {
            await MarkOfflineAsync(root, OfflineReason.Unreachable, now, ct);
            return [];
        }
        var volume = await Task.Run(() => identity.Volume(root.Path), ct);
        if (root.VolumeSerial is { } serial && volume is not null && !string.Equals(volume.Serial, serial, StringComparison.OrdinalIgnoreCase))
        {
            await MarkOfflineAsync(root, OfflineReason.DifferentDisk, now, ct);
            return [];
        }
        if (result.Files.Count == 0 && await library.HasFilesAsync(root.Id, ct))
        {
            await MarkOfflineAsync(root, OfflineReason.LooksEmpty, now, ct);
            return [];
        }

        Func<string, string?>? fileId = volume is { HasFileIds: true } ? relative => identity.FileId(Path.Combine(root.Path, relative)) : null;
        // On a local drive every file's ID is read again, so a file saved over another with the same size and date is
        // seen; on a network share each read is a round trip, so only new and changed files are asked.
        var changes = await library.ReconcileRootAsync(root.Id, result.Files, result.Summary.Inaccessible, fileId, volume?.Serial,
            readKnownIds: volume is { IsLocal: true }, ct: ct);
        log.LogInformation("Scanned library folder {RootId}: {Files} files, {Changes}", root.Id, result.Files.Count, changes);
        if (root.Availability == SourceRootAvailability.Offline)
        {
            log.LogInformation("Library folder {RootId} is back online", root.Id);
            // Its watcher, if it had one, watched a drive that went away.
            watchers.Renew(root.Id);
        }
        // Anything that couldn't be read gets another try now its folder has been looked at again: forgotten first, so
        // whoever sees this scan doesn't see them still waiting on it.
        hashing.ForgetUnreadable(root.Path);
        lock (_lock) _scans[root.Id] = new RootScan(root.Id, root.Path, true, result.Summary, changes, now);
        return changes.MovedFrom ?? [];
    }

    async Task MarkOfflineAsync(SourceRoot root, OfflineReason reason, DateTime now, CancellationToken ct)
    {
        log.LogInformation("Library folder {RootId} is offline: {Reason}", root.Id, reason);
        await library.SetRootAvailabilityAsync(root.Id, SourceRootAvailability.Offline, ct);
        lock (_lock) _scans[root.Id] = new RootScan(root.Id, root.Path, false, null, null, now, reason);
    }

    public void Dispose() => _signal.Dispose();
}
