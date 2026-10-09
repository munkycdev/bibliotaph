using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Fingerprints a document's pages from the text already in index.db and, when another document holds the same book
/// page for page, joins the two as copies on one card (foundation slice F2, choices 1 to 3 of the F2 plan). Runs after
/// Text, and after OCR for a book with scanned pages, so it never reads the file.
/// </summary>
public sealed class MatchStage(EntryStore entries, IndexStore index, IndexQueries queries, MetadataProjector projector, ILogger<MatchStage>? log = null) : IStage
{
    readonly ILogger _log = log ?? NullLogger<MatchStage>.Instance;

    public Stage Stage => Stage.Match;

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (await queries.GetStageStatusAsync(job.DocumentId, Stage.Ocr, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(ClassifyStage.OcrWait, "Waiting for its scanned pages to be read.");

        var texts = await queries.GetAllPageTextsAsync(job.DocumentId, ct);
        var fingerprints = PageFingerprints.Compute(texts);
        await index.SetFingerprintsAsync(job.DocumentId, fingerprints, ct);
        if (fingerprints.Count(f => f is not null) < PageFingerprints.MinimumMatchingPages)
            return new StageOutcome.Done(StageStatus.Skipped, [], "Too little text to compare with other files.");

        foreach (var (other, shared) in await queries.GetSharingPagesAsync(job.DocumentId, ct))
        {
            if (shared < PageFingerprints.MinimumMatchingPages) break;
            if (!PageFingerprints.IsSameBook(fingerprints, await queries.GetFingerprintsAsync(other, ct))) continue;
            if (await entries.JoinAsCopyAsync(job.DocumentId, other, ct) is not { } join) continue;
            _log.LogInformation("Document {Document} is a copy of document {Other}; its card joined entry {Entry}", job.DocumentId, other, join.EntryId);
            await projector.ProjectAsync([join.EntryId, join.JoinedEntryId], ct);
            break;
        }
        return StageOutcome.Complete();
    }
}

/// <summary>A copy of a book with the places its file is, for the inspector's Copies list.</summary>
public sealed record CopyDetails(EntryCopy Copy, IReadOnlyList<DocumentLocation> Locations);

/// <summary>
/// The inspector's Copies list (F2 plan, choices 6, 8 and 10): which files hold an entry's book, which one opens, and
/// "Not the same book". Every change is projected at once so the library shows it.
/// </summary>
public sealed class CopiesService(EntryStore entries, LibraryStore library, MetadataProjector projector)
{
    public async Task<IReadOnlyList<CopyDetails>> GetAsync(EntryId entryId, CancellationToken ct = default)
    {
        var copies = await entries.GetCopiesAsync(entryId, ct);
        var details = new List<CopyDetails>(copies.Count);
        foreach (var copy in copies) details.Add(new CopyDetails(copy, await library.GetLocationsAsync(copy.DocumentId, ct)));
        return details;
    }

    /// <summary>Makes a copy the one the card opens, with its cover, page count and page hits.</summary>
    public async Task MakeCurrentAsync(EntryId entryId, long documentId, CancellationToken ct = default)
    {
        if (await entries.MakeCurrentAsync(entryId, documentId, ct)) await projector.ProjectAsync([entryId], ct);
    }

    /// <summary>Takes a copy onto a card of its own, as it was before it joined; returns that card.</summary>
    public async Task<EntryId?> NotSameBookAsync(EntryId entryId, long documentId, CancellationToken ct = default)
    {
        var card = await entries.SplitCopyAsync(entryId, documentId, ct);
        if (card is { } split) await projector.ProjectAsync([entryId, split], ct);
        return card;
    }
}
