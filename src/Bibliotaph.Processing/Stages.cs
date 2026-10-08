using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;

namespace Bibliotaph.Processing;

/// <summary>How a stage ended. The lane turns it into the job's next state.</summary>
public abstract record StageOutcome
{
    /// <summary>The stage finished (complete, partial or skipped) and these stages come next.</summary>
    public sealed record Done(StageStatus Status, IReadOnlyList<Stage> Next, string? Reason = null) : StageOutcome;

    /// <summary>Waiting on the user: a password, a folder that is offline.</summary>
    public sealed record Blocked(string Reason) : StageOutcome;

    /// <summary>Failed. With <see cref="Retry"/>, the queue tries again later, up to its attempt limit.</summary>
    public sealed record Failed(string Reason, bool Retry) : StageOutcome;

    public static Done Complete(params Stage[] next) => new(StageStatus.Complete, next);
}

/// <summary>Remembered PDF passwords, by content hash. Slice 1c stores them in Windows Credential Manager.</summary>
public interface IPasswordStore
{
    string? Find(string contentHash);
}

public sealed class NoPasswords : IPasswordStore
{
    public string? Find(string contentHash) => null;
}

/// <summary>Everything a stage needs. One per indexing service.</summary>
public sealed record StageServices(
    LibraryStore Library,
    IndexStore Index,
    IndexQueries Queries,
    PdfWorkerPool Workers,
    ISourceFileReader Reader,
    IImageCodec Images,
    CoverCache Covers,
    IPasswordStore Passwords);

public interface IStage
{
    Stage Stage { get; }
    Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct);
}

/// <summary>
/// A document open in the index worker for the length of one job. If the worker restarts (a crash elsewhere),
/// the document is opened again before the next request.
/// </summary>
sealed class PdfSession(WorkerClient worker, string path, string? password) : IAsyncDisposable
{
    int _generation = -1;

    public DocInfo Doc { get; private set; } = null!;

    /// <summary>Opens the document, returning the worker's error response when it won't open.</summary>
    public async Task<Response?> OpenAsync(CancellationToken ct)
    {
        var response = await worker.SendAsync(new Request { Op = Op.Open, Path = path, Password = password }, ct: ct);
        if (!response.Ok) return response;
        Doc = response.Doc!;
        _generation = worker.Generation;
        return null;
    }

    public async Task<Response> SendAsync(Request request, CancellationToken ct)
    {
        if (worker.Generation != _generation)
        {
            var failed = await OpenAsync(ct);
            if (failed is not null) return failed;
        }
        return await worker.SendAsync(request with { DocId = Doc.DocId }, ct: ct);
    }

    public async Task<(Response Response, byte[]? Pixels)> RenderAsync(Request request, CancellationToken ct)
    {
        if (worker.Generation != _generation)
        {
            var failed = await OpenAsync(ct);
            if (failed is not null) return (failed, null);
        }
        return await worker.RenderAsync(request with { DocId = Doc.DocId }, ct: ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Doc is null || worker.Generation != _generation) return;
        try
        {
            await worker.SendAsync(new Request { Op = Op.Close, DocId = Doc.DocId });
        }
        catch (WorkerException)
        {
            // The worker died; the document went with it.
        }
    }
}

static class StageHelpers
{
    public const string Unreachable = "No copy of this file can be read right now: its folder is offline or the file has gone.";

    /// <summary>Opens the job's document in the index worker, or says why the stage can't run.</summary>
    public static async Task<(PdfSession? Session, StageOutcome? Outcome)> OpenPdfAsync(StageServices s, JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null) return (null, new StageOutcome.Blocked(Unreachable));

        var session = new PdfSession(s.Workers[WorkerSlot.Index], source.FullPath, s.Passwords.Find(job.ContentHash));
        var error = await session.OpenAsync(ct);
        if (error is null) return (session, null);
        await session.DisposeAsync();
        return (null, OpenFailure(error));
    }

    public static StageOutcome OpenFailure(Response error) => error.Error switch
    {
        ErrorKind.Password => new StageOutcome.Blocked("Password required. Open the book to enter it."),
        ErrorKind.Format => new StageOutcome.Failed("This file isn't a readable PDF; it may be damaged or incomplete.", Retry: false),
        ErrorKind.Security => new StageOutcome.Failed("This PDF uses a protection scheme Bibliotaph can't open.", Retry: false),
        ErrorKind.File => new StageOutcome.Failed("The file could not be read. It may be in use, or still downloading.", Retry: true),
        _ => new StageOutcome.Failed($"The PDF engine could not open the file ({error.Message}).", Retry: true),
    };
}

/// <summary>Probe: page count, sizes, labels, protection, document information and outline.</summary>
public sealed class ProbeStage(StageServices s) : IStage
{
    public Stage Stage => Stage.Probe;

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null) return new StageOutcome.Blocked(StageHelpers.Unreachable);
        var title = DisplayTitle.FromFileName(source.FullPath);

        if (SourceFormats.IsImage(source.Format))
        {
            (int Width, int Height)? size;
            await using (var stream = s.Reader.OpenRead(source.FullPath)) size = s.Images.ReadSize(stream);
            if (size is null) return new StageOutcome.Failed("This image could not be read; it may be damaged.", Retry: false);
            await s.Index.UpsertDocumentAsync(new DocRow
            {
                DocumentId = job.DocumentId,
                ContentHash = job.ContentHash,
                Format = source.Format,
                DisplayTitle = title,
                WidthPx = size.Value.Width,
                HeightPx = size.Value.Height,
                FolderHint = source.FolderHint,
            }, [], [], ct);
            await s.Library.SetProbeResultAsync(job.DocumentId, null, ProtectionType.None, "{}", ct);
            return StageOutcome.Complete(Stage.Text, Stage.Covers);
        }

        // The document is in the library from here, by its file name, even if it turns out not to open.
        var basic = new DocRow { DocumentId = job.DocumentId, ContentHash = job.ContentHash, Format = source.Format, DisplayTitle = title, FolderHint = source.FolderHint };
        var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
        if (session is null)
        {
            await s.Index.UpsertDocumentAsync(basic, [], [], ct);
            return failed!;
        }
        await using (session)
        {
            var doc = session.Doc;
            await s.Index.UpsertDocumentAsync(basic with
            {
                PageCount = doc.PageCount,
                Encrypted = doc.IsEncrypted,
                CanCopy = doc.CanCopy,
                MetaTitle = doc.Metadata.Title,
                MetaAuthor = doc.Metadata.Author,
                MetaSubject = doc.Metadata.Subject,
                MetaKeywords = doc.Metadata.Keywords,
            },
            [.. doc.PageSizes.Select((size, i) => new PageRow(i, doc.PageLabels.ElementAtOrDefault(i), size.Width, size.Height))],
            [.. doc.Outline.Select(o => new OutlineRow(o.Title, o.PageIndex, o.Depth))], ct);

            var protection = !doc.IsEncrypted ? ProtectionType.None
                : s.Passwords.Find(job.ContentHash) is not null ? ProtectionType.OpenPassword
                : ProtectionType.PermissionsOnly;
            var capabilities = JsonSerializer.Serialize(new { canCopy = doc.CanCopy, canPrint = doc.CanPrint, encrypted = doc.IsEncrypted });
            await s.Library.SetProbeResultAsync(job.DocumentId, doc.PageCount, protection, capabilities, ct);
        }
        return StageOutcome.Complete(Stage.Text, Stage.Covers);
    }
}

/// <summary>
/// Text: every page's text in runs of 32 pages, scored for quality. Low-quality pages are flagged for OCR but stay
/// searchable with what they have. A page that crashes the PDF engine is recorded and skipped, not retried forever (A18).
/// </summary>
public sealed class TextStage(StageServices s) : IStage
{
    public const int RunLength = 32;

    public Stage Stage => Stage.Text;

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null) return new StageOutcome.Blocked(StageHelpers.Unreachable);
        if (SourceFormats.IsImage(source.Format)) return new StageOutcome.Done(StageStatus.Skipped, [], "Images have no text layer.");

        var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
        if (session is null) return failed!;
        await using (session)
        {
            int ocrPages = 0, failedPages = 0;
            for (var first = 0; first < session.Doc.PageCount; first += RunLength)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(RunLength, session.Doc.PageCount - first);
                var pages = await ExtractRunAsync(session, first, count, ct) ?? await ExtractOneByOneAsync(session, first, count, ct);
                var rows = pages.Select(Score).ToList();
                ocrPages += rows.Count(r => r.NeedsOcr);
                failedPages += rows.Count(r => r.Error is not null);
                await s.Index.SetPageTextAsync(job.DocumentId, rows, ct);
            }

            var next = ocrPages > 0 ? new[] { Stage.Ocr } : [];
            return failedPages == 0
                ? new StageOutcome.Done(StageStatus.Complete, next)
                : new StageOutcome.Done(StageStatus.Partial, next, $"{failedPages} page(s) could not be read.");
        }
    }

    static PageTextRow Score(PageText page)
    {
        if (page.Error is not null) return new PageTextRow(page.PageIndex, "", "none", 0, NeedsOcr: false, page.Error);
        var quality = TextQuality.Score(page.Text, page.UnmappedChars);
        return new PageTextRow(page.PageIndex, page.Text, page.CharCount == 0 ? "none" : "pdf", quality, TextQuality.NeedsOcr(quality));
    }

    /// <summary>The whole run in one request, or null if the worker died on it.</summary>
    static async Task<List<PageText>?> ExtractRunAsync(PdfSession session, int first, int count, CancellationToken ct)
    {
        try
        {
            var response = await session.SendAsync(new Request { Op = Op.ExtractPages, PageIndex = first, PageCount = count }, ct);
            return response.Ok ? response.Pages : null;
        }
        catch (WorkerException)
        {
            return null;
        }
    }

    /// <summary>After a run fails, each page on its own, so one bad page costs only itself.</summary>
    static async Task<List<PageText>> ExtractOneByOneAsync(PdfSession session, int first, int count, CancellationToken ct)
    {
        var pages = new List<PageText>(count);
        for (var p = first; p < first + count; p++)
        {
            try
            {
                var response = await session.SendAsync(new Request { Op = Op.ExtractPages, PageIndex = p, PageCount = 1 }, ct);
                pages.Add(response.Ok ? response.Pages![0] : new PageText(p, "", 0, 0, response.Message ?? "The page could not be read."));
            }
            catch (WorkerException)
            {
                pages.Add(new PageText(p, "", 0, 0, "This page crashed the PDF engine."));
            }
        }
        return pages;
    }
}

/// <summary>Covers: the first page (or the image itself) as a JPEG in the cover cache. A missing cover is not an error.</summary>
public sealed class CoversStage(StageServices s) : IStage
{
    public Stage Stage => Stage.Covers;

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null) return new StageOutcome.Blocked(StageHelpers.Unreachable);

        byte[]? jpeg;
        if (SourceFormats.IsImage(source.Format))
        {
            await using var stream = s.Reader.OpenRead(source.FullPath);
            jpeg = s.Images.Thumbnail(stream, CoverCache.Width);
        }
        else
        {
            var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
            if (session is null) return failed!;
            await using (session)
            {
                if (session.Doc.PageCount == 0) return new StageOutcome.Done(StageStatus.Skipped, [], "The PDF has no pages.");
                var size = session.Doc.PageSizes[0];
                var scale = Math.Min(4.0, CoverCache.Width / Math.Max(1, size.Width));
                var (response, pixels) = await session.RenderAsync(new Request { PageIndex = 0, Scale = scale }, ct);
                jpeg = response.Ok && pixels is not null
                    ? s.Images.EncodeJpeg(pixels, response.Render!.Width, response.Render.Height, response.Render.Stride)
                    : null;
            }
        }

        if (jpeg is null) return new StageOutcome.Done(StageStatus.Skipped, [], "No cover could be drawn.");
        await s.Index.SetCoverAsync(job.DocumentId, s.Covers.Write(job.ContentHash, jpeg), ct);
        return StageOutcome.Complete();
    }
}

/// <summary>
/// OCR: each flagged page, rendered at 300 dpi and read by the worker's OCR engine. Progress is saved page by
/// page, so a paused or interrupted job carries on where it stopped. Page numbering never changes (A05).
/// </summary>
public sealed class OcrStage(StageServices s) : IStage
{
    public const double Dpi = 300;

    public Stage Stage => Stage.Ocr;

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var pages = await s.Queries.GetPagesNeedingOcrAsync(job.DocumentId, ct);
        if (pages.Count == 0) return StageOutcome.Complete();

        var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
        if (session is null) return failed!;
        await using (session)
        {
            var failedPages = 0;
            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();
                Response response;
                try
                {
                    response = await session.SendAsync(new Request { Op = Op.Ocr, PageIndex = page.PdfPage, Scale = Dpi / 72 }, ct);
                }
                catch (WorkerException)
                {
                    await s.Index.SetPageErrorAsync(job.DocumentId, page.PdfPage, "This page crashed the PDF engine during OCR.", ct);
                    failedPages++;
                    continue;
                }

                if (response.Error == ErrorKind.OcrUnavailable) return new StageOutcome.Blocked(response.Message ?? "OCR is not available on this computer.");
                if (!response.Ok)
                {
                    await s.Index.SetPageErrorAsync(job.DocumentId, page.PdfPage, response.Message ?? "OCR failed on this page.", ct);
                    failedPages++;
                    continue;
                }

                var ocr = response.Ocr!;
                await s.Index.SetOcrPageAsync(job.DocumentId, page.PdfPage, ocr.Text, TextQuality.Score(ocr.Text, 0),
                    [.. ocr.Words.Select(w => new OcrWordRow(w.Text, w.Box.Left, w.Box.Top, w.Box.Right, w.Box.Bottom))], ct);
            }
            return failedPages == 0
                ? StageOutcome.Complete()
                : new StageOutcome.Done(StageStatus.Partial, [], $"OCR failed on {failedPages} of {pages.Count} page(s).");
        }
    }
}
