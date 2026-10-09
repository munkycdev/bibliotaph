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
}

/// <summary>The last scan of a root: what it holds, what changed, or that it couldn't be reached.</summary>
public sealed record RootScan(long RootId, string Path, bool Reachable, ScanSummary? Summary, ReconcileResult? Changes, DateTime ScannedUtc);

/// <summary>A file the hasher could not read. It is tried again after the next scan of its folder.</summary>
public sealed record UnreadableFile(long LocationId, string Path, string Reason);

/// <summary>
/// Runs the library: scans roots (and rescans when a watched folder changes), hashes new and changed files into
/// documents, and works the job queue in two lanes, Index (Probe, Text, Covers, RuleHints) and OCR, each pausable on its own.
/// Leases left by a previous run go back to pending before anything else starts (A11).
/// </summary>
public sealed class IndexingService(
    SourceRootStore roots,
    LibraryStore library,
    JobBoard queue,
    IEnumerable<IStage> stages,
    FileHasher hasher,
    IDiskSpace disk,
    IndexingOptions? options = null,
    ILogger<IndexingService>? log = null,
    TimeProvider? clock = null) : BackgroundService
{
    readonly IndexingOptions _options = options ?? new IndexingOptions();
    readonly ILogger _log = log ?? NullLogger<IndexingService>.Instance;
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Dictionary<Stage, IStage> _stages = stages.ToDictionary(s => s.Stage);
    readonly Lock _lock = new();

    readonly Signal _scanSignal = new();
    readonly Signal _hashSignal = new();
    readonly Dictionary<Lane, Signal> _laneSignals = new() { [Lane.Index] = new(), [Lane.Ocr] = new() };
    readonly Dictionary<Lane, LaneControl> _lanes = new() { [Lane.Index] = new(), [Lane.Ocr] = new() };

    HashSet<long>? _pendingScans; // null: every root
    bool _scanAll = true;
    readonly Dictionary<long, RootScan> _scans = [];
    readonly Dictionary<long, UnreadableFile> _unreadable = [];
    readonly Dictionary<long, (FileSystemWatcher Watcher, ITimer Debounce)> _watchers = [];
    CancellationToken _stopping;

    /// <summary>Something changed that a progress display would show. Raised on a background thread, often; throttle.</summary>
    public event EventHandler? Changed;

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

    /// <summary>Lists a folder for the Add folder preview, without adding it or opening any file.</summary>
    public async Task<ScanResult?> PreviewAsync(string folder, CancellationToken ct = default)
    {
        var others = (await roots.ListAsync(ct)).Select(r => r.Path).ToList();
        return await Task.Run(() => SourceScanner.Scan(folder, others, ct), ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var recovered = await queue.RecoverLeasesAsync(stoppingToken);
        if (recovered > 0) _log.LogInformation("Returned {Count} interrupted jobs to the queue", recovered);
        // A library indexed before rule hints existed gets them without re-probing anything.
        if (_stages.ContainsKey(Stage.RuleHints))
        {
            var backfilled = await queue.EnqueueMissingAsync(Stage.RuleHints, after: Stage.Probe, stoppingToken);
            if (backfilled > 0) _log.LogInformation("Queued hints from names for {Count} documents", backfilled);
        }

        _scanSignal.Set();
        // Each loop on the thread pool: none of this may run on the UI thread that started the host.
        await Task.WhenAll(
            Task.Run(() => LoopAsync("scanner", ScanLoopAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("hasher", HashLoopAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("index lane", ct => LaneLoopAsync(Lane.Index, ct), stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("OCR lane", ct => LaneLoopAsync(Lane.Ocr, ct), stoppingToken), CancellationToken.None));
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
            if (_options.WatchFolders) SyncWatchers(current);
            IsScanning = true;
            RaiseChanged();
            try
            {
                foreach (var root in current.Where(r => all || some?.Contains(r.Id) == true))
                    await ScanRootAsync(root, current, ct);
            }
            finally
            {
                IsScanning = false;
                RaiseChanged();
            }
            await ReleaseReachableAsync(ct);
            _hashSignal.Set();
        }
    }

    async Task ScanRootAsync(SourceRoot root, IReadOnlyList<SourceRoot> all, CancellationToken ct)
    {
        var others = all.Where(r => r.Id != root.Id).Select(r => r.Path).ToList();
        var result = await Task.Run(() => SourceScanner.Scan(root.Path, others, ct), ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (result is null)
        {
            _log.LogInformation("Library folder {RootId} is offline", root.Id);
            await library.SetRootAvailabilityAsync(root.Id, SourceRootAvailability.Offline, ct);
            lock (_lock) _scans[root.Id] = new RootScan(root.Id, root.Path, false, null, null, now);
            return;
        }

        var changes = await library.ReconcileRootAsync(root.Id, result.Files, result.Summary.Inaccessible, ct);
        _log.LogInformation("Scanned library folder {RootId}: {Files} files, {Changes}", root.Id, result.Files.Count, changes);
        lock (_lock)
        {
            _scans[root.Id] = new RootScan(root.Id, root.Path, true, result.Summary, changes, now);
            // Anything that couldn't be read gets another try now its folder has been looked at again.
            foreach (var stale in _unreadable.Values.Where(u => u.Path.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase)).ToList())
                _unreadable.Remove(stale.LocationId);
        }
    }

    void SyncWatchers(IReadOnlyList<SourceRoot> current)
    {
        lock (_lock)
        {
            foreach (var gone in _watchers.Keys.Where(id => current.All(r => r.Id != id)).ToList())
            {
                _watchers[gone].Watcher.Dispose();
                _watchers[gone].Debounce.Dispose();
                _watchers.Remove(gone);
            }
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
                    // A buffer overflow loses events; a rescan catches up with whatever they were.
                    watcher.Error += Changed;
                    watcher.EnableRaisingEvents = true;
                    _watchers[rootId] = (watcher, debounce);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
                {
                    // An offline root can't be watched; the next scan after it returns tries again.
                    _log.LogDebug(ex, "Not watching library folder {RootId}", rootId);
                }
            }
        }
    }

    // ---- Hashing --------------------------------------------------------------------------------------------------

    async Task HashLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            await _hashSignal.WaitAsync(_options.IdleRecheck, ct);
            if (IsPaused(Lane.Index)) continue;
            await HashPendingAsync(ct);
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

        var attached = await library.AttachHashAsync(file, hash, ct);
        // Idempotent: a copy of a known document finds its Probe job already there.
        if (attached is { } document) await queue.EnqueueAsync(document.DocumentId, hash.Hex, Pipeline.First, ct: ct);
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
            lock (_lock) laneToken = _lanes[lane].Paused ? default : _lanes[lane].Cancel.Token;
            if (laneToken == default)
            {
                await _laneSignals[lane].WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

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

        if (outcome is not StageOutcome.Done { Status: StageStatus.Complete })
            _log.LogInformation("{Stage} for document {DocumentId}: {Outcome}", job.Stage, job.DocumentId, outcome);

        // Recorded even while closing, so a finished job isn't redone next time.
        await TryAsync(() => outcome switch
        {
            StageOutcome.Done done => queue.CompleteAsync(job, done.Status, done.Reason, done.Next, CancellationToken.None),
            StageOutcome.Blocked blocked => queue.BlockAsync(job, blocked.Reason, CancellationToken.None),
            StageOutcome.Failed failed => queue.FailAsync(job, failed.Reason, failed.Retry, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unknown outcome {outcome}"),
        });
        if (outcome is StageOutcome.Done { Next: var next })
            foreach (var lane in next.Select(Pipeline.LaneOf).Distinct()) _laneSignals[lane].Set();
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
