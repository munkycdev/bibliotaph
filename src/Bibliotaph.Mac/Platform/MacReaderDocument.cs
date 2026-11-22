using Bibliotaph.Core.Reading;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Presentation.Services;
using CoreGraphics;

namespace Bibliotaph.Mac.Platform;

/// <summary>Readers' documents on the Mac, each over the viewer worker its reader leased.</summary>
public sealed class MacReaderDocuments : IReaderDocuments
{
    public IReaderDocument Create(WorkerClient worker) => new MacReaderDocument(worker);
}

/// <summary>
/// One open PDF in a viewer worker: its text, finds and page pictures. A worker that restarts loses its documents, so
/// the next request opens the book in the new one.
/// </summary>
public sealed class MacReaderDocument(WorkerClient worker) : IReaderDocument
{
    static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(120);

    readonly SemaphoreSlim _gate = new(1, 1);
    Session? _session;

    sealed record Session(string Path, string? Password, int DocId, int WorkerGeneration);

    public async Task<DocInfo> OpenAsync(string path, string? password, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await CloseSessionAsync();
            var response = await worker.SendAsync(new Request { Op = Op.Open, Path = path, Password = password }, OpenTimeout, ct);
            if (!response.Ok || response.Doc is null) throw new PdfOpenException(response.Error, response.Message);
            _session = new Session(path, password, response.Doc.DocId, worker.Generation);
            return response.Doc;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseSessionAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PageTextLayer> GetTextAsync(int pageIndex, CancellationToken ct = default)
    {
        var session = await EnsureOpenAsync(ct);
        var response = await worker.SendAsync(
            new Request { Op = Op.ExtractText, DocId = session.DocId, PageIndex = pageIndex, IncludeCharBoxes = true }, RenderTimeout, ct);
        if (!response.Ok || response.Text is not { } text) return PageTextLayer.Empty;
        var boxes = text.CharBoxes ?? [];
        if (boxes.Count != text.Text.Length) return PageTextLayer.Empty;
        return new PageTextLayer(text.Text, [.. boxes.Select(PdfRects.ToPageRect)]);
    }

    public async Task<IReadOnlyList<SearchHit>> FindAsync(string query, int pageIndex = -1, int maxHits = 2000, CancellationToken ct = default)
    {
        var session = await EnsureOpenAsync(ct);
        var response = await worker.SendAsync(
            new Request { Op = Op.Find, DocId = session.DocId, PageIndex = pageIndex, Query = query, MaxHits = maxHits }, OpenTimeout, ct);
        return response.Ok ? response.Hits ?? [] : [];
    }

    /// <summary>A page drawn at <paramref name="scale"/> pixels a PDF point, copied out of the worker's shared memory once.</summary>
    public async Task<CGImage> RenderAsync(int pageIndex, double scale, CancellationToken ct = default)
    {
        var session = await EnsureOpenAsync(ct);
        var (response, image) = await worker.RenderAsync(
            new Request { DocId = session.DocId, PageIndex = pageIndex, Scale = scale },
            static (info, pixels) => Image(info, pixels), RenderTimeout, ct);
        return response.Ok && image is not null ? image : throw new PdfRenderException(response.Error, response.Message);
    }

    static CGImage Image(RenderInfo info, nint pixels)
    {
        var bytes = new byte[info.Stride * info.Height];
        System.Runtime.InteropServices.Marshal.Copy(pixels, bytes, 0, bytes.Length);
        using var provider = new CGDataProvider(bytes);
        using var space = CGColorSpace.CreateSrgb();
        // The worker draws BGRA onto white, so the alpha byte carries nothing.
        return new CGImage(info.Width, info.Height, 8, 32, info.Stride, space,
            CGBitmapFlags.ByteOrder32Little | CGBitmapFlags.NoneSkipFirst, provider, null, true, CGColorRenderingIntent.Default);
    }

    async Task<Session> EnsureOpenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var session = _session ?? throw new InvalidOperationException("No document is open.");
            if (session.WorkerGeneration == worker.Generation && worker.IsRunning) return session;
            var response = await worker.SendAsync(new Request { Op = Op.Open, Path = session.Path, Password = session.Password }, OpenTimeout, ct);
            if (!response.Ok || response.Doc is null) throw new PdfOpenException(response.Error, response.Message);
            _session = session with { DocId = response.Doc.DocId, WorkerGeneration = worker.Generation };
            return _session;
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task CloseSessionAsync()
    {
        var old = _session;
        _session = null;
        if (old is null || !worker.IsRunning || old.WorkerGeneration != worker.Generation) return;
        try
        {
            await worker.SendAsync(new Request { Op = Op.Close, DocId = old.DocId });
        }
        catch (WorkerException)
        {
            // The worker went away; the document went with it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
        _gate.Dispose();
    }
}

/// <summary>The reader's page strip isn't on the Mac yet, so no thumbnails are drawn.</summary>
public sealed class NoPageThumbnails : IPageThumbnails
{
    public object? TryGet(string contentHash, int pageIndex) => null;

    public Task<object?> LoadAsync(string contentHash, int pageIndex, double scale, IReaderDocument document, bool keepOnDisk, CancellationToken ct) =>
        Task.FromResult<object?>(null);

    public void Touch(string contentHash)
    {
    }
}
