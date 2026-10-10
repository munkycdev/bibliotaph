using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Fingerprints a document's pages from the text already in index.db and, when another document holds the same book
/// page for page, joins the two as copies on one card (foundation slice F2, choices 1 to 3 of the F2 plan). Otherwise
/// a file that shares most of its pages with a book, or has its title and publisher, is proposed in Needs review as a
/// new version of it (choice 4). Runs after Text, and after OCR for a book with scanned pages and its hints from
/// names, so it never reads the file. A file with the title of a book the user owns elsewhere is offered as its file
/// (F5 plan, choice 6).
/// </summary>
public sealed class MatchStage(EntryStore entries, VersionStore versions, IndexStore index, IndexQueries queries, MetadataProjector projector,
    ILogger<MatchStage>? log = null, ElsewhereStore? elsewhere = null) : IStage
{
    /// <summary>How long Match waits for a document's hints from names, which take moments.</summary>
    static readonly TimeSpan HintsWait = TimeSpan.FromSeconds(30);

    readonly ILogger _log = log ?? NullLogger<MatchStage>.Instance;

    public Stage Stage => Stage.Match;

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (await queries.GetStageStatusAsync(job.DocumentId, Stage.Ocr, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(ClassifyStage.OcrWait, "Waiting for its scanned pages to be read.");
        // Title and publisher come from the hints, which are usually in by now.
        if (await queries.GetStageStatusAsync(job.DocumentId, Stage.RuleHints, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(HintsWait, "Waiting for its hints from names.");

        var texts = await queries.GetAllPageTextsAsync(job.DocumentId, ct);
        var fingerprints = PageFingerprints.Compute(texts);
        await index.SetFingerprintsAsync(job.DocumentId, fingerprints, ct);
        var enoughText = fingerprints.Count(f => f is not null) >= PageFingerprints.MinimumMatchingPages;
        var sharing = enoughText ? await queries.GetSharingPagesAsync(job.DocumentId, ct) : [];

        foreach (var (other, shared, _) in sharing)
        {
            if (shared < PageFingerprints.MinimumMatchingPages) break;
            if (!PageFingerprints.IsSameBook(fingerprints, await queries.GetFingerprintsAsync(other, ct))) continue;
            if (await entries.JoinAsCopyAsync(job.DocumentId, other, ct) is not { } join) continue;
            _log.LogInformation("Document {Document} is a copy of document {Other}; its card joined entry {Entry}", job.DocumentId, other, join.EntryId);
            await projector.ProjectAsync([join.EntryId, join.JoinedEntryId], ct);
            return StageOutcome.Complete();
        }

        await ProposeVersionAsync(job.DocumentId, fingerprints, sharing, ct);
        await ProposeElsewhereAsync(job.DocumentId, ct);
        return enoughText ? StageOutcome.Complete() : new StageOutcome.Done(StageStatus.Skipped, [], "Too little text to compare with other files.");
    }

    /// <summary>
    /// Offers the file as the file of each book owned elsewhere with its title and no publisher that says otherwise.
    /// It doesn't need text of its own: the title is enough to ask.
    /// </summary>
    async Task ProposeElsewhereAsync(long documentId, CancellationToken ct)
    {
        if (elsewhere is null || await entries.GetEntryAsync(documentId, ct) is not { } entry) return;
        foreach (var book in await queries.GetElsewhereMatchesAsync(entry.EntryId, ct))
            if (await elsewhere.ProposeAsync(book, documentId, ct))
                _log.LogInformation("Document {Document} has the title of entry {Entry}, owned elsewhere; proposed as its file", documentId, book);
    }

    /// <summary>
    /// The best file this one may be a new version of, or be the new version of: the most pages in common, as a share of
    /// the smaller file's fingerprinted pages, or else a book with the same title and publisher.
    /// </summary>
    async Task ProposeVersionAsync(long documentId, IReadOnlyList<string?> fingerprints, IReadOnlyList<(long DocumentId, int Shared, int Fingerprinted)> sharing,
        CancellationToken ct)
    {
        var mine = fingerprints.Where(f => f is not null).Distinct(StringComparer.Ordinal).Count();
        var candidates = sharing
            .Select(s => (s.DocumentId, s.Shared, Compared: Math.Min(mine, s.Fingerprinted)))
            .Where(s => s.Shared >= PageFingerprints.MinimumMatchingPages && s.Shared >= VersionStore.MinimumSharedPages * s.Compared)
            .OrderByDescending(s => (double)s.Shared / s.Compared).ThenByDescending(s => s.Shared);
        foreach (var (other, shared, compared) in candidates)
        {
            if (!await versions.ProposeAsync(documentId, other, VersionEvidence.SharedPages, shared, compared, ct)) continue;
            _log.LogInformation("Document {Document} shares {Shared} of {Compared} pages with document {Other}; proposed as a new version", documentId, shared, compared, other);
            return;
        }

        if (await entries.GetEntryAsync(documentId, ct) is not { } entry) return;
        foreach (var other in await queries.GetSameTitleAndPublisherAsync(entry.EntryId, ct))
        {
            if (!await versions.ProposeAsync(documentId, other, VersionEvidence.TitleAndPublisher, ct: ct)) continue;
            _log.LogInformation("Document {Document} has the title and publisher of document {Other}; proposed as a new version", documentId, other);
            return;
        }
    }
}

/// <summary>A copy of a book with the places its file is, for the inspector's Copies list.</summary>
public sealed record CopyDetails(EntryCopy Copy, IReadOnlyList<DocumentLocation> Locations);

/// <summary>
/// A "new version?" card for Needs review: the new file's card title and where it is, and the book it may be a new
/// version of.
/// </summary>
public sealed record VersionItem(PendingVersion Version, string Title, string? Path, string BookTitle, string? BookPath);

/// <summary>
/// The inspector's Copies list (F2 plan, choices 6, 8 and 10): which files hold an entry's book, which one opens, and
/// "Not the same book"; and Needs review's "new version?" cards (choice 4). Every change is projected at once so the
/// library shows it.
/// </summary>
public sealed class CopiesService(EntryStore entries, VersionStore versions, LibraryStore library, IndexQueries queries, MetadataProjector projector)
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
        var card = await entries.SplitCopyAsync(entryId, documentId, ct: ct);
        if (card is { } split) await projector.ProjectAsync([entryId, split], ct);
        return card;
    }

    public async Task<int> CountVersionsAsync(CancellationToken ct = default) => (await versions.GetPendingAsync(ct)).Count;

    /// <summary>The "new version?" cards waiting, oldest first.</summary>
    public async Task<IReadOnlyList<VersionItem>> GetVersionsAsync(CancellationToken ct = default)
    {
        var pending = await versions.GetPendingAsync(ct);
        if (pending.Count == 0) return [];
        var titles = await queries.GetEntryTitlesAsync([.. pending.SelectMany(p => new[] { p.EntryId, p.MatchedEntryId })], ct);
        var items = new List<VersionItem>(pending.Count);
        foreach (var version in pending)
        {
            var path = await PathAsync(version.DocumentId, ct);
            items.Add(new VersionItem(version, titles.GetValueOrDefault(version.EntryId) ?? System.IO.Path.GetFileNameWithoutExtension(path) ?? "Untitled",
                path, titles.GetValueOrDefault(version.MatchedEntryId) ?? "Untitled", await PathAsync(version.MatchedDocumentId, ct)));
        }
        return items;
    }

    async Task<string?> PathAsync(long documentId, CancellationToken ct) =>
        (await library.GetLocationsAsync(documentId, ct)).FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;

    /// <summary>Answers a "new version?" card. Returns what undoes it, or null when it isn't waiting any more.</summary>
    public async Task<VersionDecision?> AnswerVersionAsync(PendingVersion version, VersionAnswer answer, CancellationToken ct = default)
    {
        var decision = await versions.AnswerAsync(version.Id, answer, ct);
        if (decision is not null && answer != VersionAnswer.SeparateBook) await projector.ProjectAsync([version.MatchedEntryId, version.EntryId], ct);
        return decision;
    }

    public async Task UndoVersionAsync(VersionDecision decision, CancellationToken ct = default)
    {
        await versions.UndoAsync(decision, ct);
        if (decision.Answer != VersionAnswer.SeparateBook) await projector.ProjectAsync([decision.Version.MatchedEntryId, decision.Version.EntryId], ct);
    }
}
