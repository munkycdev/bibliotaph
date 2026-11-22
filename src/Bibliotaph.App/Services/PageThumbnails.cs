using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using Bibliotaph.Viewer;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Page thumbnails for the reader's strip, as WPF images, by book (content hash) and page. The most recently used few hundred are kept
/// decoded; behind them is the thumbnail cache on disk (<see cref="PageThumbnailCache"/>); a page in neither is drawn by
/// the viewer worker that has the book open, after the pages on screen, and kept in both. One for the app, so a book
/// opened again, or in another window, finds its thumbnails ready.
/// </summary>
public sealed class PageThumbnails(PageThumbnailCache cache, ILogger<PageThumbnails> log) : IPageThumbnails
{
    /// <summary>About 50 MB of thumbnails: a few long strips' worth.</summary>
    const int Capacity = 400;

    readonly Lock _lock = new();
    readonly Dictionary<(string Hash, int Page), LinkedListNode<((string Hash, int Page) Key, ImageSource Image)>> _byKey = [];
    readonly LinkedList<((string Hash, int Page) Key, ImageSource Image)> _recent = new();

    public object? TryGet(string contentHash, int pageIndex) => Find(contentHash, pageIndex);

    ImageSource? Find(string contentHash, int pageIndex)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue((contentHash, pageIndex), out var node)) return null;
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>From memory, else from the cache on disk, else drawn by the document's renderer.</summary>
    public async Task<object?> LoadAsync(string contentHash, int pageIndex, double scale, IReaderDocument document, bool keepOnDisk, CancellationToken ct)
    {
        // The WPF reader's documents are its renderers (PdfRenderers), which draw the strip's pages after those on screen.
        var renderer = (PdfRenderer)document;
        if (Find(contentHash, pageIndex) is { } known) return known;
        ImageSource? image = keepOnDisk ? await Task.Run(() => Decode(cache.Read(contentHash, pageIndex)), ct) : null;
        if (image is null)
        {
            if (await renderer.RenderWhenIdleAsync(pageIndex, scale, ct) is not { } drawn) return null;
            image = drawn;
            if (keepOnDisk) _ = Task.Run(() => Save(contentHash, pageIndex, drawn), CancellationToken.None);
        }
        Remember((contentHash, pageIndex), image);
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

    void Remember((string Hash, int Page) key, ImageSource image)
    {
        lock (_lock)
        {
            if (_byKey.ContainsKey(key)) return;
            _byKey[key] = _recent.AddFirst((key, image));
            if (_recent.Count <= Capacity) return;
            _byKey.Remove(_recent.Last!.Value.Key);
            _recent.RemoveLast();
        }
    }

    void Save(string contentHash, int pageIndex, BitmapSource drawn)
    {
        try
        {
            cache.Write(contentHash, pageIndex, WpfImageCodec.EncodeJpeg(drawn));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // Kept in memory anyway, and drawn again next time.
            log.LogDebug(ex, "A page thumbnail couldn't be kept");
        }
    }

    BitmapImage? Decode(byte[]? jpeg)
    {
        if (jpeg is null) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(jpeg);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            // A damaged file: the page is drawn again, which replaces it.
            log.LogDebug(ex, "A page thumbnail couldn't be read");
            return null;
        }
    }
}
