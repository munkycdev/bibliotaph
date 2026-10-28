using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What the web library read from a file, for a connected PC that has the file only there: what Probe, Text and Covers
/// wrote, so the PC shows the book, searches it and lets its own AI read it without downloading it. The PC writes it as
/// those stages would have. <see cref="TextStatus"/> is how Text ended there (Complete, Partial, or Skipped for an
/// image, with <see cref="TextReason"/>), and <see cref="Cover"/> names the cover the server drew, fetched on its own.
/// </summary>
public sealed record SharedReading(
    string Format, string DisplayTitle, int? PageCount, int? WidthPx, int? HeightPx, bool Encrypted, bool CanCopy,
    string? MetaTitle, string? MetaAuthor, string? MetaSubject, string? MetaKeywords, string? MetaProducer,
    string Protection, string? Capabilities, string? Cover,
    IReadOnlyList<StoredPage> Pages, IReadOnlyList<OutlineRow> Outline, string TextStatus, string? TextReason);

/// <summary>
/// What the web library says of a file: its <see cref="Reading"/> once it has one; <see cref="ReadHere"/> when it
/// couldn't read the file itself (a password it hasn't been given, a file its PDF engine can't open), so a PC reads it
/// as it reads its own files; neither while it hasn't read it yet.
/// </summary>
public sealed record WebReading(SharedReading? Reading, bool ReadHere)
{
    public static readonly WebReading NotYet = new(null, false);
    public static readonly WebReading Unreadable = new(null, true);
}

/// <summary>Reads a file's <see cref="SharedReading"/> out of this library, once Probe and Text have both finished with it.</summary>
public sealed class SharedReadings(LibraryStore library, IndexQueries queries)
{
    public async Task<WebReading> ReadAsync(string contentHash, CancellationToken ct = default)
    {
        if (!ContentHash.TryParse(contentHash, out var hash) || await library.FindDocumentAsync(hash, ct) is not { } documentId) return WebReading.NotYet;
        var probe = await queries.GetStageAsync(documentId, Stage.Probe, ct);
        if (probe?.Status is StageStatus.Failed or StageStatus.Blocked) return WebReading.Unreadable;
        if (probe is not { Status: StageStatus.Complete }) return WebReading.NotYet;
        var text = await queries.GetStageAsync(documentId, Stage.Text, ct);
        if (text?.Status == StageStatus.Failed) return WebReading.Unreadable;
        if (text is not { Status: StageStatus.Complete or StageStatus.Partial or StageStatus.Skipped } finished) return WebReading.NotYet;
        if (await queries.GetReadingAsync(documentId, ct) is not { } reading || await library.GetProbeResultAsync(documentId, ct) is not { } result)
            return WebReading.NotYet;
        var doc = reading.Doc;
        return new WebReading(new SharedReading(doc.Format, doc.DisplayTitle, doc.PageCount, doc.WidthPx, doc.HeightPx, doc.Encrypted, doc.CanCopy,
            doc.MetaTitle, doc.MetaAuthor, doc.MetaSubject, doc.MetaKeywords, doc.MetaProducer,
            result.Protection.ToString(), result.Capabilities, reading.Cover, reading.Pages, reading.Outline, finished.Status.ToString(), finished.Reason), false);
    }
}
