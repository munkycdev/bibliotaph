using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Pdf.Host;

/// <summary>Indexing's PDF engine on Windows: PDFium in the pool's index worker.</summary>
public sealed class WorkerPdfEngine(PdfWorkerPool workers) : IPdfEngine
{
    public async Task<PdfResult<IPdfDocument>> OpenAsync(string path, string? password, CancellationToken ct)
    {
        var document = new WorkerPdfDocument(workers.Index, path, password);
        var failed = await document.OpenAsync(ct);
        return failed is null
            ? PdfResult.Success<IPdfDocument>(document)
            : PdfResult.Fail<IPdfDocument>(failed.Error, failed.Message);
    }
}

/// <summary>
/// A document open in the index worker. If the worker restarts (a crash elsewhere), the document is opened again before
/// the next request.
/// </summary>
sealed class WorkerPdfDocument(WorkerClient worker, string path, string? password) : IPdfDocument
{
    int _generation = -1;

    public DocInfo Info { get; private set; } = null!;

    /// <summary>Opens the document, returning the worker's error response when it won't open.</summary>
    public async Task<Response?> OpenAsync(CancellationToken ct)
    {
        var response = await worker.SendAsync(new Request { Op = Op.Open, Path = path, Password = password }, ct: ct);
        if (!response.Ok) return response;
        Info = response.Doc!;
        _generation = worker.Generation;
        return null;
    }

    public async Task<PdfResult<IReadOnlyList<PageText>>> ExtractPagesAsync(int first, int count, CancellationToken ct)
    {
        var response = await SendAsync(new Request { Op = Op.ExtractPages, PageIndex = first, PageCount = count }, ct);
        return response.Ok ? PdfResult.Success<IReadOnlyList<PageText>>(response.Pages!) : Fail<IReadOnlyList<PageText>>(response);
    }

    public async Task<PdfResult<PageImage>> RenderAsync(int page, double scale, CancellationToken ct)
    {
        if (await ReopenAsync(ct) is { } failed) return Fail<PageImage>(failed);
        var (response, pixels) = await worker.RenderAsync(new Request { DocId = Info.DocId, PageIndex = page, Scale = scale }, ct: ct);
        if (!response.Ok || pixels is null) return Fail<PageImage>(response);
        var render = response.Render!;
        return PdfResult.Success(new PageImage(pixels, render.Width, render.Height, render.Stride));
    }

    public async Task<PdfResult<OcrInfo>> OcrAsync(int page, double scale, CancellationToken ct)
    {
        var response = await SendAsync(new Request { Op = Op.Ocr, PageIndex = page, Scale = scale }, ct);
        return response.Ok ? PdfResult.Success(response.Ocr!) : Fail<OcrInfo>(response);
    }

    async Task<Response> SendAsync(Request request, CancellationToken ct) =>
        await ReopenAsync(ct) ?? await worker.SendAsync(request with { DocId = Info.DocId }, ct: ct);

    /// <summary>Opens the document again in a worker that restarted since; the error response if it won't open.</summary>
    Task<Response?> ReopenAsync(CancellationToken ct) =>
        worker.Generation == _generation ? Task.FromResult<Response?>(null) : OpenAsync(ct);

    static PdfResult<T> Fail<T>(Response response) =>
        PdfResult.Fail<T>(response.Error == ErrorKind.None ? ErrorKind.Unknown : response.Error, response.Message);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Info is null || worker.Generation != _generation) return;
            await worker.SendAsync(new Request { Op = Op.Close, DocId = Info.DocId });
        }
        catch (WorkerException)
        {
            // The worker died; the document went with it.
        }
    }
}
