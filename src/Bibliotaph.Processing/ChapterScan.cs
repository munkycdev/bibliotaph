using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Copies whole-book scans from catalog.db into index.db, where the inspector, the reader and search read them. A scan
/// is kept in catalog.db by its content hash, so a rebuilt index.db gets every chapter back without the AI reading
/// anything again.
/// </summary>
public sealed class ChapterProjection(ChapterScanStore store, IndexStore index, ChapterQueries chapters, LibraryStore library,
    ILogger<ChapterProjection>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<ChapterProjection>.Instance;

    /// <summary>Writes one document's chapters as they stand, after each batch the AI reads.</summary>
    public Task ProjectAsync(long documentId, IReadOnlyList<ScannedChapter> scanned, CancellationToken ct = default) =>
        index.SetChaptersAsync([(documentId, scanned)], ct);

    /// <summary>Takes a document's chapters out of index.db, as when it is to be scanned again.</summary>
    public Task ClearAsync(long documentId, CancellationToken ct = default) => index.SetChaptersAsync([(documentId, [])], ct);

    /// <summary>
    /// Brings index.db up to date with catalog.db at startup: the scans of documents index.db has no chapters for, as
    /// after a rebuild, are written, and chapters whose scan has gone are taken away. Returns how many documents changed.
    /// </summary>
    public async Task<int> RestoreAsync(CancellationToken ct = default)
    {
        var scans = (await store.ListAsync(ct)).Where(s => s.Chapters.Count > 0).ToDictionary(s => s.ContentHash, StringComparer.Ordinal);
        var present = await chapters.GetScannedDocumentsAsync(ct);
        var documents = await library.GetDocumentHashesAsync(ct);
        var scanned = documents.Where(d => scans.ContainsKey(d.ContentHash)).ToList();
        var stale = present.Except(scanned.Select(d => d.DocumentId)).ToList();
        List<(long, IReadOnlyList<ScannedChapter>)> writes =
        [
            .. scanned.Where(d => !present.Contains(d.DocumentId)).Select(d => (d.DocumentId, scans[d.ContentHash].Chapters)),
            .. stale.Select(id => (id, (IReadOnlyList<ScannedChapter>)[])),
        ];
        foreach (var chunk in writes.Chunk(100)) await index.SetChaptersAsync(chunk, ct);
        if (writes.Count > 0) _log.LogInformation("Restored the chapters of {Count} scanned books in index.db", writes.Count);
        return writes.Count;
    }
}

/// <summary>
/// Reads a whole book with the AI, chapter by chapter, and tags each chapter with its setting, environments, themes and
/// creatures, each with the page and the words that show it. It runs in the AI's lane after everything else, at the
/// lowest priority, only once the book's own cataloging is done, and never while a person is waiting on the AI
/// (<see cref="ModelActivity"/>). Book-level values are never touched.
/// </summary>
/// <remarks>
/// One batch of chapters, about 20 to 30 pages, is one request, and each job reads one batch and goes back to the
/// queue, so a book waiting to be catalogued always goes first. Progress is saved in catalog.db after every batch, so a
/// scan carries on where it stopped after a restart. A batch the model can't answer is marked as such and passed over:
/// a scan never fails a book or asks the user for anything.
/// </remarks>
public sealed class ChapterScanStage : IStage, IDisposable
{
    /// <summary>How long a book whose own cataloging or scanned pages are still waiting waits before looking again; it looks sooner when they finish.</summary>
    public static readonly TimeSpan BookWait = TimeSpan.FromMinutes(5);

    public const string Off = "Scanning whole books is switched off.";
    public const string Paused = "Waiting while you use the AI.";

    readonly AiSettings _ai;
    readonly LibraryStore _library;
    readonly IndexQueries _queries;
    readonly ChapterScanStore _store;
    readonly ChapterProjection _projection;
    readonly VocabularyStore _vocabularies;
    readonly TimeProvider _clock;
    readonly ILogger _log;
    readonly Lock _lock = new();
    CancellationTokenSource _stop = new();

    public ChapterScanStage(AiSettings ai, LibraryStore library, IndexQueries queries, ChapterScanStore store, ChapterProjection projection,
        VocabularyStore vocabularies, TimeProvider? clock = null, ILogger<ChapterScanStage>? log = null)
    {
        (_ai, _library, _queries, _store, _projection, _vocabularies) = (ai, library, queries, store, projection, vocabularies);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<ChapterScanStage>.Instance;
        // Switched off: the request under way stops now, rather than after a chapter nobody wants any more.
        ai.ScanWholeBooksChanged += (_, _) =>
        {
            if (ai.ScanWholeBooks) return;
            CancellationTokenSource stopped;
            lock (_lock) (stopped, _stop) = (_stop, new CancellationTokenSource());
            stopped.Cancel();
        };
    }

    public Stage Stage => Stage.ChapterScan;

    public void Dispose()
    {
        lock (_lock) _stop.Dispose();
    }

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        var ai = _ai.Current;
        if (!ai.Setup.IsReady || ai.Background is not { } client || ai.Setup.ModelName is not { } model)
            return new StageOutcome.Unavailable("AI is switched off.");
        if (!_ai.ScanWholeBooks) return new StageOutcome.Done(StageStatus.Skipped, [], Off);
        var provider = ai.Setup.RunProvider;
        if (ai.Setup.SkippedRoots.Count > 0 && (await _library.GetRootIdsAsync(job.DocumentId, ct)).Any(ai.Setup.SkippedRoots.Contains))
            return new StageOutcome.Done(StageStatus.Skipped, [], "Its library folder isn't sent to AI.");

        var scan = await _store.GetAsync(job.ContentHash, ct);
        // Read before, by this copy or another with the same content, or before index.db was built again.
        if (scan is { IsFinished: true, Chapters.Count: > 0 })
        {
            await _projection.ProjectAsync(job.DocumentId, scan.Chapters, ct);
            return StageOutcome.Complete();
        }
        if (await _queries.GetStageStatusAsync(job.DocumentId, Stage.Ocr, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(BookWait, "Waiting for its scanned pages to be read.");
        if (await _queries.GetStageStatusAsync(job.DocumentId, Stage.Classify, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(BookWait, "Waiting for the book to be catalogued first.");
        if (!_ai.Activity.TryQuiet(out var yield, out var wait)) return new StageOutcome.Later(wait, Paused);

        var texts = await _queries.GetPageTextsAsync(job.DocumentId, ct);
        if (texts.Count == 0) return new StageOutcome.Done(StageStatus.Skipped, [], "There is no text to read.");
        var pageCount = await _queries.GetPageCountAsync(job.DocumentId, ct) ?? texts[^1].PdfPage + 1;
        var chapters = scan?.Chapters is { Count: > 0 } saved ? [.. saved] : await SplitAsync(job.DocumentId, texts, pageCount, ct);
        var started = scan?.StartedUtc ?? _clock.GetUtcNow().UtcDateTime;

        var unmarked = Watermarks.Strip([.. texts.Select(t => t.Text)]);
        var clean = texts.Select((t, i) => new BookPage(t.PdfPage, unmarked[i])).ToDictionary(p => p.PdfPage);
        var raw = texts.ToDictionary(t => t.PdfPage, t => t.Text);
        var batches = BookChapters.Batches(chapters, page => clean.TryGetValue(page, out var p) ? p.Text.Length : 0);
        if (batches.Count == 0) return await FinishAsync(job, provider, model, chapters, started);

        var batch = batches[0];
        var reading = batch.Select(i => chapters[i]).ToList();
        var pages = BookChapters.Fit([.. clean.Values.Where(p => reading.Any(c => c.Pages.Contains(p.PdfPage))).OrderBy(p => p.PdfPage)], ChapterPrompt.Budget);
        var title = await _queries.GetTitleAsync(job.DocumentId, ct) ?? "";
        CancellationToken stop;
        lock (_lock) stop = _stop.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, yield, stop);
        try
        {
            var result = await new ChapterReader(client, model).ReadAsync(new ChapterBatch(title, reading, pages), raw.GetValueOrDefault,
                await _vocabularies.GetAsync(ct), linked.Token);
            for (var i = 0; i < batch.Count; i++) chapters[batch[i]] = chapters[batch[i]] with { Reading = ChapterReading.Read, Tags = result.Tags[i] };
            _log.LogInformation("Scanned chapters {First}–{Last} of {Count} in document {DocumentId} with {Model}: {Kept} values kept, {Dropped} dropped",
                batch[0] + 1, batch[^1] + 1, chapters.Count, job.DocumentId, model, result.Tags.Sum(t => t.Count), result.Dropped);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not this book's problem: the user asked the AI something, or switched scanning off. Nothing is lost.
            return stop.IsCancellationRequested ? new StageOutcome.Done(StageStatus.Skipped, [], Off) : new StageOutcome.Later(_ai.Activity.QuietPeriod, Paused);
        }
        catch (ModelEndpointException ex)
        {
            return new StageOutcome.Unavailable(ex.Message);
        }
        catch (ClassifierAnswerException ex)
        {
            foreach (var i in batch) chapters[i] = chapters[i] with { Reading = ChapterReading.Failed };
            _log.LogInformation("Couldn't scan chapters {First}–{Last} of document {DocumentId} with {Model}: {Problem}",
                batch[0] + 1, batch[^1] + 1, job.DocumentId, model, ex.Message);
        }

        if (batches.Count == 1) return await FinishAsync(job, provider, model, chapters, started);
        await _store.SaveAsync(job.ContentHash, provider, model, ChapterPrompt.Version, chapters, started, finished: false, CancellationToken.None);
        await _projection.ProjectAsync(job.DocumentId, chapters, CancellationToken.None);
        // Back to the queue between batches, so a book waiting to be catalogued, or the user, goes first.
        var done = chapters.Count(c => c.Reading != ChapterReading.Waiting);
        return new StageOutcome.Later(TimeSpan.Zero, $"Read {done} of {chapters.Count} chapters.");
    }

    /// <summary>Saves the scan as finished: it isn't read again while the file stays the same.</summary>
    async Task<StageOutcome> FinishAsync(JobRecord job, string provider, string model, List<ScannedChapter> chapters, DateTime started)
    {
        await _store.SaveAsync(job.ContentHash, provider, model, ChapterPrompt.Version, chapters, started, finished: true, CancellationToken.None);
        await _projection.ProjectAsync(job.DocumentId, chapters, CancellationToken.None);
        var failed = chapters.Count(c => c.Reading == ChapterReading.Failed);
        return failed == 0
            ? StageOutcome.Complete()
            : new StageOutcome.Done(StageStatus.Partial, [], $"The AI couldn't read {failed} of {chapters.Count} chapters.");
    }

    /// <summary>The book's chapters, from its bookmarks, its contents page or runs of pages.</summary>
    async Task<List<ScannedChapter>> SplitAsync(long documentId, IReadOnlyList<PageTextEntry> texts, int pageCount, CancellationToken ct)
    {
        var outline = await _queries.GetOutlineAsync(documentId, ct);
        var labelled = PrintedPages.FromLabels(await _queries.GetPageLabelsAsync(documentId, ct), pageCount);
        var printed = labelled.IsKnown ? labelled : PrintedPages.FromFolios(texts.Select(t => (t.PdfPage, t.Text)), pageCount);
        return [.. BookChapters.Split(pageCount, [.. outline.Select(o => new BookHeading(o.Title, o.PdfPage, o.Depth))],
            [.. texts.Select(t => new BookPage(t.PdfPage, t.Text))], printed)];
    }
}

/// <summary>
/// What Settings > AI, the inspector and the reader do with whole-book scans: switch them on or off, read a book's
/// chapters as found, and scan a book again.
/// </summary>
public sealed class ChapterScanService(
    AiSettings ai, IJobControl jobs, IIndexingStatus indexing, ChapterScanStore store, ChapterProjection projection, ChapterQueries chapters)
{
    /// <summary>Whether books are read whole once they are catalogued.</summary>
    public bool IsOn => ai.ScanWholeBooks;

    /// <summary>
    /// Switches reading whole books on or off. Switched on, every book passed over while it was off goes back to the
    /// queue; those already read are only copied into the index again.
    /// </summary>
    public async Task SetOnAsync(bool on, CancellationToken ct = default)
    {
        var was = ai.ScanWholeBooks;
        await ai.SetScanWholeBooksAsync(on, ct);
        if (on && !was) await jobs.RerunAsync(Stage.ChapterScan, ct);
    }

    /// <summary>
    /// A document's chapters as the AI found them, in page order; for an adventure inside it (<paramref name="pages"/>),
    /// only the chapters that overlap its pages, with the values found on them.
    /// </summary>
    public async Task<IReadOnlyList<ScannedChapter>> GetChaptersAsync(long documentId, PartPages? pages = null, CancellationToken ct = default)
    {
        var all = await chapters.GetChaptersAsync(documentId, ct);
        if (pages is not { } part) return all;
        return [.. all.Where(c => c.Pages.Overlaps(part)).Select(c => c with { Tags = [.. c.Tags.Where(t => part.Contains(t.PdfPage))] })];
    }

    /// <summary>
    /// Forgets what the AI found in a document and reads it again from the start, when AI is on and scanning whole
    /// books is too. A scan under way for it stops first.
    /// </summary>
    public async Task RescanAsync(long documentId, CancellationToken ct = default)
    {
        if (indexing.Running(Lane.Classify) is { Stage: Stage.ChapterScan } running && running.DocumentId == documentId) jobs.Interrupt([documentId]);
        await store.ForgetDocumentAsync(documentId, ct);
        await projection.ClearAsync(documentId, ct);
        await jobs.RerunAsync(Stage.ChapterScan, documentId, ct);
    }
}
