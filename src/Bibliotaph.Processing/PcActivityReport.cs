using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>A connected PC's way to tell the web library what its AI is doing.</summary>
public interface IPcActivityChannel
{
    /// <summary>Whether this PC is connected to a web library.</summary>
    bool IsOn { get; }

    /// <summary>Says what the AI is doing, or, with null, that it has nothing to do. Returns false when it couldn't be said now.</summary>
    Task<bool> ReportActivityAsync(PcActivity? activity, CancellationToken ct);
}

/// <summary>
/// Tells the web library what this PC's AI is doing, so Settings › Processing on the web can say the cataloguing it
/// shows is being done here: the book it is on, by its content hash, whether it is paused or waiting, and this PC's
/// counts. Said every half minute while there is work, which is also how the web learns this PC is still there; soon
/// after the lane moves on to another book, pauses or starts waiting; and, with nothing, once the work is done or the
/// app closes. Nothing is said while this PC isn't connected or its AI isn't set up.
/// </summary>
public sealed class PcActivityReport(IndexingService indexing, IndexQueries queries, AiSettings ai, IPcActivityChannel channel,
    TimeProvider? clock = null, ILogger<PcActivityReport>? log = null) : IHostedService, IDisposable
{
    /// <summary>How often it is said while there is work: well inside the web's <c>PcActivityBoard.ForgetAfter</c>.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    /// <summary>How long after the lane changes it is said: long enough to let a burst of changes settle.</summary>
    public static readonly TimeSpan Soon = TimeSpan.FromSeconds(2);

    /// <summary>How long a closing app waits to say it has stopped: the web forgets it by itself soon after anyway.</summary>
    static readonly TimeSpan Closing = TimeSpan.FromSeconds(3);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<PcActivityReport>.Instance;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _stopping = new();
    readonly Lock _lock = new();
    ITimer? _timer;

    /// <summary>The lane as last said, to tell when it moved; null when nothing was said or "nothing to do" was.</summary>
    (string? Step, long? DocumentId, bool Paused, string? Waiting)? _said;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock) _timer = _clock.CreateTimer(_ => Fire(), null, Every, Every);
        indexing.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        indexing.Changed -= OnChanged;
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        await _stopping.CancelAsync();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_said is null || !channel.IsOn) return;
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            closing.CancelAfter(Closing);
            await channel.ReportActivityAsync(null, closing.Token);
        }
        catch (OperationCanceledException)
        {
            // The web forgets this PC by itself.
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        lock (_lock) _timer?.Dispose();
        _stopping.Dispose();
        _gate.Dispose();
    }

    /// <summary>Raised often: only a move the web would show brings the next report forward.</summary>
    void OnChanged(object? sender, EventArgs e)
    {
        var said = _said;
        if (said is null ? indexing.Running(Lane.Classify) is null : LaneNow() == said) return;
        lock (_lock) _timer?.Change(Soon, Every);
    }

    (string? Step, long? DocumentId, bool Paused, string? Waiting) LaneNow()
    {
        var running = indexing.Running(Lane.Classify);
        return (running?.Stage.ToString(), running?.DocumentId, indexing.IsPaused(Lane.Classify), indexing.Unavailable(Lane.Classify));
    }

    void Fire() => _ = Task.Run(async () =>
    {
        try
        {
            await ReportNowAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            // Said again at the next half minute.
            _log.LogWarning(ex, "Telling the web library what the AI is doing failed");
        }
    });

    /// <summary>
    /// What the AI is doing, as the web is told it: null while the AI isn't set up, or has nothing to do, running or
    /// waiting. Whole books count only while Settings › AI has them scanned.
    /// </summary>
    public async Task<PcActivity?> ActivityAsync(CancellationToken ct = default)
    {
        if (!ai.Setup.IsReady) return null;
        var running = indexing.Running(Lane.Classify);
        var progress = await queries.GetProgressAsync(ct);
        var (scanned, toScan) = ai.ScanWholeBooks ? (progress.ScannedWhole, progress.ToScanWhole) : (0, 0);
        if (running is null && progress.ToClassify == 0 && toScan == 0) return null;
        return new PcActivity(running?.Stage.ToString(), running?.ContentHash, indexing.IsPaused(Lane.Classify),
            indexing.Unavailable(Lane.Classify), progress.Classified, progress.ToClassify, scanned, toScan);
    }

    /// <summary>Tells the web now: what the AI is doing, or, once, that it has nothing to do.</summary>
    public async Task ReportNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!channel.IsOn)
            {
                _said = null;
                return;
            }
            var lane = LaneNow();
            var activity = await ActivityAsync(ct);
            if (activity is null && _said is null) return;
            if (await channel.ReportActivityAsync(activity, ct)) _said = activity is null ? null : lane;
        }
        finally
        {
            _gate.Release();
        }
    }
}
