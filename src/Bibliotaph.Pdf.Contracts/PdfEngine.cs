namespace Bibliotaph.Pdf.Contracts;

/// <summary>
/// What indexing needs from a PDF engine. On Windows that is PDFium in the out-of-process worker; an engine for
/// another platform answers the same calls, and says <see cref="ErrorKind.OcrUnavailable"/> for what it can't do.
/// </summary>
public interface IPdfEngine
{
    /// <summary>Opens the file at <paramref name="path"/>, or says why it won't open.</summary>
    Task<PdfResult<IPdfDocument>> OpenAsync(string path, string? password, CancellationToken ct);
}

/// <summary>An open document. Disposing it closes the document in the engine.</summary>
public interface IPdfDocument : IAsyncDisposable
{
    DocInfo Info { get; }

    /// <summary>The text of <paramref name="count"/> pages from <paramref name="first"/>, clipped at the last page.</summary>
    Task<PdfResult<IReadOnlyList<PageText>>> ExtractPagesAsync(int first, int count, CancellationToken ct);

    /// <summary>A page drawn at <paramref name="scale"/> pixels per point.</summary>
    Task<PdfResult<PageImage>> RenderAsync(int page, double scale, CancellationToken ct);

    /// <summary>A page drawn at <paramref name="scale"/> and read by the engine's OCR.</summary>
    Task<PdfResult<OcrInfo>> OcrAsync(int page, double scale, CancellationToken ct);
}

/// <summary>An engine call's value, or why there is none.</summary>
public sealed record PdfResult<T>(T? Value, ErrorKind Error, string? Message)
{
    public bool Ok => Error == ErrorKind.None;
}

public static class PdfResult
{
    public static PdfResult<T> Success<T>(T value) => new(value, ErrorKind.None, null);

    public static PdfResult<T> Fail<T>(ErrorKind error, string? message) => new(default, error, message);
}

/// <summary>A drawn page: BGRA pixels, top-down, <see cref="Stride"/> bytes per row.</summary>
public sealed record PageImage(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>
/// The engine stopped while handling a call: it crashed, hung or was killed. <see cref="Retry"/> is false when trying
/// again can't help, as with a page that has already brought the engine down more than once.
/// </summary>
public class PdfEngineException(string message) : Exception(message)
{
    public virtual bool Retry => true;
}
