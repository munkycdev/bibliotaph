using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// For a book only the web library has: Probe, Text and Covers write what the server read from it
/// (<see cref="SharedReading"/>) and the cover it drew, instead of reading the file, so nothing is downloaded to put a
/// book in the library, make it searchable or let the AI here read it. A book the server hasn't read yet waits.
/// OCR and the covers of adventures inside a book read the file, which the web library downloads.
/// </summary>
public sealed class WebReadings(IWebBooks web, TimeSpan? wait = null)
{
    /// <summary>How long a book waits for the server to read it before asking again.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    readonly TimeSpan _wait = wait ?? Wait;

    public const string Waiting = "Waiting for bibliotaph.online to read this book.";

    /// <summary>Probe, Text and Covers each take the reading in turn, so the last few are kept rather than asked for again.</summary>
    const int Kept = 8;

    readonly Lock _lock = new();
    readonly LinkedList<(string Hash, SharedReading Reading)> _recent = [];

    /// <summary>Whether a stage reads this source through the web library: its only readable copy is there.</summary>
    public static bool Serves(DocumentSource source) => !source.InArchive && WebLibraryPaths.IsWebPath(source.FullPath);

    public async Task<StageOutcome> ProbeAsync(StageServices s, JobRecord job, DocumentSource source, CancellationToken ct)
    {
        if (await ReadAsync(job.ContentHash, ct) is not { } reading) return Later();
        await StageHelpers.UpsertDocumentAsync(s, new DocRow
        {
            DocumentId = job.DocumentId,
            ContentHash = job.ContentHash,
            Format = source.Format,
            DisplayTitle = reading.DisplayTitle,
            PageCount = reading.PageCount,
            WidthPx = reading.WidthPx,
            HeightPx = reading.HeightPx,
            Encrypted = reading.Encrypted,
            CanCopy = reading.CanCopy,
            MetaTitle = reading.MetaTitle,
            MetaAuthor = reading.MetaAuthor,
            MetaSubject = reading.MetaSubject,
            MetaKeywords = reading.MetaKeywords,
            MetaProducer = reading.MetaProducer,
            FolderHint = source.FolderHint,
        },
        [.. reading.Pages.Select(p => new PageRow(p.Page, p.Label, p.Width, p.Height))], reading.Outline, ct);
        var protection = Enum.TryParse<ProtectionType>(reading.Protection, out var parsed) ? parsed : ProtectionType.None;
        await s.Library.SetProbeResultAsync(job.DocumentId, reading.PageCount, protection, reading.Capabilities ?? "{}", ct);
        return StageOutcome.Complete();
    }

    public async Task<StageOutcome> TextAsync(StageServices s, JobRecord job, CancellationToken ct)
    {
        if (await ReadAsync(job.ContentHash, ct) is not { } reading) return Later();
        if (!Enum.TryParse<StageStatus>(reading.TextStatus, out var status)) status = StageStatus.Complete;
        switch (status)
        {
            case StageStatus.Skipped:
                return new StageOutcome.Done(StageStatus.Skipped, [], reading.TextReason);
            case StageStatus.Failed:
                return new StageOutcome.Failed(reading.TextReason ?? "bibliotaph.online couldn't read this book's text.", Retry: false);
        }
        await s.Index.SetPageTextAsync(job.DocumentId,
            [.. reading.Pages.Select(p => new PageTextRow(p.Page, p.Text, p.Source, p.Quality ?? 0, p.NeedsOcr, p.Error))], ct);
        // As the Text stage asks for OCR: pages the server couldn't read well are read here.
        Stage[] next = (await s.Queries.GetPagesNeedingOcrAsync(job.DocumentId, ct)).Count > 0 ? [Stage.Ocr] : [];
        return status == StageStatus.Partial ? new StageOutcome.Done(StageStatus.Partial, next, reading.TextReason) : new StageOutcome.Done(StageStatus.Complete, next);
    }

    public async Task<StageOutcome> CoverAsync(StageServices s, JobRecord job, CancellationToken ct)
    {
        if (await ReadAsync(job.ContentHash, ct) is not { } reading) return Later();
        if (reading.Cover is not { } name) return new StageOutcome.Done(StageStatus.Skipped, [], "bibliotaph.online has no cover for this book.");
        if (await web.ReadCoverAsync(name, ct) is not { } jpeg) return Later();
        // Kept under the server's name, which says which page it is, as the cover drawn here would be.
        await s.Index.SetCoverAsync(job.DocumentId, s.Covers.Write(Path.GetFileNameWithoutExtension(name), jpeg), ct);
        return StageOutcome.Complete();
    }

    StageOutcome.Later Later() => new(_wait, Waiting);

    async Task<SharedReading?> ReadAsync(string hash, CancellationToken ct)
    {
        lock (_lock)
            if (_recent.FirstOrDefault(r => r.Hash == hash) is { Reading: not null } kept) return kept.Reading;
        if (await web.ReadReadingAsync(hash, ct) is not { } reading) return null;
        lock (_lock)
        {
            _recent.AddFirst((hash, reading));
            while (_recent.Count > Kept) _recent.RemoveLast();
        }
        return reading;
    }
}
