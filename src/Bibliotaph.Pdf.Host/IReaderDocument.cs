using Bibliotaph.Core.Reading;
using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Pdf.Host;

/// <summary>
/// One document open in a viewer worker, as a reader's view model sees it: opened, searched and read for its text.
/// Drawing its pages belongs to the platform's page surface, which knows the implementation it was given.
/// </summary>
public interface IReaderDocument : IAsyncDisposable
{
    /// <summary>Opens a document, closing the one before. Throws <see cref="PdfOpenException"/> with the reason it failed.</summary>
    Task<DocInfo> OpenAsync(string path, string? password, CancellationToken ct = default);

    Task CloseAsync();

    /// <summary>A page's text with a box per character, for selection.</summary>
    Task<PageTextLayer> GetTextAsync(int pageIndex, CancellationToken ct = default);

    /// <summary>Where a phrase appears on one page (or in the whole book with -1), with its rectangles.</summary>
    Task<IReadOnlyList<SearchHit>> FindAsync(string query, int pageIndex = -1, int maxHits = 2000, CancellationToken ct = default);
}

/// <summary>Makes the platform's <see cref="IReaderDocument"/> over a viewer worker.</summary>
public interface IReaderDocuments
{
    IReaderDocument Create(WorkerClient worker);
}

public static class PdfRects
{
    public static PageRect ToPageRect(this PdfRect r) => new(r.Left, r.Top, r.Right, r.Bottom);
}
