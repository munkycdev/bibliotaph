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

    /// <summary>Online-only files stop downloading when their drive would have less than this free.</summary>
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
    /// How often offline folders are looked at again, for a network share coming back, which Windows doesn't announce
    /// as it does a disk being plugged in.
    /// </summary>
    public TimeSpan OfflineRecheck { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after files go back to online-only, while online-only files wait for space, the hasher looks at the
    /// free space again: the sync app frees it shortly after it is asked, not at once.
    /// </summary>
    public TimeSpan SpaceSettle { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Requests someone is waiting on, which hashing and the lanes let go first; none in the Windows app.</summary>
    public ForegroundActivity? Foreground { get; init; }
}

/// <summary>Why a library folder reads as offline. Its files are never marked missing for it.</summary>
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

/// <summary>What "Point to its new place" did with a folder.</summary>
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
/// How indexing reaches the library's files: hashing them, listing and opening ZIPs, the free space an online-only file
/// needs to download into, the disk and file IDs that tell a moved file from a new one, giving downloaded files back
/// to online-only, and listing library folders.
/// </summary>
public sealed record IndexingFiles(FileHasher Hasher, ArchiveReader Archives, IDiskSpace Disk, IFileIdentity Identity, OnlineOnlyReturns Returns, ISourceLister Lister);

/// <summary>
/// What indexing is doing, for those that show it or wait on it: whether folders are being looked through, the file
/// being read, the job each lane is running, which lanes are paused or waiting and why, and the files that couldn't
/// be read. <see cref="Changed"/> says when any of it may have moved.
/// </summary>
public interface IIndexingStatus
{
    /// <summary>Something changed that a progress display would show. Raised on a background thread, often; throttle.</summary>
    event EventHandler? Changed;

    /// <summary>Whether a lane may work: every gated stage in it is ready. A lane with no gated stage always may.</summary>
    bool IsOpen(Lane lane);

    /// <summary>Why a lane is waiting even though it has work, such as a model endpoint that isn't answering; null otherwise.</summary>
    string? Unavailable(Lane lane);

    /// <summary>True while library folders are being looked through for new, changed and missing files.</summary>
    bool IsScanning { get; }

    /// <summary>True when online-only files are waiting because their drive is short of space.</summary>
    bool WaitingForDiskSpace { get; }

    /// <summary>The last scan of each library folder: what it holds, what changed, or why it couldn't be reached.</summary>
    IReadOnlyList<RootScan> Scans { get; }

    /// <summary>Files that couldn't be read for hashing, each with why. Each is tried again after the next scan of its folder.</summary>
    IReadOnlyList<UnreadableFile> Unreadable { get; }

    /// <summary>The file being read for hashing, if any.</summary>
    string? Hashing { get; }

    /// <summary>The job a lane is running, if any.</summary>
    JobRecord? Running(Lane lane);

    /// <summary>Whether a lane is paused.</summary>
    bool IsPaused(Lane lane);
}

/// <summary>
/// What can be asked of indexing: pause and resume a lane, look through folders again, run stages again, give failed
/// ones another go, stop work on a book, read a book again from the start, and point a moved folder to its new place.
/// </summary>
public interface IJobControl
{
    /// <summary>Pauses a lane. Its running job stops at the next page and goes back to the queue without using an attempt.</summary>
    void Pause(Lane lane);

    /// <summary>Lets a paused lane work again.</summary>
#pragma warning disable CA1716 // A keyword in Visual Basic only; nothing outside this C# app implements it.
    void Resume(Lane lane);
#pragma warning restore CA1716

    /// <summary>Scans one root, or every root when <paramref name="rootId"/> is null, as soon as the scanner is free.</summary>
    void RequestScan(long? rootId = null);

    /// <summary>
    /// Scans these roots, or every root when <paramref name="rootIds"/> is null, and returns once that scan has
    /// finished. Nothing waits for files to be read.
    /// </summary>
    Task ScanNowAsync(IReadOnlyCollection<long>? rootIds = null, CancellationToken ct = default);

    /// <summary>Runs a stage again for every document that has been through it, as after a vocabulary edit.</summary>
    Task<int> RerunAsync(Stage stage, CancellationToken ct = default);

    /// <summary>
    /// Runs a stage again for one document that has been through it, as "Scan again" does for a whole-book scan.
    /// A job still waiting runs anyway. Returns whether it was queued again.
    /// </summary>
    Task<bool> RerunAsync(Stage stage, long documentId, CancellationToken ct = default);

    /// <summary>Gives a document's failed and blocked stages another go, as from Files needing attention.</summary>
    Task RetryAsync(long documentId, CancellationToken ct = default);

    /// <summary>Gives these documents' failed and blocked stages another go, as Files needing attention's Try all again does.</summary>
    Task RetryAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default);

    /// <summary>
    /// Stops the stages running for these documents now, as "Forget its text" does before it removes what they made.
    /// Each goes back to the queue as a paused job does; the lanes carry on with other work.
    /// </summary>
    void Interrupt(IReadOnlyCollection<long> documentIds);

    /// <summary>
    /// Looks again at every library folder that is offline, and at those on <paramref name="drives"/> (drive letters),
    /// as when a disk is plugged in or taken out.
    /// </summary>
    Task RescanOfflineAsync(IReadOnlyCollection<char>? drives = null, CancellationToken ct = default);

    /// <summary>
    /// "Point to its new place", for a library folder that moved for good: the folder takes the new path and is scanned
    /// there, keeping every book and everything done with it. The result says why not when the new place won't do.
    /// </summary>
    Task<RootRelocation> RelocateRootAsync(long rootId, string path, CancellationToken ct = default);

    /// <summary>
    /// Reprocess, from the inspector: reads a document's file again from the start, ahead of other work, keeping
    /// everything the user added. False while one of its stages is running.
    /// </summary>
    Task<bool> ReprocessAsync(long documentId, bool ocrEveryPage = false, CancellationToken ct = default);
}


/// <summary>
/// Runs the library: scans roots (and rescans when a watched folder changes), hashes new and changed files into
/// documents, and works the job queue in three lanes, Index (Probe, Text, Covers, RuleHints), OCR and Classify, each
/// pausable on its own. Classify works only while AI is ready, and waits with a notice while its endpoint can't be used.
/// Leases left by a previous run go back to pending before anything else starts. Online-only files that reading
/// downloaded go back to online-only once their stages are done with them (<see cref="OnlineOnlyReturns"/>).
/// </summary>
public sealed class IndexingService : BackgroundService, IJobControl, IIndexingStatus
{
    /// <summary>How the reason begins when a stage threw something nothing expected: a bug, not a problem with the file.</summary>
    public const string UnexpectedError = "Unexpected error: ";

    readonly SourceRootStore _roots;
    readonly LibraryStore _library;
    readonly JobBoard _queue;
    readonly PartsService _parts;
    readonly OnlineOnlyReturns _returns;
    readonly ISourceLister _lister;
    readonly ILogger _log;
    readonly TimeProvider _clock;
    readonly IndexingActivityState _activity = new();
    readonly LaneRunner _lanes;
    readonly HashingLoop _hashing;
    readonly FolderWatchers _watchers;
    readonly LibraryScanner _scanner;
    readonly Lock _lock = new();
    bool _gatesWatched;
    CancellationToken _stopping;

    /// <summary>Library folders are watched through <paramref name="folderWatcher"/>: the file system's own notifications unless a test says otherwise.</summary>
    public IndexingService(
        SourceRootStore roots,
        LibraryStore library,
        JobBoard queue,
        IStageCatalog stages,
        IndexingFiles files,
        PackService packs,
        PartsService parts,
        FolderCollections folderCollections,
        IndexingOptions? options = null,
        ILogger<IndexingService>? log = null,
        TimeProvider? clock = null,
        IFolderWatcher? folderWatcher = null)
    {
        options ??= new IndexingOptions();
        (_roots, _library, _queue, _parts, _returns, _lister) = (roots, library, queue, parts, files.Returns, files.Lister);
        _log = log ?? NullLogger<IndexingService>.Instance;
        _clock = clock ?? TimeProvider.System;
        // Each part logs as indexing, and says through Changed when what it shows moves. A callback into a part made
        // after it runs only once the loops have started, long after this.
        _lanes = new LaneRunner(queue, library, stages, options, _log, _clock, RaiseChanged, fileStageDone: () => _hashing!.WakeReturns());
        _hashing = new HashingLoop(library, queue, files, packs, folderCollections, _lanes, _activity, options, _log, _clock, RaiseChanged);
        _watchers = new FolderWatchers(folderWatcher ?? new FileSystemFolderWatcher(), options.RescanDelay, _log, _clock, rootId => _scanner!.RequestScan(rootId));
        _scanner = new LibraryScanner(roots, library, files.Lister, files.Identity, _watchers, _hashing, _lanes, _activity, options, _log, _clock, RaiseChanged);
    }

    /// <summary>Something changed that a progress display would show. Raised on a background thread, often; throttle.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether a lane may work: every gated stage in it is ready. A lane with no gated stage always may.</summary>
    public bool IsOpen(Lane lane) => _lanes.IsOpen(lane);

    /// <summary>Why a lane is waiting even though it has work, such as a model endpoint that isn't answering; null otherwise.</summary>
    public string? Unavailable(Lane lane) => _lanes.Unavailable(lane);

    public bool IsScanning => _activity.Current.IsScanning;

    /// <summary>
    /// True while folders are due to be looked at for packs and for the collections that follow them, or are being
    /// looked at: hashing can finish and every job go idle a moment before the pack cards it calls for are made.
    /// </summary>
    public bool PacksPending => _hashing.PacksPending;

    /// <summary>True when online-only files are waiting because their drive is short of space.</summary>
    public bool WaitingForDiskSpace => _activity.Current.WaitingForDiskSpace;

    public IReadOnlyList<RootScan> Scans => _scanner.Scans;

    public IReadOnlyList<UnreadableFile> Unreadable => _hashing.Unreadable;

    /// <summary>The file being read for hashing, if any.</summary>
    public string? Hashing => _activity.Current.Hashing;

    /// <summary>The job a lane is running, if any.</summary>
    public JobRecord? Running(Lane lane) => _lanes.Running(lane);

    public bool IsPaused(Lane lane) => _lanes.IsPaused(lane);

    /// <summary>Pauses a lane. Its running job stops at the next page and goes back to the queue without using an attempt.</summary>
    public void Pause(Lane lane)
    {
        if (!_lanes.Pause(lane)) return;
        // While online-only files wait for space, a paused lane's books give their files back (HashingLoop.ReturnLoopAsync).
        _hashing.WakeReturns();
        RaiseChanged();
    }

    public void Resume(Lane lane)
    {
        if (!_lanes.Resume(lane)) return;
        if (lane == Lane.Index) _hashing.Wake();
        RaiseChanged();
    }

    /// <summary>Scans one root, or every root when <paramref name="rootId"/> is null, as soon as the scanner is free.</summary>
    public void RequestScan(long? rootId = null) => _scanner.RequestScan(rootId);

    /// <summary>
    /// Scans these roots, or every root when <paramref name="rootIds"/> is null, and returns once that scan has
    /// finished: as the reader's Try again does for a book whose folder was offline or whose file had gone, so it
    /// opens at once if the folder is back or the file turned up. Nothing waits for files to be read.
    /// </summary>
    public Task ScanNowAsync(IReadOnlyCollection<long>? rootIds = null, CancellationToken ct = default) => _scanner.ScanNowAsync(rootIds, ct);

    /// <summary>Runs a stage again for every document that has been through it, as after a vocabulary edit.</summary>
    public async Task<int> RerunAsync(Stage stage, CancellationToken ct = default)
    {
        var queued = await _queue.RerunAsync(stage, ct);
        _lanes.Wake(stage);
        RaiseChanged();
        return queued;
    }

    /// <summary>
    /// Runs a stage again for one document that has been through it, as "Scan again" does for a whole-book scan.
    /// A job still waiting runs anyway. Returns whether it was queued again.
    /// </summary>
    public async Task<bool> RerunAsync(Stage stage, long documentId, CancellationToken ct = default)
    {
        var queued = await _queue.RerunAsync(stage, documentId, ct);
        _lanes.Wake(stage);
        RaiseChanged();
        return queued;
    }

    /// <summary>Gives a document's failed and blocked stages another go, as from Files needing attention.</summary>
    public Task RetryAsync(long documentId, CancellationToken ct = default) => RetryAsync([documentId], ct);

    /// <summary>Gives these documents' failed and blocked stages another go, as Files needing attention's Try all again does.</summary>
    public async Task RetryAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return;
        await _queue.RetryAsync(documentIds, ct);
        _lanes.WakeAll();
        RaiseChanged();
    }

    /// <summary>
    /// Stops the stages running for these documents now, as "Forget its text" does before it removes what they made.
    /// Each goes back to the queue as a paused job does; the lanes carry on with other work.
    /// </summary>
    public void Interrupt(IReadOnlyCollection<long> documentIds)
    {
        _lanes.Interrupt(documentIds);
        RaiseChanged();
    }

    /// <summary>
    /// Looks again at every library folder that is offline, and at those on <paramref name="drives"/> (drive letters),
    /// as when a disk is plugged in or taken out. One that can be reached again comes back with its books without
    /// Look for changes; one whose disk went reads as offline at once.
    /// </summary>
    public Task RescanOfflineAsync(IReadOnlyCollection<char>? drives = null, CancellationToken ct = default) => _scanner.RescanOfflineAsync(drives, ct);

    /// <summary>
    /// "Point to its new place", for a library folder that moved for good, as to a new disk: checked as a folder to
    /// add would be (it exists, and isn't another library folder, inside one or holding one), then the folder takes
    /// the new path and is scanned there. Its files are matched by their paths under it, so every book keeps its hash,
    /// card and everything done with it, and nothing is read again unless it changed. Restore points folders at their
    /// new places through this too.
    /// </summary>
    public Task<RootRelocation> RelocateRootAsync(long rootId, string path, CancellationToken ct = default) => _scanner.RelocateRootAsync(rootId, path, ct);

    /// <summary>Lists a folder for the Add folder preview, without adding it or opening any file.</summary>
    public async Task<ScanResult?> PreviewAsync(string folder, CancellationToken ct = default)
    {
        var others = (await _roots.ListAsync(ct)).Select(r => r.Path).ToList();
        return await SourceScanner.ScanAsync(_lister, folder, others, ct);
    }

    /// <summary>
    /// Reprocess, from the inspector: reads a document's file again from the start, ahead of other work, keeping
    /// everything the user added (<see cref="JobBoard.ReprocessAsync"/>). Its folders are scanned too, so a file that
    /// changed since it was indexed becomes a new version, as on any rescan. False while one of its stages is running.
    /// </summary>
    public async Task<bool> ReprocessAsync(long documentId, bool ocrEveryPage = false, CancellationToken ct = default)
    {
        if (!await _queue.ReprocessAsync(documentId, ocrEveryPage, ct)) return false;
        _log.LogInformation("Reprocessing document {DocumentId} (OCR on every page: {EveryPage})", documentId, ocrEveryPage);
        foreach (var rootId in await _library.GetRootIdsAsync(documentId, ct)) _scanner.RequestScan(rootId);
        _lanes.WakeAll();
        RaiseChanged();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        var recovered = await _queue.RecoverLeasesAsync(stoppingToken);
        if (recovered > 0) _log.LogInformation("Returned {Count} interrupted jobs to the queue", recovered);
        // A new index.db beside a catalog with books in it, as after a restore without the index: each is read again.
        var unqueued = await _queue.EnqueueUnqueuedAsync(await _library.GetDocumentHashesAsync(stoppingToken), Pipeline.First, stoppingToken);
        if (unqueued > 0) _log.LogInformation("Queued {Count} documents index.db had no record of, to be read again", unqueued);
        // A stage added in an update reaches the books read before it existed, when the stage says it should.
        foreach (var stage in _lanes.Stages.All)
        {
            if (stage is not { Backfill: true, After: { } after }) continue;
            var backfilled = await _queue.EnqueueMissingAsync(stage.Stage, after, stoppingToken);
            if (backfilled > 0) _log.LogInformation("Queued {Stage} for {Count} documents read before it existed", stage.Stage, backfilled);
        }
        WatchGates();

        _scanner.Wake();
        // Each loop on the thread pool: none of this may run on the UI thread that started the host.
        List<Task> loops =
        [
            Task.Run(() => LoopAsync("scanner", _scanner.RunAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("offline check", _scanner.OfflineLoopAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("hasher", _hashing.RunAsync, stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("index lane", ct => _lanes.RunAsync(Lane.Index, ct), stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("OCR lane", ct => _lanes.RunAsync(Lane.Ocr, ct), stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("Classify lane", ct => _lanes.RunAsync(Lane.Classify, ct), stoppingToken), CancellationToken.None),
            Task.Run(() => LoopAsync("online-only returns", _hashing.ReturnLoopAsync, stoppingToken), CancellationToken.None),
        ];
        await Task.WhenAll(loops);
    }

    void WatchGates()
    {
        lock (_lock)
        {
            if (_gatesWatched) return;
            _gatesWatched = true;
        }
        foreach (var gated in _lanes.Stages.All.OfType<IGatedStage>())
        {
            var lane = gated.Lane;
            gated.ReadyChanged += (_, _) => _lanes.GateChanged(lane);
        }
        _parts.Marked += (_, documentId) => _ = ClassifyPartsAsync(documentId);
        _returns.SettingChanged += (_, _) => _hashing.WakeReturns();
    }

    static readonly TimeSpan PartsRecheck = TimeSpan.FromSeconds(1);

    /// <summary>
    /// An adventure marked in a document is classified on its own pages: the document's Classify job runs again, and
    /// passes over what this model has read already. While AI is off it waits with the rest.
    /// </summary>
    async Task ClassifyPartsAsync(long documentId)
    {
        try
        {
            // Classify may be reading the document now, having looked for its adventures before this one was marked
            // (Accept marks several in a row): it runs again once it is done.
            while (!await _queue.RerunAsync(Stage.Classify, documentId, _stopping))
            {
                if (await _queue.IsRunningAsync(Stage.Classify, documentId, _stopping))
                {
                    await Task.Delay(PartsRecheck, _clock, _stopping);
                    continue;
                }
                // Not running and not finished: waiting to run, which reads the adventure then; or it finished between
                // the two looks, and runs again now.
                if (await _queue.RerunAsync(Stage.Classify, documentId, _stopping)) break;
                return;
            }
            _lanes.Wake(Stage.Classify);
            RaiseChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Document {DocumentId}'s adventures couldn't be queued for AI", documentId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        _watchers.Clear();
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

    void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public override void Dispose()
    {
        base.Dispose();
        _lanes.Dispose();
        _scanner.Dispose();
        _hashing.Dispose();
    }
}
