using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bibliotaph.Spike.Contracts;
using Bibliotaph.Spike.WorkerHost;

namespace Bibliotaph.Spike.Viewer;

/// <summary>A render request. Tiles carry their pixel rectangle; full pages do not.</summary>
public sealed record RenderJob(PageModel Page, int Generation, double Scale, double PixelsPerDip, PixelRect? Tile);

public sealed class PdfOpenException(ErrorKind kind, string? message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
}

/// <summary>
/// Feeds render jobs to the worker one at a time, newest viewport first, and hands frozen
/// bitmaps back to the UI thread. Reopens the document if the worker restarts.
/// </summary>
public sealed class PageRenderer : IAsyncDisposable
{
    static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);

    readonly WorkerClient _worker = new();
    readonly Dispatcher _dispatcher;
    readonly object _lock = new();
    readonly SemaphoreSlim _signal = new(0, 1);
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;

    List<RenderJob> _pending = [];
    Session? _session;

    sealed record Session(string Path, string? Password, int DocId, int WorkerGeneration);

    public PageRenderer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _loop = Task.Run(LoopAsync);
    }

    public ViewerMetrics Metrics { get; } = new();
    public long? WorkerPeakBytes => _worker.PeakMemoryBytes;
    public int WorkerRestarts => _worker.Restarts;

    public async Task<DocInfo> OpenAsync(string path, string? password)
    {
        lock (_lock) _pending = [];
        if (_session is { } old && _worker.IsRunning)
            await _worker.SendAsync(new Request { Op = Op.Close, DocId = old.DocId });
        _session = null;

        var response = await _worker.SendAsync(new Request { Op = Op.Open, Path = path, Password = password }, TimeSpan.FromSeconds(120));
        if (!response.Ok || response.Doc is null) throw new PdfOpenException(response.Error, response.Message);
        _session = new Session(path, password, response.Doc.DocId, _worker.Generation);
        return response.Doc;
    }

    /// <summary>Replaces whatever is still queued: only the current viewport matters.</summary>
    public void Submit(List<RenderJob> jobs)
    {
        lock (_lock) _pending = jobs;
        if (_signal.CurrentCount == 0)
        {
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }
    }

    public async Task<List<SearchHit>> FindAsync(string query)
    {
        var session = await EnsureOpenAsync();
        var response = await _worker.SendAsync(
            new Request { Op = Op.Find, DocId = session.DocId, PageIndex = -1, Query = query, MaxHits = 2000 },
            TimeSpan.FromSeconds(120));
        return response.Ok ? response.Hits ?? [] : throw new InvalidOperationException(response.Message);
    }

    async Task LoopAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _signal.WaitAsync(token);
                while (TryTake(out var job)) await RenderAsync(job, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    bool TryTake(out RenderJob job)
    {
        lock (_lock)
        {
            if (_pending.Count == 0) { job = null!; return false; }
            job = _pending[0];
            _pending.RemoveAt(0);
            return true;
        }
    }

    async Task RenderAsync(RenderJob job, CancellationToken token)
    {
        if (job.Generation != job.Page.Generation || _session is null) return;
        try
        {
            var session = await EnsureOpenAsync();
            var sw = Stopwatch.StartNew();
            var (response, pixels) = await _worker.RenderAsync(
                new Request { DocId = session.DocId, PageIndex = job.Page.Index, Scale = job.Scale, Tile = job.Tile },
                RenderTimeout, token);
            if (!response.Ok || response.Render is not { } info || pixels is null)
            {
                await OnUi(() => job.Page.Error = $"Page {job.Page.Index + 1}: {response.Error} {response.Message}");
                return;
            }

            // Build and freeze the bitmap off the UI thread; the UI thread only swaps a reference.
            var bitmap = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Bgr32, null, pixels, info.Stride);
            bitmap.Freeze();
            Metrics.AddRender(sw.Elapsed.TotalMilliseconds);

            await OnUi(() =>
            {
                if (job.Generation != job.Page.Generation) return;
                if (job.Tile is { } t)
                {
                    if (!job.Page.RenderedTiles.Add((t.X, t.Y))) return;
                    var ppd = job.PixelsPerDip;
                    job.Page.Tiles.Add(new PlacedImage(t.X / ppd, t.Y / ppd, t.Width / ppd, t.Height / ppd, bitmap));
                }
                else
                {
                    job.Page.Image = bitmap;
                    job.Page.RenderedScale = job.Scale;
                }
                job.Page.Error = null;
                if (job.Page.BlankSince is { } since)
                {
                    Metrics.AddBlank((DateTime.UtcNow - since).TotalMilliseconds);
                    job.Page.BlankSince = null;
                }
            });
        }
        catch (WorkerException ex)
        {
            Metrics.AddCrash();
            await OnUi(() => job.Page.Error = $"Worker restarted: {ex.Message}");
        }
    }

    async Task<Session> EnsureOpenAsync()
    {
        var session = _session ?? throw new InvalidOperationException("No document is open.");
        if (_worker.IsRunning && session.WorkerGeneration == _worker.Generation) return session;

        // The worker died and came back without our document; reopen it transparently.
        var response = await _worker.SendAsync(new Request { Op = Op.Open, Path = session.Path, Password = session.Password }, TimeSpan.FromSeconds(120));
        if (!response.Ok || response.Doc is null) throw new WorkerException($"Reopen failed: {response.Message}");
        _session = session with { DocId = response.Doc.DocId, WorkerGeneration = _worker.Generation };
        return _session;
    }

    Task OnUi(Action action) => _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _loop; } catch (OperationCanceledException) { }
        await _worker.DisposeAsync();
    }
}

/// <summary>Numbers the spike's smoothness gate is judged on.</summary>
public sealed class ViewerMetrics
{
    readonly object _lock = new();
    readonly List<double> _renders = [];
    readonly List<double> _blanks = [];

    public int Crashes { get; private set; }
    public double WorstUiStallMs { get; set; }

    public void AddRender(double ms) { lock (_lock) _renders.Add(ms); }
    public void AddBlank(double ms) { lock (_lock) _blanks.Add(ms); }
    public void AddCrash() { lock (_lock) Crashes++; }

    public void Reset()
    {
        lock (_lock) { _renders.Clear(); _blanks.Clear(); Crashes = 0; WorstUiStallMs = 0; }
    }

    public string Summary()
    {
        lock (_lock)
        {
            return $"renders {_renders.Count}, p50 {P(_renders, 50):0} ms, p95 {P(_renders, 95):0} ms · " +
                   $"blank page p95 {P(_blanks, 95):0} ms, worst {(_blanks.Count > 0 ? _blanks.Max() : 0):0} ms · " +
                   $"worst UI stall {WorstUiStallMs:0} ms";
        }
    }

    static double P(List<double> values, int percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
