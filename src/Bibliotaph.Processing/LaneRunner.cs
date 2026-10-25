using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Processing;

/// <summary>
/// The three lanes, Index, OCR and Classify: each takes the next job for its stages from the queue and runs it, and
/// each can be paused on its own. A lane with a gated stage works only while every one is ready, and a lane whose work
/// can't be done right now (a model endpoint that isn't running) waits a while with the reason before trying again.
/// <paramref name="changed"/> is called when something a progress display would show has moved, and
/// <paramref name="fileStageDone"/> when a stage that reads the file is done with it, so it may go back to online-only.
/// </summary>
internal sealed class LaneRunner(JobBoard queue, LibraryStore library, IStageCatalog stages, IndexingOptions options, ILogger log,
    TimeProvider clock, Action changed, Action fileStageDone) : IDisposable
{
    readonly Lock _lock = new();
    readonly Dictionary<Lane, Signal> _signals = Enum.GetValues<Lane>().ToDictionary(l => l, _ => new Signal());
    readonly Dictionary<Lane, LaneControl> _lanes = Enum.GetValues<Lane>().ToDictionary(l => l, _ => new LaneControl());

    /// <summary>The stages the lanes run.</summary>
    public IStageCatalog Stages => stages;

    /// <summary>Whether a lane may work: every gated stage in it is ready. A lane with no gated stage always may.</summary>
    public bool IsOpen(Lane lane) =>
        stages.All.Where(s => s.Lane == lane).OfType<IGatedStage>().All(s => s.IsReady);

    /// <summary>Why a lane is waiting even though it has work, such as a model endpoint that isn't answering; null otherwise.</summary>
    public string? Unavailable(Lane lane)
    {
        lock (_lock) return _lanes[lane].Unavailable;
    }

    /// <summary>The job a lane is running, if any.</summary>
    public JobRecord? Running(Lane lane)
    {
        lock (_lock) return _lanes[lane].Running;
    }

    public bool IsPaused(Lane lane)
    {
        lock (_lock) return _lanes[lane].Paused;
    }

    /// <summary>Wakes a lane to look for work, as when some was queued for it.</summary>
    public void Wake(Lane lane) => _signals[lane].Set();

    /// <summary>Wakes the lane that runs a stage, as when it was queued again. No lane runs a stage indexing doesn't have.</summary>
    public void Wake(Stage stage)
    {
        if (stages.Find(stage) is { } runs) Wake(runs.Lane);
    }

    public void WakeAll()
    {
        foreach (var signal in _signals.Values) signal.Set();
    }

    /// <summary>Wakes a lane whose gate may have opened, as when AI is switched on, and clears what it was waiting for.</summary>
    public void GateChanged(Lane lane)
    {
        lock (_lock) _lanes[lane].Unavailable = null;
        _signals[lane].Set();
        changed();
    }

    /// <summary>
    /// Pauses a lane. Its running job stops at the next page and goes back to the queue without using an attempt.
    /// Returns false when it was paused already.
    /// </summary>
    public bool Pause(Lane lane)
    {
        lock (_lock)
        {
            var control = _lanes[lane];
            if (control.Paused) return false;
            control.Paused = true;
            control.Cancel.Cancel();
        }
        return true;
    }

    /// <summary>Lets a paused lane work again, and wakes it. Returns false when it wasn't paused.</summary>
    public bool Resume(Lane lane)
    {
        lock (_lock)
        {
            var control = _lanes[lane];
            if (!control.Paused) return false;
            control.Paused = false;
            control.Cancel.Dispose();
            control.Cancel = new CancellationTokenSource();
        }
        _signals[lane].Set();
        return true;
    }

    /// <summary>Stops the jobs running for these documents now; each goes back to the queue as a paused job does.</summary>
    public void Interrupt(IReadOnlyCollection<long> documentIds)
    {
        lock (_lock)
        {
            foreach (var control in _lanes.Values)
            {
                if (control.Paused || control.Running is not { } job || !documentIds.Contains(job.DocumentId)) continue;
                // Not disposed: the lane's loop still holds the old token, and drops it with this job.
                var interrupted = control.Cancel;
                control.Cancel = new CancellationTokenSource();
                interrupted.Cancel();
            }
        }
    }

    /// <summary>
    /// Gives stages blocked because no copy of their file could be read another go, for each document whose file
    /// can be read again: its folder is back online, a scan found the file again, or it was hashed back to the document.
    /// </summary>
    public async Task ReleaseReachableAsync(CancellationToken ct)
    {
        var blocked = await queue.BlockedForAsync(StageOutcome.Blocked.Unreachable, ct);
        if (blocked.Count == 0) return;
        var readable = await library.GetReadableAsync(blocked, ct);
        if (readable.Count == 0) return;
        var released = await queue.UnblockAsync(readable, StageOutcome.Blocked.Unreachable, ct);
        log.LogInformation("Released {Count} stages whose files can be read again", released);
        WakeAll();
        changed();
    }

    /// <summary>Works a lane's jobs one at a time until indexing stops.</summary>
    public async Task RunAsync(Lane lane, CancellationToken stoppingToken)
    {
        var names = stages.All.Where(s => s.Lane == lane).Select(s => s.Stage).ToArray();
        if (names.Length == 0) return;
        var owner = $"{Environment.ProcessId}/{lane}";

        while (true)
        {
            CancellationToken laneToken;
            string? unavailable;
            lock (_lock) (laneToken, unavailable) = (_lanes[lane].Paused ? default : _lanes[lane].Cancel.Token, _lanes[lane].Unavailable);
            if (laneToken == default)
            {
                await _signals[lane].WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }
            if (!IsOpen(lane))
            {
                // Its gate opening (AI switched on) wakes it; nothing else needs to.
                await _signals[lane].WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }
            if (unavailable is not null && !await WaitOutAsync(lane, stoppingToken)) continue;

            var job = await queue.LeaseAsync(names, owner, options.Lease, stoppingToken);
            if (job is null)
            {
                var next = await queue.NextReadyAsync(names, stoppingToken);
                var wait = next is null ? options.IdleRecheck : next.Value - clock.GetUtcNow();
                wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > options.IdleRecheck ? options.IdleRecheck : wait;
                await _signals[lane].WaitAsync(wait, stoppingToken);
                continue;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(laneToken, stoppingToken);
            lock (_lock) _lanes[lane].Running = job;
            changed();
            try
            {
                await RunJobAsync(lane, job, linked.Token, stoppingToken);
            }
            finally
            {
                lock (_lock) _lanes[lane].Running = null;
            }
            changed();
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
        var wait = until - clock.GetUtcNow();
        if (wait <= TimeSpan.Zero) return true;
        await _signals[lane].WaitAsync(wait, ct);
        return false;
    }

    async Task RunJobAsync(Lane lane, JobRecord job, CancellationToken ct, CancellationToken stopping)
    {
        StageOutcome outcome;
        try
        {
            // A book whose text the user chose to forget isn't read again until "Read it again", whatever queued the
            // job.
            outcome = await library.IsTextForgottenAsync(job.DocumentId, ct)
                ? new StageOutcome.Done(StageStatus.Skipped, [], TextAccessReasons.Forgotten)
                : await (stages.Find(job.Stage) ?? throw new InvalidOperationException($"No stage does {job.Stage}.")).RunAsync(job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Paused or closing: back to the queue as if it never started.
            await TryAsync(() => queue.ReleaseAsync(job, CancellationToken.None), stopping);
            return;
        }
        catch (PdfEngineException ex)
        {
            // A page that keeps killing the engine says so itself, and trying again can't help.
            outcome = ex.Retry
                ? new StageOutcome.Failed($"The PDF engine stopped: {ex.Message}", Retry: true)
                : new StageOutcome.Failed(ex.Message, Retry: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outcome = new StageOutcome.Failed($"The file could not be read: {ex.Message}", Retry: true);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "{Stage} failed for document {DocumentId}", job.Stage, job.DocumentId);
            outcome = new StageOutcome.Failed(IndexingService.UnexpectedError + ex.Message, Retry: true);
        }

        if (outcome is not StageOutcome.Done { Status: StageStatus.Complete } and not StageOutcome.Later)
            log.LogInformation("{Stage} for document {DocumentId}: {Outcome}", job.Stage, job.DocumentId, outcome);

        lock (_lock)
        {
            var control = _lanes[lane];
            if (outcome is StageOutcome.Unavailable unavailable)
            {
                control.Unavailable = unavailable.Reason;
                control.RetryAt = clock.GetUtcNow() + options.UnavailableRetry;
            }
            else control.Unavailable = null;
        }

        // A stage that got somewhere is followed by the stages that always come after it, as well as any it asked for.
        if (outcome is StageOutcome.Done { Status: StageStatus.Complete or StageStatus.Partial } done)
            outcome = done with { Next = [.. done.Next.Union(stages.Following(job.Stage)).Order()] };

        // Recorded even while closing, so a finished job isn't redone next time.
        await TryAsync(() => outcome switch
        {
            StageOutcome.Done done => queue.CompleteAsync(job, done.Status, done.Reason, done.Next, CancellationToken.None),
            StageOutcome.Blocked blocked => queue.BlockAsync(job, blocked.Reason, CancellationToken.None),
            StageOutcome.Failed failed => queue.FailAsync(job, failed.Reason, failed.Retry, CancellationToken.None),
            StageOutcome.Later later => queue.DeferAsync(job, later.Wait, later.Reason, CancellationToken.None),
            StageOutcome.Unavailable => queue.ReleaseAsync(job, CancellationToken.None),
            _ => throw new InvalidOperationException($"Unknown outcome {outcome}"),
        }, stopping);
        if (outcome is StageOutcome.Done { Next: var next })
            foreach (var other in next.Select(stages.Find).OfType<IStage>().Select(s => s.Lane).Distinct()) _signals[other].Set();
        if (Pipeline.FileStages.Contains(job.Stage)) fileStageDone();
    }

    /// <summary>A queue write at shutdown can find the index writer already stopped; the lease is recovered next start.</summary>
    static async Task TryAsync(Func<Task> write, CancellationToken stopping)
    {
        try
        {
            await write();
        }
        catch (InvalidOperationException) when (stopping.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        foreach (var control in _lanes.Values) control.Dispose();
        foreach (var signal in _signals.Values) signal.Dispose();
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
}
