using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bibliotaph.Core.Reading;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;

namespace Bibliotaph.Viewer;

/// <summary>A render request. Tiles carry their pixel rectangle; whole pages do not.</summary>
public sealed record RenderJob(ViewerPage Page, int Generation, double Scale, double PixelsPerDip, PixelRect? Tile = null, bool Preview = false);

/// <summary>A document that would not open, with the worker's reason.</summary>
public sealed class PdfOpenException(ErrorKind kind, string? message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
}

/// <summary>
/// Feeds one open document's render jobs to the viewer worker one at a time, in the order the view asks (visible
/// previews, visible pages, then prefetch), and hands frozen bitmaps to the UI thread. Each bitmap is built straight
/// from the worker's shared memory, so pixels are copied once. Reopens the document if the worker restarts.
/// </summary>
public sealed class PdfRenderer : IAsyncDisposable
{
    static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(120);

    readonly WorkerClient _worker;
    readonly Dispatcher _dispatcher;
    readonly Lock _lock = new();
    readonly SemaphoreSlim _signal = new(0, 1);
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;

    IReadOnlyList<RenderJob> _pending = [];
    Session? _session;

    sealed record Session(string Path, string? Password, int DocId, int WorkerGeneration);

    public PdfRenderer(WorkerClient worker, Dispatcher dispatcher)
    {
        _worker = worker;
        _dispatcher = dispatcher;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Raised on the UI thread when a page's pixels change, so a view can tell a page has been drawn.</summary>
    public event Action<ViewerPage>? Rendered;

    /// <summary>Opens a document, closing the one before. Throws <see cref="PdfOpenException"/> with the reason it failed.</summary>
    public async Task<DocInfo> OpenAsync(string path, string? password, CancellationToken ct = default)
    {
        lock (_lock) _pending = [];
        await CloseAsync();
        var response = await _worker.SendAsync(new Request { Op = Op.Open, Path = path, Password = password }, OpenTimeout, ct);
        if (!response.Ok || response.Doc is null) throw new PdfOpenException(response.Error, response.Message);
        _session = new Session(path, password, response.Doc.DocId, _worker.Generation);
        return response.Doc;
    }

    public async Task CloseAsync()
    {
        lock (_lock) _pending = [];
        if (_session is { } old && _worker.IsRunning && old.WorkerGeneration == _worker.Generation)
        {
            try
            {
                await _worker.SendAsync(new Request { Op = Op.Close, DocId = old.DocId });
            }
            catch (WorkerException)
            {
                // The worker went away; the document went with it.
            }
        }
        _session = null;
    }

    /// <summary>Replaces whatever is still queued: only the current viewport matters.</summary>
    public void Submit(IReadOnlyList<RenderJob> jobs)
    {
        lock (_lock) _pending = jobs;
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
        }
    }

    /// <summary>A page's text with a box per character, for selection.</summary>
    public async Task<PageTextLayer> GetTextAsync(int pageIndex, CancellationToken ct = default)
    {
        var session = await EnsureOpenAsync(ct);
        var response = await _worker.SendAsync(
            new Request { Op = Op.ExtractText, DocId = session.DocId, PageIndex = pageIndex, IncludeCharBoxes = true }, RenderTimeout, ct);
        if (!response.Ok || response.Text is not { } text) return PageTextLayer.Empty;
        var boxes = text.CharBoxes ?? [];
        if (boxes.Count != text.Text.Length) return PageTextLayer.Empty;
        return new PageTextLayer(text.Text, [.. boxes.Select(ToPageRect)]);
    }

    /// <summary>Where a phrase appears on one page (or in the whole book with -1), with its rectangles.</summary>
    public async Task<IReadOnlyList<SearchHit>> FindAsync(string query, int pageIndex = -1, int maxHits = 2000, CancellationToken ct = default)
    {
        var session = await EnsureOpenAsync(ct);
        var response = await _worker.SendAsync(
            new Request { Op = Op.Find, DocId = session.DocId, PageIndex = pageIndex, Query = query, MaxHits = maxHits }, OpenTimeout, ct);
        return response.Ok ? response.Hits ?? [] : [];
    }

    public static PageRect ToPageRect(PdfRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>
    /// Renders one page or tile and returns the frozen bitmap, without touching any page model. Used by the render
    /// queue and by --measure-viewer.
    /// </summary>
    public async Task<BitmapSource?> RenderBitmapAsync(int pageIndex, double scale, PixelRect? tile = null, CancellationToken ct = default)
    {
        var session = await EnsureOpenAsync(ct);
        var (response, bitmap) = await _worker.RenderAsync(
            new Request { DocId = session.DocId, PageIndex = pageIndex, Scale = scale, Tile = tile },
            static (info, pixels) =>
            {
                // Built off the UI thread straight from shared memory and frozen, so the UI thread only swaps a reference.
                var source = BitmapSource.Create(info.Width, info.Height, 96, 96, PixelFormats.Bgr32, null, pixels, info.Stride * info.Height, info.Stride);
                source.Freeze();
                return source;
            },
            RenderTimeout, ct);
        return response.Ok ? bitmap : throw new PdfRenderException(response.Error, response.Message);
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposed.
        }
    }

    bool TryTake(out RenderJob job)
    {
        lock (_lock)
        {
            if (_pending.Count == 0)
            {
                job = null!;
                return false;
            }
            job = _pending[0];
            _pending = _pending.Count == 1 ? [] : [.. _pending.Skip(1)];
            return true;
        }
    }

    async Task RenderAsync(RenderJob job, CancellationToken token)
    {
        if (job.Generation != job.Page.Generation || _session is null) return;
        try
        {
            var bitmap = await RenderBitmapAsync(job.Page.Index, job.Scale, job.Tile, token);
            if (bitmap is null) return;
            await OnUi(() => Place(job, bitmap));
        }
        catch (PdfRenderException ex)
        {
            await OnUi(() => job.Page.Error = $"Page {job.Page.Index + 1} could not be drawn. {ex.Message}");
        }
        catch (PagePoisonedException)
        {
            await OnUi(() => job.Page.Error = $"Page {job.Page.Index + 1} stopped the PDF reader twice, so it is skipped.");
        }
        catch (WorkerException ex)
        {
            await OnUi(() => job.Page.Error = $"The PDF reader restarted. {ex.Message}");
        }
    }

    void Place(RenderJob job, BitmapSource bitmap)
    {
        var page = job.Page;
        if (job.Generation != page.Generation) return;
        if (job.Tile is { } t)
        {
            if (!page.RenderedTiles.Add((t.X, t.Y))) return;
            var ppd = job.PixelsPerDip;
            page.Tiles.Add(new PlacedImage(t.X / ppd, t.Y / ppd, t.Width / ppd, t.Height / ppd, bitmap));
        }
        else
        {
            // A late preview never replaces the full page.
            if (job.Preview && page.Image is not null) return;
            page.Image = bitmap;
            page.RenderedScale = job.Scale;
            page.IsPreview = job.Preview;
        }
        page.Error = null;
        Rendered?.Invoke(page);
    }

    async Task<Session> EnsureOpenAsync(CancellationToken ct)
    {
        var session = _session ?? throw new InvalidOperationException("No document is open.");
        if (_worker.IsRunning && session.WorkerGeneration == _worker.Generation) return session;

        // The worker died and came back without the document; reopen it.
        var response = await _worker.SendAsync(new Request { Op = Op.Open, Path = session.Path, Password = session.Password }, OpenTimeout, ct);
        if (!response.Ok || response.Doc is null) throw new WorkerException($"The document could not be reopened: {response.Message}");
        _session = session with { DocId = response.Doc.DocId, WorkerGeneration = _worker.Generation };
        return _session;
    }

    Task OnUi(Action action) => _dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        await CloseAsync();
        _stop.Dispose();
        _signal.Dispose();
    }
}

/// <summary>A page the worker could not render, with its reason.</summary>
public sealed class PdfRenderException(ErrorKind kind, string? message) : Exception(message)
{
    public ErrorKind Kind { get; } = kind;
}
