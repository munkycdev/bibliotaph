using Bibliotaph.Core.Reading;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;
using AppKit;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using Foundation;
using Microsoft.Extensions.Logging;
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

    /// <summary>The page's links: to another page, with where on it, or to the web.</summary>
    public async Task<IReadOnlyList<PageLink>> GetLinksAsync(int pageIndex, CancellationToken ct = default)
    {
        var session = await EnsureOpenAsync(ct);
        var response = await worker.SendAsync(new Request { Op = Op.Links, DocId = session.DocId, PageIndex = pageIndex }, RenderTimeout, ct);
        if (!response.Ok || response.Links is not { } links) return [];
        return [.. links.Where(l => l.PageIndex >= 0 || l.Uri is not null).Select(l => new PageLink(l.Box.ToPageRect(), l.PageIndex, l.Top, l.Uri))];
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

/// <summary>
/// Page thumbnails for the reader's strip, by book (content hash) and page: the most recently used few hundred kept
/// decoded, behind them the thumbnail cache on disk, and a page in neither drawn by the book's viewer worker.
/// </summary>
public sealed class MacPageThumbnails(PageThumbnailCache cache, ILogger<MacPageThumbnails> log) : IPageThumbnails
{
    const int Capacity = 400;
    readonly Lock _lock = new();
    readonly Dictionary<(string Hash, int Page), LinkedListNode<((string Hash, int Page) Key, NSImage Image)>> _byKey = [];
    readonly LinkedList<((string Hash, int Page) Key, NSImage Image)> _recent = new();

    public object? TryGet(string contentHash, int pageIndex)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue((contentHash, pageIndex), out var node)) return null;
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Image;
        }
    }

    public async Task<object?> LoadAsync(string contentHash, int pageIndex, double scale, IReaderDocument document, bool keepOnDisk, CancellationToken ct)
    {
        if (TryGet(contentHash, pageIndex) is NSImage known) return known;
        var image = keepOnDisk ? await Task.Run(() => cache.Read(contentHash, pageIndex) is { } jpeg ? new NSImage(NSData.FromArray(jpeg)) : null, ct) : null;
        if (image is null)
        {
            if (document is not MacReaderDocument pdf) return null;
            using var drawn = await pdf.RenderAsync(pageIndex, scale, ct);
            image = new NSImage(drawn, new CGSize(drawn.Width, drawn.Height));
            if (keepOnDisk && MacImageCodec.Jpeg(drawn) is { } jpeg) _ = Task.Run(() => Keep(contentHash, pageIndex, jpeg), CancellationToken.None);
        }
        lock (_lock)
        {
            if (!_byKey.ContainsKey((contentHash, pageIndex)))
            {
                _byKey[(contentHash, pageIndex)] = _recent.AddFirst(((contentHash, pageIndex), image));
                if (_recent.Count > Capacity)
                {
                    _byKey.Remove(_recent.Last!.Value.Key);
                    _recent.RemoveLast();
                }
            }
        }
        return image;
    }

    public void Touch(string contentHash) => _ = Task.Run(() =>
    {
        try
        {
            cache.Touch(contentHash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "The page thumbnail cache couldn't be tidied");
        }
    });

    void Keep(string contentHash, int pageIndex, byte[] jpeg)
    {
        try
        {
            cache.Write(contentHash, pageIndex, jpeg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory anyway, and drawn again next time.
            log.LogDebug(ex, "A page thumbnail couldn't be kept");
        }
    }
}
