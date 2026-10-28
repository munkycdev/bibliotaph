using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What the web library read from a file, for a connected PC that has the file only there: what Probe, Text and Covers
/// wrote, so the PC shows the book, searches it and lets its own AI read it without downloading it. The PC writes it as
/// those stages would have. <see cref="TextStatus"/> is how Text ended there (Complete, Partial, Skipped for an image,
/// Failed with <see cref="TextReason"/>), and <see cref="Cover"/> names the cover the server drew, fetched on its own.
/// </summary>
public sealed record SharedReading(
    string Format, string DisplayTitle, int? PageCount, int? WidthPx, int? HeightPx, bool Encrypted, bool CanCopy,
    string? MetaTitle, string? MetaAuthor, string? MetaSubject, string? MetaKeywords, string? MetaProducer,
    string Protection, string? Capabilities, string? Cover,
    IReadOnlyList<StoredPage> Pages, IReadOnlyList<OutlineRow> Outline, string TextStatus, string? TextReason);

/// <summary>Reads a file's <see cref="SharedReading"/> out of this library, once Probe and Text have both finished with it.</summary>
public sealed class SharedReadings(LibraryStore library, IndexQueries queries)
{
    /// <summary>The reading of the file with this content, or null when it isn't in the library or hasn't been read yet.</summary>
    public async Task<SharedReading?> ReadAsync(string contentHash, CancellationToken ct = default)
    {
        if (!ContentHash.TryParse(contentHash, out var hash) || await library.FindDocumentAsync(hash, ct) is not { } documentId) return null;
        if (await queries.GetStageAsync(documentId, Stage.Probe, ct) is not { Status: StageStatus.Complete }) return null;
        if (await queries.GetStageAsync(documentId, Stage.Text, ct) is not { } text || !Finished(text.Status)) return null;
        if (await queries.GetReadingAsync(documentId, ct) is not { } reading || await library.GetProbeResultAsync(documentId, ct) is not { } probe) return null;
        var doc = reading.Doc;
        return new SharedReading(doc.Format, doc.DisplayTitle, doc.PageCount, doc.WidthPx, doc.HeightPx, doc.Encrypted, doc.CanCopy,
            doc.MetaTitle, doc.MetaAuthor, doc.MetaSubject, doc.MetaKeywords, doc.MetaProducer,
            probe.Protection.ToString(), probe.Capabilities, reading.Cover, reading.Pages, reading.Outline, text.Status.ToString(), text.Reason);
    }

    static bool Finished(StageStatus status) => status is StageStatus.Complete or StageStatus.Partial or StageStatus.Skipped or StageStatus.Failed;
}
