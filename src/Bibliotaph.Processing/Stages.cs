using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Ebooks;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Processing;

/// <summary>How a stage ended. The lane turns it into the job's next state.</summary>
public abstract record StageOutcome
{
    /// <summary>
    /// The stage finished (complete, partial or skipped). Complete or partial, it is followed by the stages that always
    /// come after it (<see cref="IStage.After"/>) and by <paramref name="Next"/>, those it asks for this time only, as
    /// Text asks for OCR only when pages need it. A skipped stage is followed only by <paramref name="Next"/>.
    /// </summary>
    public sealed record Done(StageStatus Status, IReadOnlyList<Stage> Next, string? Reason = null) : StageOutcome;

    /// <summary>Waiting on the user: a password, a folder that is offline.</summary>
    public sealed record Blocked(string Reason) : StageOutcome
    {
        /// <summary>No readable copy. Released by itself once one can be read again.</summary>
        public const string Unreachable = "No copy of this file can be read right now: its folder is offline or the file has gone.";
    }

    /// <summary>Failed. With <see cref="Retry"/>, the queue tries again later, up to its attempt limit.</summary>
    public sealed record Failed(string Reason, bool Retry) : StageOutcome;

    /// <summary>Not yet: the job waits at least <see cref="Wait"/> without using an attempt, as Classify waits for OCR.</summary>
    public sealed record Later(TimeSpan Wait, string? Reason = null) : StageOutcome;

    /// <summary>
    /// The lane can't work right now for a reason that isn't this job's, such as a model endpoint that isn't running. The
    /// job goes back without using an attempt, and the lane waits and says why until a job gets through.
    /// </summary>
    public sealed record Unavailable(string Reason) : StageOutcome;

    public static Done Complete(params Stage[] next) => new(StageStatus.Complete, next);
}

/// <summary>
/// PDF passwords the index may open files with, by content hash: those remembered in Windows Credential Manager, and
/// those entered this sitting to make a book searchable, which live only in memory.
/// </summary>
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
    EntryStore Entries,
    IndexStore Index,
    IndexQueries Queries,
    IPdfEngine Pdf,
    ISourceFileReader Reader,
    IImageCodec Images,
    CoverCache Covers,
    IPasswordStore Passwords,
    SourceFiles Sources,
    MetadataProjector Projector,
    PartStore Parts);

/// <summary>
/// One step of reading a book. Besides doing its work, a stage says where it runs, what queues it and what the app
/// calls it, so the lanes, startup and the app's screens all follow from the stages registered. What a stage says about
/// itself never depends on the services it is given.
/// </summary>
public interface IStage
{
    Stage Stage { get; }

    /// <summary>The lane that runs it. A lane runs one job at a time, and pauses as a whole.</summary>
    Lane Lane { get; }

    /// <summary>
    /// The stage whose finishing, complete or partly, queues this one; null for the first stage, and for one that the
    /// stage before asks for only when needed, as Text asks for OCR only when pages need it.
    /// </summary>
    Stage? After { get; }

    /// <summary>
    /// Whether, at startup, it is queued for every book that finished <see cref="After"/> without ever having had it,
    /// so a library read before the stage existed gets it without reading anything again.
    /// </summary>
    bool Backfill { get; }

    /// <summary>What the app calls it.</summary>
    StageNames Names { get; }

    Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct);
}

/// <summary>
/// What the app calls a stage: <paramref name="Label"/> in a book's list of processing steps, <paramref name="Step"/>
/// before the reason in Files needing attention, and <paramref name="Running"/> before the book's title while it runs.
/// </summary>
public sealed record StageNames(string Label, string Step, string Running);

/// <summary>
/// How long a stage that needs another of the book's stages to finish first waits before looking again. It looks
/// sooner when that stage ends, since the job board wakes a book's deferred jobs when another of its stages finishes.
/// </summary>
static class StageWaits
{
    /// <summary>For the book's scanned pages to be read, which can take a while.</summary>
    public static readonly TimeSpan ForOcr = TimeSpan.FromMinutes(5);

    /// <summary>For the book's hints from names, which take moments.</summary>
    public static readonly TimeSpan ForHints = TimeSpan.FromSeconds(30);
}

/// <summary>A stage that runs only while something outside the queue allows it, as Classify runs only while AI is set up.</summary>
public interface IGatedStage : IStage
{
    bool IsReady { get; }

    /// <summary>Raised when <see cref="IsReady"/> may have changed, from any thread.</summary>
    event EventHandler? ReadyChanged;
}

/// <summary>
/// A document open in the PDF engine for the length of one job. The file it reads is held until the session ends, so
/// a file extracted from a ZIP isn't evicted under it.
/// </summary>
sealed class PdfSession(IPdfDocument document, LocalFile file) : IAsyncDisposable
{
    public DocInfo Doc => document.Info;

    public IPdfDocument Document => document;

    /// <summary>Opens <paramref name="file"/>: the session, or the engine's reason it won't open.</summary>
    public static async Task<(PdfSession? Session, PdfResult<IPdfDocument> Opened)> OpenAsync(IPdfEngine engine, LocalFile file, string? password,
        CancellationToken ct)
    {
        PdfResult<IPdfDocument> opened;
        try
        {
            opened = await engine.OpenAsync(file.Path, password, ct);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
        if (opened.Ok) return (new PdfSession(opened.Value!, file), opened);
        await file.DisposeAsync();
        return (null, opened);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await document.DisposeAsync();
        }
        finally
        {
            await file.DisposeAsync();
        }
    }
}

static class StageHelpers
{
    /// <summary>
    /// Records a probed document in index.db, and the library cards that show it, so it appears in the library from
    /// here.
    /// </summary>
    public static async Task UpsertDocumentAsync(StageServices s, DocRow doc, IReadOnlyList<PageRow> pages, IReadOnlyList<OutlineRow> outline,
        CancellationToken ct)
    {
        await s.Index.UpsertDocumentAsync(doc, pages, outline, ct);
        await s.Projector.ProjectShownByAsync(doc.DocumentId, ct);
    }

    /// <summary>
    /// The file to read for a source: itself, or extracted from its ZIP. A ZIP that changed since it was read blocks
    /// the stage until the hasher has read it again.
    /// </summary>
    public static async Task<(LocalFile? File, StageOutcome? Outcome)> OpenFileAsync(StageServices s, DocumentSource source, CancellationToken ct)
    {
        try
        {
            return (await s.Sources.OpenAsync(source, ct), null);
        }
        catch (SourceChangedException)
        {
            return (null, new StageOutcome.Blocked(StageOutcome.Blocked.Unreachable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var from = source.InArchive ? " from its ZIP" : "";
            return (null, new StageOutcome.Failed($"The file could not be read{from}: {ex.Message}", Retry: ex is not InvalidDataException));
        }
    }

    /// <summary>Opens the job's document in the index worker, or says why the stage can't run.</summary>
    public static async Task<(PdfSession? Session, StageOutcome? Outcome)> OpenPdfAsync(StageServices s, JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null) return (null, new StageOutcome.Blocked(StageOutcome.Blocked.Unreachable));
        var (file, failed) = await OpenFileAsync(s, source, ct);
        if (file is null) return (null, failed);

        var (session, opened) = await PdfSession.OpenAsync(s.Pdf, file, s.Passwords.Find(job.ContentHash), ct);
        return session is null ? (null, OpenFailure(opened.Error, opened.Message)) : (session, null);
    }

    /// <summary>A page drawn at cover size, as a JPEG, or null when it couldn't be drawn.</summary>
    public static async Task<byte[]?> DrawCoverAsync(StageServices s, PdfSession session, int page, CancellationToken ct)
    {
        var size = session.Doc.PageSizes[page];
        var scale = Math.Min(4.0, CoverCache.Width / Math.Max(1, size.Width));
        var drawn = await session.Document.RenderAsync(page, scale, ct);
        return drawn.Value is { } image ? s.Images.EncodeJpeg(image.Pixels, image.Width, image.Height, image.Stride) : null;
    }

    /// <summary>
    /// The covers of adventures inside a file: each of <paramref name="pages"/> drawn and kept as that page's cover.
    /// The first page is the file's own cover, which they share.
    /// </summary>
    public static async Task DrawPartCoversAsync(StageServices s, PdfSession session, long documentId, string contentHash, IReadOnlyCollection<int> pages,
        CancellationToken ct)
    {
        foreach (var page in pages.Distinct().Where(p => p > 0 && p < session.Doc.PageCount))
            if (await DrawCoverAsync(s, session, page, ct) is { } jpeg)
                await s.Index.SetPageCoverAsync(documentId, page, s.Covers.Write(CoverCache.PageKey(contentHash, page), jpeg), ct);
    }

    public static StageOutcome OpenFailure(ErrorKind error, string? message) => error switch
    {
        ErrorKind.Password => new StageOutcome.Blocked(TextAccessReasons.Locked),
        ErrorKind.Format => new StageOutcome.Failed("This file isn't a readable PDF; it may be damaged or incomplete.", Retry: false),
        ErrorKind.Security => new StageOutcome.Failed(TextAccessReasons.Protected, Retry: false),
        ErrorKind.File => new StageOutcome.Failed("The file could not be read. It may be in use, or still downloading.", Retry: true),
        _ => new StageOutcome.Failed($"The PDF engine could not open the file ({message}).", Retry: true),
    };
}

/// <summary>Probe: page count, sizes, labels, protection, document information and outline.</summary>
public sealed class ProbeStage(StageServices s) : IStage
{
    public Stage Stage => Stage.Probe;

    public Lane Lane => Lane.Index;

    public Stage? After => null;

    public bool Backfill => false;

    public StageNames Names => new("Opening", "Opening", "Opening");

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null)
        {
            await RecordUnreachableAsync(job, ct);
            return new StageOutcome.Blocked(StageOutcome.Blocked.Unreachable);
        }
        var title = DisplayTitle.FromFileName(source.FullPath);

        if (SourceFormats.IsImage(source.Format))
        {
            (int Width, int Height)? size;
            var (file, unreadable) = await StageHelpers.OpenFileAsync(s, source, ct);
            if (file is null) return unreadable!;
            await using (file)
            await using (var stream = s.Reader.OpenRead(file.Path)) size = s.Images.ReadSize(stream);
            if (size is null) return new StageOutcome.Failed(Unreadable(source.Format), Retry: false);
            await StageHelpers.UpsertDocumentAsync(s, new DocRow
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
            return StageOutcome.Complete();
        }

        // The document is in the library from here, by its file name, even if it turns out not to open.
        var basic = new DocRow { DocumentId = job.DocumentId, ContentHash = job.ContentHash, Format = source.Format, DisplayTitle = title, FolderHint = source.FolderHint };
        if (EbookFiles.Is(source)) return await ProbeEbookAsync(job, source, basic, ct);
        var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
        if (session is null)
        {
            // Only a document new to the index: one already there keeps its pages and their text, so a file that won't
            // open this time (in use, say) never makes a reprocessed book unsearchable.
            if (await s.Queries.GetTitleAsync(job.DocumentId, ct) is null) await StageHelpers.UpsertDocumentAsync(s, basic, [], [], ct);
            return failed!;
        }
        await using (session)
        {
            var doc = session.Doc;
            await StageHelpers.UpsertDocumentAsync(s, basic with
            {
                PageCount = doc.PageCount,
                Encrypted = doc.IsEncrypted,
                CanCopy = doc.CanCopy,
                MetaTitle = doc.Metadata.Title,
                MetaAuthor = doc.Metadata.Author,
                MetaSubject = doc.Metadata.Subject,
                MetaKeywords = doc.Metadata.Keywords,
                // Kept for the store it may name; one that reads like a watermark (a buyer, an order number) isn't kept.
                MetaProducer = doc.Metadata.Producer is { } producer && !Watermarks.ReadsLikeOne(producer) ? producer : null,
            },
            [.. doc.PageSizes.Select((size, i) => new PageRow(i, doc.PageLabels.ElementAtOrDefault(i), size.Width, size.Height))],
            [.. doc.Outline.Select(o => new OutlineRow(o.Title, o.PageIndex, o.Depth))], ct);

            var protection = !doc.IsEncrypted ? ProtectionType.None
                : s.Passwords.Find(job.ContentHash) is not null ? ProtectionType.OpenPassword
                : ProtectionType.PermissionsOnly;
            var capabilities = JsonSerializer.Serialize(new { canCopy = doc.CanCopy, canPrint = doc.CanPrint, encrypted = doc.IsEncrypted });
            await s.Library.SetProbeResultAsync(job.DocumentId, doc.PageCount, protection, capabilities, ct);
        }
        return StageOutcome.Complete();
    }

    /// <summary>
    /// An e-book's package and pages. A book whose text is protected by DRM still has its package, so it is listed with
    /// the title, authors and publisher it gives, and its Text stage says why it can't be read.
    /// </summary>
    async Task<StageOutcome> ProbeEbookAsync(JobRecord job, DocumentSource source, DocRow basic, CancellationToken ct)
    {
        var (read, failed) = await EbookFiles.ReadAsync(s, source,
            (book, token) => (book.Metadata, book.IsProtected, book.FixedLayout, Layout: book.IsProtected ? null : EbookPaging.Cut(book, token)), ct);
        if (failed is not null)
        {
            if (await s.Queries.GetTitleAsync(job.DocumentId, ct) is null) await StageHelpers.UpsertDocumentAsync(s, basic, [], [], ct);
            return failed;
        }
        var layout = read.Layout;
        await StageHelpers.UpsertDocumentAsync(s, EbookFiles.WithMetadata(basic with { PageCount = layout?.Pages.Count, Encrypted = read.IsProtected }, read.Metadata),
            layout is null ? [] : EbookFiles.Pages(layout), layout is null ? [] : EbookFiles.Outline(layout), ct);
        var capabilities = JsonSerializer.Serialize(new
        {
            canCopy = true, canPrint = true, encrypted = read.IsProtected, fixedLayout = read.FixedLayout, printPages = layout?.PrintPages == true,
            paging = EbookPaging.Version,
        });
        await s.Library.SetProbeResultAsync(job.DocumentId, layout?.Pages.Count, read.IsProtected ? ProtectionType.Unsupported : ProtectionType.None, capabilities, ct);
        return StageOutcome.Complete();
    }

    /// <summary>
    /// A document new to the index whose files can't be read now, as when index.db is rebuilt after a restore while a
    /// library folder is offline: recorded by its file name, so its card stays in the library, marked offline, until
    /// its folder is back and it is read.
    /// </summary>
    async Task RecordUnreachableAsync(JobRecord job, CancellationToken ct)
    {
        if (await s.Queries.GetTitleAsync(job.DocumentId, ct) is not null) return;
        if (await s.Library.GetLocationsAsync(job.DocumentId, ct) is not [var location, ..]
            || SourceFormats.FromFileName(location.FullPath) is not { } format) return;
        await StageHelpers.UpsertDocumentAsync(s, new DocRow
        {
            DocumentId = job.DocumentId,
            ContentHash = job.ContentHash,
            Format = format,
            DisplayTitle = DisplayTitle.FromFileName(location.FullPath),
        }, [], [], ct);
    }

    /// <summary>
    /// Why an image couldn't be read. Windows reads WebP only with Microsoft's free WebP Image Extensions, which most
    /// installs have; without them every WebP lands here, so the reason says what to install.
    /// </summary>
    internal static string Unreadable(string format) => format == SourceFormats.Webp
        ? "Windows couldn't read this WebP image. If Microsoft's free WebP Image Extensions aren't installed, get them from the Microsoft Store, then retry."
        : "This image could not be read; it may be damaged.";
}

/// <summary>
/// Text: every page's text in runs of 32 pages, scored for quality. Low-quality pages are flagged for OCR but stay
/// searchable with what they have. A page that crashes the PDF engine is recorded and skipped, not retried forever.
/// </summary>
public sealed class TextStage(StageServices s) : IStage
{
    public const int RunLength = 32;

    public Stage Stage => Stage.Text;

    public Lane Lane => Lane.Index;

    public Stage? After => Stage.Probe;

    public bool Backfill => false;

    public StageNames Names => new("Reading text", "Reading text", "Reading the text of");

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null) return new StageOutcome.Blocked(StageOutcome.Blocked.Unreachable);
        if (SourceFormats.IsImage(source.Format)) return new StageOutcome.Done(StageStatus.Skipped, [], "Images have no text layer.");
        if (EbookFiles.Is(source)) return await ReadEbookAsync(job, source, ct);

        var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
        if (session is null) return failed!;
        await using (session)
        {
            var failedPages = 0;
            for (var first = 0; first < session.Doc.PageCount; first += RunLength)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(RunLength, session.Doc.PageCount - first);
                var pages = await ExtractRunAsync(session, first, count, ct) ?? await ExtractOneByOneAsync(session, first, count, ct);
                var rows = pages.Select(Score).ToList();
                failedPages += rows.Count(r => r.Error is not null);
                await s.Index.SetPageTextAsync(job.DocumentId, rows, ct);
            }

            // Read back rather than counted from the scores: a page flagged for OCR before Text ran (Reprocess with OCR
            // on every page) keeps its flag whatever its text layer scores. The stages after Text wait for OCR by
            // themselves, so they are queued now either way.
            Stage[] next = (await s.Queries.GetPagesNeedingOcrAsync(job.DocumentId, ct)).Count > 0 ? [Stage.Ocr] : [];
            return failedPages == 0
                ? new StageOutcome.Done(StageStatus.Complete, next)
                : new StageOutcome.Done(StageStatus.Partial, next, $"{failedPages} page(s) could not be read.");
        }
    }

    /// <summary>An e-book's pages, cut again as Probe cut them: the same file always gives the same pages.</summary>
    async Task<StageOutcome> ReadEbookAsync(JobRecord job, DocumentSource source, CancellationToken ct)
    {
        var (layout, failed) = await EbookFiles.ReadAsync(s, source, (book, token) => book.IsProtected ? null : EbookPaging.Cut(book, token), ct);
        if (failed is not null) return failed;
        if (layout is null) return new StageOutcome.Failed(TextAccessReasons.ProtectedEbook, Retry: false);
        await s.Index.SetPageTextAsync(job.DocumentId, EbookFiles.Text(layout), ct);
        return StageOutcome.Complete();
    }

    static PageTextRow Score(PageText page)
    {
        if (page.Error is not null) return new PageTextRow(page.PageIndex, "", "none", 0, NeedsOcr: false, page.Error);
        var quality = TextQuality.Score(page.Text, page.UnmappedChars);
        return new PageTextRow(page.PageIndex, page.Text, page.CharCount == 0 ? "none" : "pdf", quality, TextQuality.NeedsOcr(quality));
    }

    /// <summary>The whole run in one request, or null if the worker died on it.</summary>
    internal static async Task<List<PageText>?> ExtractRunAsync(PdfSession session, int first, int count, CancellationToken ct)
    {
        try
        {
            var extracted = await session.Document.ExtractPagesAsync(first, count, ct);
            return extracted.Ok ? [.. extracted.Value!] : null;
        }
        catch (PdfEngineException)
        {
            return null;
        }
    }

    /// <summary>After a run fails, each page on its own, so one bad page costs only itself.</summary>
    internal static async Task<List<PageText>> ExtractOneByOneAsync(PdfSession session, int first, int count, CancellationToken ct)
    {
        var pages = new List<PageText>(count);
        for (var p = first; p < first + count; p++)
        {
            try
            {
                var extracted = await session.Document.ExtractPagesAsync(p, 1, ct);
                pages.Add(extracted.Ok ? extracted.Value![0] : new PageText(p, "", 0, 0, extracted.Message ?? "The page could not be read."));
            }
            catch (PdfEngineException)
            {
                pages.Add(new PageText(p, "", 0, 0, "This page crashed the PDF engine."));
            }
        }
        return pages;
    }
}

/// <summary>
/// Covers: the first page, or the page the user chose, or the image itself, or an e-book's cover image, as a JPEG in the
/// cover cache. A missing cover is not an error.
/// </summary>
public sealed class CoversStage(StageServices s) : IStage
{
    public Stage Stage => Stage.Covers;

    public Lane Lane => Lane.Index;

    public Stage? After => Stage.Probe;

    public bool Backfill => false;

    public StageNames Names => new("Cover", "Making a cover", "Making a cover for");

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var source = await s.Library.GetSourceAsync(job.DocumentId, ct);
        if (source is null) return new StageOutcome.Blocked(StageOutcome.Blocked.Unreachable);

        byte[]? jpeg;
        var key = job.ContentHash;
        if (SourceFormats.IsImage(source.Format))
        {
            var (file, unreadable) = await StageHelpers.OpenFileAsync(s, source, ct);
            if (file is null) return unreadable!;
            await using (file)
            await using (var stream = s.Reader.OpenRead(file.Path)) jpeg = s.Images.Thumbnail(stream, CoverCache.Width);
        }
        else if (EbookFiles.Is(source))
        {
            var (image, failed) = await EbookFiles.ReadAsync(s, source, (book, _) => book.ReadCover(), ct);
            if (failed is not null) return failed;
            if (image is null) return new StageOutcome.Done(StageStatus.Skipped, [], "The e-book has no cover image.");
            using var stream = new MemoryStream(image, writable: false);
            jpeg = s.Images.Thumbnail(stream, CoverCache.Width);
        }
        else
        {
            var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
            if (session is null) return failed!;
            await using (session)
            {
                if (session.Doc.PageCount == 0) return new StageOutcome.Done(StageStatus.Skipped, [], "The PDF has no pages.");
                // A chosen page is kept under its own name, so a card already showing the first page loads the new one.
                var page = await s.Library.GetCoverPageAsync(job.DocumentId, ct) is var chosen && chosen < session.Doc.PageCount ? chosen : 0;
                if (page > 0) key = CoverCache.PageKey(job.ContentHash, page);
                jpeg = await StageHelpers.DrawCoverAsync(s, session, page, ct);
                // The adventures marked in the file have their first pages as covers.
                await StageHelpers.DrawPartCoversAsync(s, session, job.DocumentId, job.ContentHash,
                    [.. (await s.Parts.GetPagesInAsync(job.DocumentId, ct)).Select(p => p.Pages.First)], ct);
            }
        }

        if (jpeg is null) return new StageOutcome.Done(StageStatus.Skipped, [], "No cover could be drawn.");
        await s.Index.SetCoverAsync(job.DocumentId, s.Covers.Write(key, jpeg), ct);
        return StageOutcome.Complete();
    }
}

/// <summary>
/// OCR: each flagged page (those Text scored too poor to trust, or every page when the user asked for that on
/// Reprocess), rendered at 300 dpi and read by the worker's OCR engine. Progress is saved page by
/// page, so a paused or interrupted job carries on where it stopped. Page numbering never changes. A file another of
/// the user's devices has OCR'd already takes that text for the pages never read here (<see cref="SharedPageText"/>),
/// and what is read here is offered to the others.
/// </summary>
public sealed class OcrStage(StageServices s, SharedPageText? pageText = null, SharedResults? shared = null) : IStage
{
    public const double Dpi = 300;

    public Stage Stage => Stage.Ocr;

    /// <summary>Its own lane: slow, so it never holds up the next book's text.</summary>
    public Lane Lane => Lane.Ocr;

    /// <summary>None: Text asks for it only when pages need it.</summary>
    public Stage? After => null;

    public bool Backfill => false;

    public StageNames Names => new("Reading scanned pages", "Reading scanned pages", "Reading scanned pages of");

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var pages = await s.Queries.GetPagesNeedingOcrAsync(job.DocumentId, ct);
        if (pages.Count == 0) return StageOutcome.Complete();
        if (pageText is not null && await pageText.TryTakeAsync(job.DocumentId, job.ContentHash, ct) > 0)
        {
            pages = await s.Queries.GetPagesNeedingOcrAsync(job.DocumentId, ct);
            if (pages.Count == 0) return StageOutcome.Complete();
        }

        var (session, failed) = await StageHelpers.OpenPdfAsync(s, job, ct);
        if (session is null) return failed!;
        var read = 0;
        try
        {
            await using (session)
            {
                var failedPages = 0;
                foreach (var page in pages)
                {
                    ct.ThrowIfCancellationRequested();
                    PdfResult<OcrInfo> response;
                    try
                    {
                        response = await session.Document.OcrAsync(page.PdfPage, Dpi / 72, ct);
                    }
                    catch (PdfEngineException)
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

                    var ocr = response.Value!;
                    await s.Index.SetOcrPageAsync(job.DocumentId, page.PdfPage, ocr.Text, TextQuality.Score(ocr.Text, 0),
                        [.. ocr.Words.Select(w => new OcrWordRow(w.Text, w.Box.Left, w.Box.Top, w.Box.Right, w.Box.Bottom))], ct);
                    read++;
                }
                return failedPages == 0
                    ? StageOutcome.Complete()
                    : new StageOutcome.Done(StageStatus.Partial, [], $"OCR failed on {failedPages} of {pages.Count} page(s).");
            }
        }
        finally
        {
            // What was read is offered even when the stage stops part way, as when the app closes.
            if (read > 0 && pageText is not null)
            {
                await pageText.OfferAsync(job.DocumentId, job.ContentHash, CancellationToken.None);
                shared?.Nudge();
            }
        }
    }
}
