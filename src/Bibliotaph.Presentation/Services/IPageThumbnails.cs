using Bibliotaph.Pdf.Host;

namespace Bibliotaph.Presentation.Services;

/// <summary>
/// Page thumbnails for the reader's strip, by book (content hash) and page. A thumbnail is the platform's own image (an
/// ImageSource on Windows), drawn by the document's own renderer: the strip's view model decides which pages to draw
/// and when to let them go, and only the view looks inside one.
/// </summary>
public interface IPageThumbnails
{
    /// <summary>A thumbnail already decoded, so a thumbnail scrolled back into view shows without waiting.</summary>
    object? TryGet(string contentHash, int pageIndex);

    /// <summary>
    /// A page's thumbnail, drawn by <paramref name="document"/> when it has nothing else to do if it isn't kept already.
    /// A book whose text was forgotten (<paramref name="keepOnDisk"/> false) has its thumbnails kept in memory only. Null
    /// when the page can't be drawn; cancelled with <paramref name="ct"/>.
    /// </summary>
    Task<object?> LoadAsync(string contentHash, int pageIndex, double scale, IReaderDocument document, bool keepOnDisk, CancellationToken ct);

    /// <summary>A book has opened: its thumbnails on disk are the last to go when the cache makes room.</summary>
    void Touch(string contentHash);
}
