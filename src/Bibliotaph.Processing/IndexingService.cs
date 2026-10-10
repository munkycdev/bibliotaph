using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

public sealed record IndexingOptions
{
    /// <summary>Files hashed between checks for pause and new scans.</summary>
    public int HashBatch { get; init; } = 32;

    /// <summary>Online-only files stop downloading when their drive would have less than this free (spec §OneDrive).</summary>
    public long OnlineOnlyReserveBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How long a job may run before its lease would count as abandoned. Leases only matter across restarts.</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How often an idle loop looks again without being told to, for backoffs ending and disk space freeing up.</summary>
    public TimeSpan IdleRecheck { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Quiet time after a change in a watched folder before it is scanned again, so a big copy causes one scan.</summary>
    public TimeSpan RescanDelay { get; init; } = TimeSpan.FromSeconds(3);

    public bool WatchFolders { get; init; } = true;

    /// <summary>How long a lane whose work can't be done right now (a model endpoint that isn't running) waits before trying again.</summary>
    public TimeSpan UnavailableRetry { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often offline folders are looked at again (slice 4g plan, choice 4), for a network share coming back, which
    /// Windows doesn't announce as it does a disk being plugged in.
    /// </summary>
    public TimeSpan OfflineRecheck { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Why a library folder reads as offline (slice 4g plan, choice 3). Its files are never marked missing for it.</summary>
public enum OfflineReason
{
    /// <summary>The folder, or its drive or share, can't be opened.</summary>
    Unreachable,
    /// <summary>The drive holds another disk than it did when the folder was first scanned: its volume serial differs.</summary>
    DifferentDisk,
    /// <summary>It lists as empty though it held files, as a disk that hasn't finished mounting or a share showing nothing.</summary>
    LooksEmpty,
}

/// <summary>The last scan of a root: what it holds, what changed, or that it couldn't be reached and why.</summary>
public sealed record RootScan(long RootId, string Path, bool Reachable, ScanSummary? Summary, ReconcileResult? Changes, DateTime ScannedUtc,
    OfflineReason? Offline = null);

/// <summary>What "Point to its new place" did with a folder (slice 4g plan, choice 6).</summary>
public enum RootRelocation
{
    Relocated,
    /// <summary>The folder picked doesn't exist or can't be opened.</summary>
    NotFound,
    /// <summary>It is another library folder, inside one, or holds one.</summary>
    Overlaps,
    /// <summary>It was a library folder of its own before it was removed; adding it again brings that one back.</summary>
    WasRemoved,
}

/// <summary>A file the hasher could not read. It is tried again after the next scan of its folder.</summary>
public sealed record UnreadableFile(long LocationId, string Path, string Reason);

/// <summary>
/// Runs the library: scans roots (and rescans when a watched folder changes), hashes new and changed files into
/// documents, and works the job queue in three lanes, Index (Probe, Text, Covers, RuleHints), OCR and Classify, each
/// pausable on its own. Classify works only while AI is ready, and waits with a notice while its endpoint can't be used.
/// Leases left by a previous run go back to pending before anything else starts (A11).
/// </summary>
public sealed class IndexingService(
    SourceRootStore roots,
    LibraryStore library,
    JobBoard queue,
    IEnumerable<IStage> stages,
    FileHasher hasher,
    IDiskSpace disk,
    ArchiveReader archives,
    IndexingOptions? options = null,
    ILogger<IndexingService>? log = null,
    TimeProvider? clock = null,
    PackService? packs = null,
    IFileIdentity? identity = null) : BackgroundService
{
    readonly IndexingOptions _options = options ?? new IndexingOptions();
    readonly IFileIdentity _identity = identity ?? new FileIdentity();
    readonly ILogger _log = log ?? NullLogger<IndexingService>.Instance;
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Dictionary<Stage, IStage> _stages = stages.ToDictionary(s => s.Stage);
    readonly Lock _lock = new();
    bool _gatesWatched;

    readonly Signal _scanSignal = new();
    readonly Signal _hashSignal = new();
    readonly Dictionary<Lane, Signal> _laneSignals = Enum.GetValues<Lane>().ToDictionary(l => l, _ => new Signal());
    readonly Dictionary<Lane, LaneControl> _lanes = Enum.GetValues<Lane>().ToDictionary(l => l, _ => new LaneControl());

    /// <summary>Set when files may have arrived, moved or been hashed, so folders are looked at for packs (F4) after hashing.</summary>
    volatile bool _packsDue = true;
    HashSet<long>? _pendingScans; // null: every root
    bool _scanAll = true;
    readonly Dictionary<long, RootScan> _scans = [];
    readonly Dictionary<long, UnreadableFile> _unreadable = [];
    readonly Dictionary<long, (FileSystemWatcher Watcher, ITimer Debounce)> _watchers = [];
    /// <summary>Roots whose watcher stopped (its drive went away) or that came back online: watched afresh at the next sync.</summary>
    readonly HashSet<long> _staleWatchers = [];
    CancellationToken _stopping;

    /// <summary>Something changed that a progress display would show. Raised on a background thread, often; throttle.</summary>
    public event EventHandler? Changed;

    /// <summary>Wakes a lane whose gate may have opened, as when AI is switched on, and clears what it was waiting for.</summary>
    void OnGateChanged(Lane lane)
    {
        lock (_lock) _lanes[lane].Unavailable = null;
        _laneSignals[lane].Set();
        RaiseChanged();
    }

    /// <summary>Whether a lane may work: every gated stage in it is ready. A lane with no gated stage always may.</summary>
    public bool IsOpen(Lane lane) =>
        _stages.Values.Where(s => Pipeline.LaneOf(s.Stage) == lane).OfType<IGatedStage>().All(s => s.IsReady);

    /// <summary>Why a lane is waiting even though it has work, such as a model endpoint that isn't answering; null otherwise.</summary>
    public string? Unavailable(Lane lane)
    {
        lock (_lock) return _lanes[lane].Unavailable;
    }

    public bool IsScanning { get; private set; }

    /// <summary>True when online-only files are waiting because their drive is short of space.</summary>
    public bool WaitingForDiskSpace { get; private set; }

    public IReadOnlyList<RootScan> Scans
    {
        get { lock (_lock) return [.. _scans.Values]; }
    }

    public IReadOnlyList<UnreadableFile> Unreadable
    {
        get { lock (_lock) return [.. _unreadable.Values]; }
    }

    /// <summary>The file being read for hashing, if any.</summary>
    public string? Hashing { get; private set; }

    /// <summary>The job a lane is running, if any.</summary>
    public JobRecord? Running(Lane lane)
    {
        lock (_lock) return _lanes[lane].Running;
    }

    public bool IsPaused(Lane lane)
    {
        lock (_lock) return _lanes[lane].Paused;
    }

    /// <summary>Pauses a lane. Its running job stops at the next page and goes back to the queue without using an attempt.</summary>
    public void Pause(Lane lane)
    {
        lock (_lock)
        {
            var control = _lanes[lane];
            if (control.Paused) return;
            control.Paused = true;
            control.Cancel.Cancel();
        }
        RaiseChanged();
    }

    public void Resume(Lane lane)
    {
        lock (_lock)
        {
            var control = _lanes[lane];
            if (!control.Paused) return;
            control.Paused = false;
            control.Cancel.Dispose();
            control.Cancel = new CancellationTokenSource();
        }
        _laneSignals[lane].Set();
        if (lane == Lane.Index) _hashSignal.Set();
        RaiseChanged();
    }

    /// <summary>Scans one root, or every root when <paramref name="rootId"/> is null, as soon as the scanner is free.</summary>
    public void RequestScan(long? rootId = null)
    {
        lock (_lock)
        {
            if (rootId is null) _scanAll = true;
            else (_pendingScans ??= []).Add(rootId.Value);
        }
        _scanSignal.Set();
    }

    /// <summary>Runs a stage again for every document that has been through it, as after a vocabulary edit.</summary>
    public async Task<int> RerunAsync(Stage stage, CancellationToken ct = default)
    {
        var queued = await queue.RerunAsync(stage, ct);
        _laneSignals[Pipeline.LaneOf(stage)].Set();
        RaiseChanged();
        return queued;
    }

    /// <summary>Gives a document's failed and blocked stages another go, as from Files needing attention.</summary>
    public async Task RetryAsync(long documentId, CancellationToken ct = default)
    {
        await queue.RetryAsync(documentId, ct);
        foreach (var signal in _laneSignals.Values) signal.Set();
        RaiseChanged();
    }

    /// <summary>
    /// Looks again at every library folder that is offline, and at those on <paramref name="drives"/> (drive letters),
    /// as when a disk is plugged in or taken out (slice 4g plan, choice 4). One that can be reached again comes back
    /// with its books without Look for changes; one whose disk went reads as offline at once.
    /// </summary>
    public async Task RescanOfflineAsync(IReadOnlyCollection<char>? drives = null, CancellationToken ct = default)
    {
        foreach (var root in await roots.ListAsync(ct))
            if (root.Availability == SourceRootAvailability.Offline || drives?.Any(d => OnDrive(root.Path, d)) == true) RequestScan(root.Id);
    }

    static bool OnDrive(string path, char drive) =>
        Path.GetPathRoot(path) is { Length: >= 2 } root && root[1] == ':' && char.ToUpperInvariant(root[0]) == char.ToUpperInvariant(drive);

    /// <summary>
    /// "Point to its new place" (slice 4g plan, choice 6), for a library folder that moved for good, as to a new disk:
    /// checked as a folder to add would be (it exists, and isn't another library folder, inside one or holding one),
    /// then the folder takes the new path and is scanned there. Its files are matched by their paths under it, so
    /// every book keeps its hash, card and everything done with it, and nothing is read again unless it changed. Slice
    /// 4j's restore points folders at their new places through this too.
    /// </summary>
    public async Task<RootRelocation> RelocateRootAsync(long rootId, string path, CancellationToken ct = default)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full)) return RootRelocation.NotFound;
        var others = (await roots.ListAsync(ct)).Where(r => r.Id != rootId).Select(r => r.Path);
        if (others.Any(other => SourceScanner.IsUnder(full, other) || SourceScanner.IsUnder(other, full))) return RootRelocation.Overlaps;
        if (!await roots.RelocateAsync(rootId, full, ct)) return RootRelocation.WasRemoved;
        _log.LogInformation("Library folder {RootId} pointed to its new place", rootId);
        lock (_lock)
        {
            _scans.Remove(rootId);
            _staleWatchers.Add(rootId);
        }
        RequestScan(rootId);
        RaiseChanged();
        return RootRelocation.Relocated;
    }

    /// <summary>Lists a folder for the Add folder preview, without adding it or opening any file.</summary>
    public async Task<ScanResult?> PreviewAsync(string folder, CancellationToken ct = default)
    {
        var others = (await roots.ListAsync(ct)).Select(r => r.Path).ToList();
        return await Task.Run(() => SourceScanner.Scan(folder, others, ct), ct);
    }

    /// <summary>
    /// Reprocess, from the inspector: reads a document's file again from the start, ahead of other work, keeping
    /// everything the user added (<see cref="JobBoard.ReprocessAsync"/>). Its folders are scanned too, so a file that
    /// changed since it was indexed becomes a new version, as on any rescan. False while one of its stages is running.
    /// </summary>
    public async Task<bool> ReprocessAsync(long documentId, bool ocrEveryPage = false, CancellationToken ct = default)
    {
        if (!await queue.ReprocessAsync(documentId, ocrEveryPage, ct)) return false;
        _log.LogInformation("Reprocessing document {DocumentId} (OCR on every page: {EveryPage})", documentId, ocrEveryPage);
        foreach (var rootId in await library.GetRootIdsAsync(documentId, ct)) RequestScan(rootId);
        foreach (var signal in _laneSignals.Values) signal.Set();
        RaiseChanged();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var recovered = await queue.RecoverLeasesAsync(stoppingToken);
        if (recovered > 0) _log.LogInformation("Returned {Count} interrupted jobs to the queue", recovered);
        // A new index.db beside a catalog with books in it, as after a restore without the index: each is read again.
        var unqueued = await queue.EnqueueUnqueuedAsync(await library.GetDocumentHashesAsync(stoppingToken), Pipeline.First, stoppingToken);
        if (unqueued > 0) _log.LogInformation("Queued {Count} documents index.db had no record of, to be read again", unqueued);
        // A library indexed before rule hints existed gets them without re-probing anything.
        if (_stages.ContainsKey(Stage.RuleHints))
        {
            var backfilled = await queue.EnqueueMissingAsync(Stage.RuleHints, after: Stage.Probe, stoppingToken);
            if (backfilled > 0) _log.LogInformation("Queued hints from names for {Count} documents", backfilled);
        }
        // Likewise classification, for books read before it existed; they wait in the queue until AI is set up.
        if (_stages.ContainsKey(Stage.Classify))
        {
            var backfilled = await queue.EnqueueMissingAsync(Stage.Classify, after: Stage.Text, stoppingToken);
            if (backfilled > 0) _log.LogInformation("Queued classification for {Count} documents", backfilled);
        }
        // And looking for other copies, for books read before F2: from the text already stored, so no file is read again.
        if (_stages.ContainsKey(Stage.Match))
        {
            var backfilled = await queue.EnqueueMissingAsync(Stage.Match, after: Stage.Text, stoppingToken);
            if (backfilled > 0) _log.LogInformation("Queued the search for other copies for {Count} documents", backfilled);
        }
        WatchGates();

        _scanSignal.Set();
        // Each loop on the thread pool: none of this may run on the UI thread that started the host.
        await Task.WhenAll(
            Task.Run(() => LoopAsync("scanner", ScanLoopAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("offline check", OfflineLoopAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("hasher", HashLoopAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("index lane", ct => LaneLoopAsync(Lane.Index, ct), stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("OCR lane", ct => LaneLoopAsync(Lane.Ocr, ct), stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("Classify lane", ct => LaneLoopAsync(Lane.Classify, ct), stoppingToken), CancellationToken.None));
    }

    void WatchGates()
    {
        lock (_lock)
        {
            if (_gatesWatched) return;
            _gatesWatched = true;
        }
        foreach (var gated in _stages.Values.OfType<IGatedStage>())
        {
            var lane = Pipeline.LaneOf(gated.Stage);
            gated.ReadyChanged += (_, _) => OnGateChanged(lane);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        lock (_lock)
        {
            foreach (var (watcher, debounce) in _watchers.Values)
            {
                watcher.Dispose();
                debounce.Dispose();
            }
            _watchers.Clear();
        }
    }

    /// <summary>Keeps a loop alive through unexpected errors, so one bad file can't stop indexing until restart.</summary>
    async Task LoopAsync(string name, Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await loop(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "The {Loop} failed; restarting it", name);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _clock, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    // ---- Scanning -------------------------------------------------------------------------------------------------

    async Task ScanLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await _scanSignal.WaitAsync(Timeout.InfiniteTimeSpan, ct);
            bool all;
            HashSet<long>? some;
            lock (_lock)
            {
                (all, some) = (_scanAll, _pendingScans);
                (_scanAll, _pendingScans) = (false, null);
            }

            var current = await roots.ListAsync(ct);
            // A folder that was offline and can be watched again is scanned now: a watcher only reports changes from then on.
            if (_options.WatchFolders) foreach (var rootId in SyncWatchers(current)) (some ??= []).Add(rootId);
            IsScanning = true;
            RaiseChanged();
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
                IsScanning = false;
                RaiseChanged();
            }
            // A folder back online is watched again now rather than at the next scan.
            if (_options.WatchFolders) foreach (var rootId in SyncWatchers(await roots.ListAsync(ct))) RequestScan(rootId);
            await ReleaseReachableAsync(ct);
            _packsDue = true;
            _hashSignal.Set();
        }
    }

    /// <summary>Every few minutes, offline folders are looked at again, for a network share that has come back.</summary>
    async Task OfflineLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(_options.OfflineRecheck, _clock, ct);
            await RescanOfflineAsync(ct: ct);
        }
    }

    /// <summary>
    /// Scans a root, unless it reads as offline (A07, slice 4g plan, choice 3): it can't be listed, its drive holds
    /// another disk than at its first scan, or it lists as empty though the catalog has files in it. Then nothing is
    /// marked missing. File IDs are read only where its disk keeps them, so a moved file is followed rather than read.
    /// </summary>
    /// <returns>The other folders a file new here may have moved from (<see cref="ReconcileResult.MovedFrom"/>).</returns>
    async Task<IReadOnlyList<long>> ScanRootAsync(SourceRoot root, IReadOnlyList<SourceRoot> all, CancellationToken ct)
    {
        var others = all.Where(r => r.Id != root.Id).Select(r => r.Path).ToList();
        var result = await Task.Run(() => SourceScanner.Scan(root.Path, others, ct), ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        // A folder whose own listing fails is as good as unreachable.
        if (result is null || result.Summary.Inaccessible.Contains("."))
        {
            await MarkOfflineAsync(root, OfflineReason.Unreachable, now, ct);
            return [];
        }
        var volume = await Task.Run(() => _identity.Volume(root.Path), ct);
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

        Func<string, string?>? fileId = volume is { HasFileIds: true } ? relative => _identity.FileId(Path.Combine(root.Path, relative)) : null;
        // On a local drive every file's ID is read again, so a file saved over another with the same size and date is
        // seen (slice 4h plan, choice 6); on a network share each read is a round trip, so only new and changed files are asked.
        var changes = await library.ReconcileRootAsync(root.Id, result.Files, result.Summary.Inaccessible, fileId, volume?.Serial,
            readKnownIds: volume is { IsLocal: true }, ct: ct);
        _log.LogInformation("Scanned library folder {RootId}: {Files} files, {Changes}", root.Id, result.Files.Count, changes);
        if (root.Availability == SourceRootAvailability.Offline)
        {
            _log.LogInformation("Library folder {RootId} is back online", root.Id);
            // Its watcher, if it had one, watched a drive that went away.
            lock (_lock) _staleWatchers.Add(root.Id);
        }
        lock (_lock)
        {
            _scans[root.Id] = new RootScan(root.Id, root.Path, true, result.Summary, changes, now);
            // Anything that couldn't be read gets another try now its folder has been looked at again.
            foreach (var stale in _unreadable.Values.Where(u => u.Path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase)).ToList())
                _unreadable.Remove(stale.LocationId);
        }
        return changes.MovedFrom ?? [];
    }

    async Task MarkOfflineAsync(SourceRoot root, OfflineReason reason, DateTime now, CancellationToken ct)
    {
        _log.LogInformation("Library folder {RootId} is offline: {Reason}", root.Id, reason);
        await library.SetRootAvailabilityAsync(root.Id, SourceRootAvailability.Offline, ct);
        lock (_lock) _scans[root.Id] = new RootScan(root.Id, root.Path, false, null, null, now, reason);
    }

    /// <summary>
    /// One watcher per root. One that stopped, or whose folder came back online, is made again. Returns the offline
    /// folders that could be watched again, which want a scan.
    /// </summary>
    List<long> SyncWatchers(IReadOnlyList<SourceRoot> current)
    {
        var reachable = new List<long>();
        lock (_lock)
        {
            foreach (var gone in _watchers.Keys.Where(id => current.All(r => r.Id != id) || _staleWatchers.Contains(id)).ToList())
            {
                _watchers[gone].Watcher.Dispose();
                _watchers[gone].Debounce.Dispose();
                _watchers.Remove(gone);
            }
            _staleWatchers.Clear();
            foreach (var root in current.Where(r => !_watchers.ContainsKey(r.Id)))
            {
                var rootId = root.Id;
                try
                {
                    var debounce = _clock.CreateTimer(_ => RequestScan(rootId), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    var watcher = new FileSystemWatcher(root.Path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                        InternalBufferSize = 64 * 1024,
                    };
                    void Changed(object? sender, EventArgs e) => debounce.Change(_options.RescanDelay, Timeout.InfiniteTimeSpan);
                    watcher.Created += Changed;
                    watcher.Changed += Changed;
                    watcher.Deleted += Changed;
                    watcher.Renamed += Changed;
                    // A buffer overflow loses events; a rescan catches up with whatever they were. Any other error, as
                    // when its drive goes away, stops the watcher for good: the rescan finds the folder offline, and
                    // the watcher is made again once it can be.
                    watcher.Error += (sender, e) =>
                    {
                        if (e.GetException() is not InternalBufferOverflowException)
                            lock (_lock) _staleWatchers.Add(rootId);
                        Changed(sender, e);
                    };
                    watcher.EnableRaisingEvents = true;
                    _watchers[rootId] = (watcher, debounce);
                    if (root.Availability == SourceRootAvailability.Offline) reachable.Add(rootId);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
                {
                    // An offline root can't be watched; the next scan after it returns tries again.
                    _log.LogDebug(ex, "Not watching library folder {RootId}", rootId);
                }
            }
        }
        return reachable;
    }

    // ---- Hashing --------------------------------------------------------------------------------------------------

    async Task HashLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await _hashSignal.WaitAsync(_options.IdleRecheck, ct);
            if (IsPaused(Lane.Index)) continue;
            await HashPendingAsync(ct);
            await PackAsync(ct);
        }
    }

    /// <summary>
    /// Makes folders and ZIPs of many images one card (F4 plan, choice 2), once what's there has been hashed. A failure
    /// is logged and tried again next time, so it never stops hashing.
    /// </summary>
    async Task PackAsync(CancellationToken ct)
    {
        if (packs is null || !_packsDue || IsPaused(Lane.Index)) return;
        _packsDue = false;
        try
        {
            await packs.PlanAsync(ct);
            RaiseChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _log.LogError(ex, "Looking for image packs failed");
            _packsDue = true;
        }
    }

    async Task HashPendingAsync(CancellationToken ct)
    {
        // Online-only files skipped for space this pass; re-checked on the next pass.
        var deferred = new HashSet<long>();
        var waitingForSpace = false;
        try
        {
            while (!IsPaused(Lane.Index))
            {
                HashSet<long> skip;
                lock (_lock) skip = [.. _unreadable.Keys, .. deferred];
                var batch = (await library.NextUnhashedAsync(_options.HashBatch + skip.Count, includeOnlineOnly: true, ct))
                    .Where(f => !skip.Contains(f.LocationId))
                    .Take(_options.HashBatch)
                    .ToList();
                if (batch.Count == 0) return;

                foreach (var file in batch)
                {
                    if (IsPaused(Lane.Index)) return;
                    if (file.OnlineOnly && disk.FreeBytes(file.FullPath) is { } free && free - file.SizeBytes < _options.OnlineOnlyReserveBytes)
                    {
                        deferred.Add(file.LocationId);
                        waitingForSpace = true;
                        continue;
                    }
                    await HashFileAsync(file, ct);
                }
                // A changed file hashed back to its old content is that document's file again.
                await ReleaseReachableAsync(ct);
                _laneSignals[Lane.Index].Set();
                RaiseChanged();
            }
        }
        finally
        {
            if (WaitingForDiskSpace != waitingForSpace)
            {
                WaitingForDiskSpace = waitingForSpace;
                if (waitingForSpace) _log.LogWarning("Online-only files are waiting: less than {Reserve} bytes would be free", _options.OnlineOnlyReserveBytes);
                RaiseChanged();
            }
        }
    }

    async Task HashFileAsync(UnhashedFile file, CancellationToken ct)
    {
        if (file.IsArchive)
        {
            await HashArchiveAsync(file, ct);
            return;
        }
        ContentHash hash;
        Hashing = file.FullPath;
        RaiseChanged();
        try
        {
            hash = await hasher.HashAsync(file.FullPath, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not read {LocationId} for hashing: {Reason}", file.LocationId, ex.Message);
            lock (_lock) _unreadable[file.LocationId] = new UnreadableFile(file.LocationId, file.FullPath, ex.Message);
            return;
        }
        finally
        {
            Hashing = null;
        }

        await AttachAsync(file, hash, ct);
    }

    async Task AttachAsync(UnhashedFile file, ContentHash hash, CancellationToken ct)
    {
        var attached = await library.AttachHashAsync(file, hash, ct);
        _packsDue = true;
        // Idempotent: a copy of a known document finds its Probe job already there.
        if (attached is { } document) await queue.EnqueueAsync(document.DocumentId, hash.Hex, Pipeline.First, ct: ct);
    }

    /// <summary>
    /// Reads a ZIP (F3 plan, choice 3): its own hash, then the list of what it holds, then each new or changed PDF and
    /// image hashed as it decompresses. The ZIP counts as read only once every member is, so a pause part way picks up
    /// with the members still to do.
    /// </summary>
    async Task HashArchiveAsync(UnhashedFile archive, CancellationToken ct)
    {
        Hashing = archive.FullPath;
        RaiseChanged();
        try
        {
            var hash = await hasher.HashAsync(archive.FullPath, ct);
            var members = await Task.Run(() => archives.List(archive.FullPath), ct);
            if (await library.ReconcileArchiveAsync(archive, members, ct) is not { } pending) return;
            if (pending.Count > 0)
            {
                using var zip = archives.Open(archive.FullPath);
                foreach (var member in pending)
                {
                    if (IsPaused(Lane.Index)) return;
                    Hashing = member.FullPath;
                    RaiseChanged();
                    ContentHash memberHash;
                    try
                    {
                        await using var stream = ArchiveReader.OpenMember(zip, member.EntryPath!);
                        memberHash = await FileHasher.HashAsync(stream, ct);
                    }
                    catch (InvalidDataException ex)
                    {
                        _log.LogWarning("A file inside ZIP {LocationId} is damaged: {Reason}", archive.LocationId, ex.Message);
                        await library.SetProblemAsync(member.LocationId, ArchiveReader.Damaged, ct);
                        continue;
                    }
                    await AttachAsync(member, memberHash, ct);
                }
            }
            await library.AttachArchiveHashAsync(archive, hash, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var reason = ex is InvalidDataException ? "it is damaged, or isn't a ZIP Bibliotaph can read" : ex.Message;
            _log.LogWarning("Could not read ZIP {LocationId}: {Reason}", archive.LocationId, ex.Message);
            lock (_lock) _unreadable[archive.LocationId] = new UnreadableFile(archive.LocationId, archive.FullPath, reason);
        }
        finally
        {
            Hashing = null;
        }
    }

    /// <summary>
    /// Gives stages blocked because no copy of their file could be read another go, for each document whose file
    /// can be read again: its folder is back online, a scan found the file again, or it was hashed back to the document.
    /// </summary>
    async Task ReleaseReachableAsync(CancellationToken ct)
    {
        var blocked = await queue.BlockedForAsync(StageOutcome.Blocked.Unreachable, ct);
        if (blocked.Count == 0) return;
        var readable = await library.GetReadableAsync(blocked, ct);
        if (readable.Count == 0) return;
        var released = await queue.UnblockAsync(readable, StageOutcome.Blocked.Unreachable, ct);
        _log.LogInformation("Released {Count} stages whose files can be read again", released);
        foreach (var signal in _laneSignals.Values) signal.Set();
        RaiseChanged();
    }

    // ---- Lanes ----------------------------------------------------------------------------------------------------

    async Task LaneLoopAsync(Lane lane, CancellationToken stoppingToken)
    {
        var names = _stages.Keys.Where(s => Pipeline.LaneOf(s) == lane).ToArray();
        if (names.Length == 0) return;
        var owner = $"{Environment.ProcessId}/{lane}";

        while (true)
        {
            CancellationToken laneToken;
            string? unavailable;
            lock (_lock) (laneToken, unavailable) = (_lanes[lane].Paused ? default : _lanes[lane].Cancel.Token, _lanes[lane].Unavailable);
            if (laneToken == default)
            {
                await _laneSignals[lane].WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }
            if (!IsOpen(lane))
            {
                // Its gate opening (AI switched on) wakes it; nothing else needs to.
                await _laneSignals[lane].WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }
            if (unavailable is not null && !await WaitOutAsync(lane, stoppingToken)) continue;

            var job = await queue.LeaseAsync(names, owner, _options.Lease, stoppingToken);
            if (job is null)
            {
                var next = await queue.NextReadyAsync(names, stoppingToken);
                var wait = next is null ? _options.IdleRecheck : next.Value - _clock.GetUtcNow();
                wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > _options.IdleRecheck ? _options.IdleRecheck : wait;
                await _laneSignals[lane].WaitAsync(wait, stoppingToken);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(laneToken, stoppingToken);
            lock (_lock) _lanes[lane].Running = job;
            RaiseChanged();
            try
            {
                await RunJobAsync(job, linked.Token);
            }
            finally
            {
                lock (_lock) _lanes[lane].Running = null;
            }
            RaiseChanged();
        }
    }

    /// <summary>
    /// A lane that couldn't work waits before trying again, unless woken first. Returns whether to try now; the next job
    /// that gets through clears the notice.
    /// </summary>
    async Task<bool> WaitOutAsync(Lane lane, CancellationToken ct)
    {
        DateTimeOffset until;
        lock (_lock) until = _lanes[lane].RetryAt;
        var wait = until - _clock.GetUtcNow();
        if (wait <= TimeSpan.Zero) return true;
        await _laneSignals[lane].WaitAsync(wait, ct);
        return false;
    }

    async Task RunJobAsync(JobRecord job, CancellationToken ct)
    {
        StageOutcome outcome;
        try
        {
            outcome = await _stages[job.Stage].RunAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Paused or closing: back to the queue as if it never started.
            await TryAsync(() => queue.ReleaseAsync(job, CancellationToken.None));
            return;
        }
        catch (PagePoisonedException ex)
        {
            outcome = new StageOutcome.Failed(ex.Message, Retry: false);
        }
        catch (WorkerException ex)
        {
            outcome = new StageOutcome.Failed($"The PDF engine stopped: {ex.Message}", Retry: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome = new StageOutcome.Failed($"The file could not be read: {ex.Message}", Retry: true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Stage} failed for document {DocumentId}", job.Stage, job.DocumentId);
            outcome = new StageOutcome.Failed($"Unexpected error: {ex.Message}", Retry: true);
        }

        if (outcome is not StageOutcome.Done { Status: StageStatus.Complete } and not StageOutcome.Later)
            _log.LogInformation("{Stage} for document {DocumentId}: {Outcome}", job.Stage, job.DocumentId, outcome);

        var lane = Pipeline.LaneOf(job.Stage);
        lock (_lock)
        {
            var control = _lanes[lane];
            if (outcome is StageOutcome.Unavailable unavailable)
            {
                control.Unavailable = unavailable.Reason;
                control.RetryAt = _clock.GetUtcNow() + _options.UnavailableRetry;
            }
            else control.Unavailable = null;
        }

        // Recorded even while closing, so a finished job isn't redone next time.
        await TryAsync(() => outcome switch
        {
            StageOutcome.Done done => queue.CompleteAsync(job, done.Status, done.Reason, done.Next, CancellationToken.None),
            StageOutcome.Blocked blocked => queue.BlockAsync(job, blocked.Reason, CancellationToken.None),
            StageOutcome.Failed failed => queue.FailAsync(job, failed.Reason, failed.Retry, CancellationToken.None),
            StageOutcome.Later later => queue.DeferAsync(job, later.Wait, later.Reason, CancellationToken.None),
            StageOutcome.Unavailable => queue.ReleaseAsync(job, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unknown outcome {outcome}"),
        });
        if (outcome is StageOutcome.Done { Next: var next })
            foreach (var other in next.Select(Pipeline.LaneOf).Distinct()) _laneSignals[other].Set();
    }

    /// <summary>A queue write at shutdown can find the index writer already stopped; the lease is recovered next start.</summary>
    async Task TryAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (InvalidOperationException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public override void Dispose()
    {
        base.Dispose();
        foreach (var control in _lanes.Values) control.Dispose();
        foreach (var signal in _laneSignals.Values) signal.Dispose();
        _scanSignal.Dispose();
        _hashSignal.Dispose();
    }

    sealed class LaneControl : IDisposable
    {
        public bool Paused;
        public CancellationTokenSource Cancel = new();
        public JobRecord? Running;
        public string? Unavailable;
        public DateTimeOffset RetryAt;

        public void Dispose() => Cancel.Dispose();
    }

    /// <summary>An auto-reset signal: any number of Sets before a wait wake it once.</summary>
    sealed class Signal : IDisposable
    {
        readonly SemaphoreSlim _semaphore = new(0, 1);

        public void Set()
        {
            try
            {
                _semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);

        public void Dispose() => _semaphore.Dispose();
    }
}
