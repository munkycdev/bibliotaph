using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// An adventure marked in a file, as the inspector, the viewer and Needs review show it: its title and its pages in
/// the copy the file shows, or none when they weren't found in that copy.
/// </summary>
public sealed record PartView(EntryId EntryId, string Title, PartPages? Pages);

/// <summary>What marking or changing an adventure did: the adventure, or why it couldn't be done, in words for the user.</summary>
public sealed record PartChange(EntryId? EntryId, string? Problem = null, Func<Task>? Undo = null);

/// <summary>
/// "This adventure's pages weren't found": an adventure whose pages weren't found in the copy or new version its
/// file opens now. <see cref="WasPages"/> are its pages in the copy it was marked in, a guess for Set its pages.
/// </summary>
public sealed record UnplacedPart(EntryId EntryId, EntryId FileEntryId, string Title, string FileTitle, long DocumentId, PartPages? WasPages);

/// <summary>
/// The adventures inside compilation files: marking them by hand, changing their pages, removing them, and following
/// them into the file's other copies and new versions by the fingerprints of their first and last pages. Every change
/// is projected at once, so the library shows the adventure's card, and its first page is drawn as its cover.
/// </summary>
public sealed class PartsService(PartStore parts, EntryStore entries, IndexQueries queries, MetadataProjector projector, StageServices stages,
    ILogger<PartsService>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<PartsService>.Instance;

    /// <summary>An adventure was marked, or came back, in a copy of its file: the document, whose Classify reads it on its own pages.</summary>
    public event EventHandler<long>? Marked;

    /// <summary>
    /// The adventures marked in a file, by where they start in the copy it shows; those whose pages weren't found
    /// there come last. Empty for an entry that isn't a file's.
    /// </summary>
    public async Task<IReadOnlyList<PartView>> GetAsync(EntryId fileId, CancellationToken ct = default)
    {
        var all = await parts.GetPartsAsync(fileId, ct);
        if (all.Count == 0) return [];
        var shown = await entries.GetCurrentDocumentAsync(fileId, ct);
        var titles = await queries.GetEntryTitlesAsync([.. all.Select(p => p.EntryId)], ct);
        return [.. all.Select(p => new PartView(p.EntryId, titles.GetValueOrDefault(p.EntryId) ?? "Untitled adventure", shown is { } d ? p.On(d)?.Pages : null))
            .OrderBy(p => p.Pages is null).ThenBy(p => p.Pages?.First)];
    }

    /// <summary>The adventures with pages in <paramref name="documentId"/>, by where they start: for the viewer's Contents panel.</summary>
    public async Task<IReadOnlyList<PartView>> GetInDocumentAsync(long documentId, CancellationToken ct = default)
    {
        var pages = await parts.GetPagesInAsync(documentId, ct);
        if (pages.Count == 0) return [];
        var titles = await queries.GetEntryTitlesAsync([.. pages.Select(p => p.EntryId)], ct);
        return [.. pages.Select(p => new PartView(p.EntryId, titles.GetValueOrDefault(p.EntryId) ?? "Untitled adventure", p.Pages))];
    }

    /// <summary>The printed labels of <paramref name="documentId"/>'s pages, one per page, for typing an adventure's pages as the file numbers them.</summary>
    public Task<IReadOnlyList<string?>> GetPageLabelsAsync(long documentId, CancellationToken ct = default) => queries.GetPageLabelsAsync(documentId, ct);

    /// <summary>The file's own card that <paramref name="documentId"/> is a copy of, which adventures can be marked in; null for any other document.</summary>
    public async Task<EntryId?> GetFileAsync(long documentId, CancellationToken ct = default) =>
        await entries.GetEntryAsync(documentId, ct) is { } entry && await entries.GetKindAsync(entry.EntryId, ct) == EntryKind.Whole ? entry.EntryId : null;

    /// <summary>
    /// "Index as its own adventure" and "Mark pages as an adventure": pages of a copy of a file become an adventure
    /// with a card of its own, found in the file's other copies too. Returns it, or why it can't be.
    /// </summary>
    public Task<PartChange> AddAsync(long documentId, PartPages pages, string title, CancellationToken ct = default) =>
        AddAsync(documentId, pages, title, [], ct);

    /// <summary>
    /// <see cref="AddAsync(long, PartPages, string, CancellationToken)"/>, with what a proposal read for the adventure,
    /// such as the levels its blurb gives, as suggestions on its card.
    /// </summary>
    public async Task<PartChange> AddAsync(long documentId, PartPages pages, string title, IReadOnlyList<MetadataProposal> suggestions, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return new PartChange(null, "Give the adventure a title.");
        if (await GetFileAsync(documentId, ct) is not { } fileId) return new PartChange(null, "Adventures can only be marked in a book's own file.");
        // An adventure's card names its file, which a pack hides; the pack would have to be split to show it.
        if (await entries.GetCardAsync(documentId, ct) != fileId)
            return new PartChange(null, "This file is in a pack. Split the pack in its details to mark adventures in it.");
        if (await CheckAsync(documentId, pages, null, ct) is { } problem) return new PartChange(null, problem);
        var contentHash = suggestions.Count == 0 ? null : (await entries.GetEntryAsync(documentId, ct))?.ContentHash;
        if (await parts.AddAsync(fileId, await MarkAsync(documentId, pages, ct), title, suggestions, contentHash, ct) is not { } partId)
            return new PartChange(null, "Those pages overlap another adventure in this file.");
        _log.LogInformation("Marked pages {First} to {Last} of document {DocumentId} as adventure {EntryId}", pages.First, pages.Last, documentId, partId);
        await FollowAsync(fileId, ct: ct);
        await projector.ProjectAsync([fileId, partId], ct);
        await DrawCoversAsync(documentId, [pages.First], ct);
        Marked?.Invoke(this, documentId);
        return new PartChange(partId);
    }

    /// <summary>
    /// "Edit range": the adventure's pages in <paramref name="documentId"/>, a copy of its file, become these. Its
    /// pages in the file's other copies are looked for again. Returns what undoes it, or why it can't be.
    /// </summary>
    public async Task<PartChange> SetPagesAsync(EntryId partId, long documentId, PartPages pages, CancellationToken ct = default)
    {
        if (await parts.GetPartAsync(partId, ct) is not { } part) return new PartChange(null, "That adventure isn't in the library any more.");
        if (await CheckAsync(documentId, pages, partId, ct) is { } problem) return new PartChange(null, problem);
        var current = await CurrentAsync(part.ParentEntryId, ct);
        if (!await parts.SetSourcesAsync(partId, [await MarkAsync(documentId, pages, ct)], current, ct))
            return new PartChange(null, "Those pages overlap another adventure in this file.");
        await FollowAsync(part.ParentEntryId, ct: ct);
        await projector.ProjectAsync([part.ParentEntryId, partId], ct);
        await DrawCoversAsync(documentId, [pages.First], ct);
        return new PartChange(partId, Undo: async () =>
        {
            await parts.SetSourcesAsync(partId, part.Sources, await CurrentAsync(part.ParentEntryId, CancellationToken.None), CancellationToken.None);
            await projector.ProjectAsync([part.ParentEntryId, partId], CancellationToken.None);
        });
    }

    /// <summary>"Remove adventure": its card goes and the file is whole again. Returns what Undo needs, or null when it was gone already.</summary>
    public async Task<RemovedPart?> RemoveAsync(EntryId partId, CancellationToken ct = default)
    {
        if (await parts.RemoveAsync(partId, ct) is not { } removed) return null;
        _log.LogInformation("Removed adventure {EntryId} from entry {FileId}", partId, removed.ParentEntryId);
        await projector.ProjectAsync([removed.ParentEntryId, partId], ct);
        return removed;
    }

    /// <summary>Undoes <see cref="RemoveAsync"/>: the adventure is back, as a new card with what was set on it.</summary>
    public async Task<EntryId?> RestoreAsync(RemovedPart removed, CancellationToken ct = default)
    {
        if (await parts.RestoreAsync(removed, ct) is not { } partId) return null;
        await FollowAsync(removed.ParentEntryId, ct: ct);
        await projector.ProjectAsync([removed.ParentEntryId, partId], ct);
        if (await entries.GetCurrentDocumentAsync(removed.ParentEntryId, ct) is { } shown) Marked?.Invoke(this, shown);
        return partId;
    }

    /// <summary>
    /// Follows the adventures marked in <paramref name="documentId"/>'s file into its copies, once that document's
    /// pages have fingerprints: as Match finishes reading it.
    /// </summary>
    public async Task FollowDocumentAsync(long documentId, CancellationToken ct = default)
    {
        if (await entries.GetEntryAsync(documentId, ct) is { } entry) await FollowAsync(entry.EntryId, documentId, ct);
    }

    /// <summary>
    /// Finds each adventure marked in a file in every copy of it: a copy that joined, or a new version the file opens
    /// now. An adventure's first page is found by its fingerprint, the one nearest where it was when a page repeats,
    /// and its last page by its own, else at the same distance. Pages in a document that isn't a copy of the file any
    /// more are let go, and the pages in the copy the file opens become current. An adventure whose pages aren't
    /// found in a copy, or would overlap another there, isn't in that copy; when that copy is the one the file shows,
    /// Needs review asks for its pages. <paramref name="fingerprinted"/> is a copy whose fingerprints Match has just
    /// written, before its stage is done. Returns the adventures that changed, projected.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> FollowAsync(EntryId fileId, long? fingerprinted = null, CancellationToken ct = default)
    {
        var all = await parts.GetPartsAsync(fileId, ct);
        if (all.Count == 0) return [];
        var copies = await entries.GetCopiesAsync(fileId, ct);
        var current = copies.FirstOrDefault(c => c.IsCurrent)?.DocumentId;
        var withFingerprints = (await queries.GetFingerprintedAsync([.. copies.Select(c => c.DocumentId)], ct)).ToHashSet();
        if (fingerprinted is { } just) withFingerprints.Add(just);
        var taken = new Dictionary<long, List<PartPages>>();
        void Take(long documentId, PartPages pages)
        {
            if (!taken.TryGetValue(documentId, out var list)) taken[documentId] = list = [];
            list.Add(pages);
        }

        // What each adventure keeps: its pages in copies that are still the file's, with fingerprints once they have them.
        var planned = new List<(PartEntry Part, List<PartSource> Sources)>(all.Count);
        foreach (var part in all)
        {
            var kept = new List<PartSource>();
            foreach (var source in part.Sources.Where(s => copies.Any(c => c.DocumentId == s.DocumentId)))
            {
                var marked = source.FirstFingerprint is null && withFingerprints.Contains(source.DocumentId) ? await MarkAsync(source.DocumentId, source.Pages, ct) : source;
                kept.Add(marked);
                Take(source.DocumentId, source.Pages);
            }
            planned.Add((part, kept));
        }

        // Then where each one is in the copies that don't have it yet, from its pages in the copy the file opens.
        foreach (var (_, kept) in planned)
        {
            if (kept.Count == 0) continue;
            var from = kept.FirstOrDefault(s => s.DocumentId == current) ?? kept[0];
            foreach (var copy in copies.Where(c => kept.All(s => s.DocumentId != c.DocumentId) && withFingerprints.Contains(c.DocumentId)))
            {
                if (await FindAsync(from, copy.DocumentId, ct) is not { } pages) continue;
                if (taken.TryGetValue(copy.DocumentId, out var others) && others.Any(o => o.Overlaps(pages))) continue;
                kept.Add(await MarkAsync(copy.DocumentId, pages, ct));
                Take(copy.DocumentId, pages);
            }
        }

        var changed = new List<EntryId>();
        var covers = new List<(long DocumentId, int Page)>();
        foreach (var (part, kept) in planned)
        {
            var same = kept.Count == part.Sources.Count && kept.All(k => part.Sources.Any(s => s.DocumentId == k.DocumentId && s.Pages == k.Pages
                && s.FirstFingerprint == k.FirstFingerprint && s.LastFingerprint == k.LastFingerprint && s.IsCurrent == (k.DocumentId == current)));
            if (same || !await parts.SetSourcesAsync(part.EntryId, kept, current, ct)) continue;
            changed.Add(part.EntryId);
            covers.AddRange(kept.Where(k => part.On(k.DocumentId) is not { } before || before.Pages.First != k.Pages.First).Select(k => (k.DocumentId, k.Pages.First)));
        }
        if (changed.Count == 0) return [];
        _log.LogInformation("Followed {Count} adventures of entry {FileId} into its copies", changed.Count, fileId);
        await projector.ProjectAsync([fileId, .. changed], ct);
        foreach (var group in covers.GroupBy(c => c.DocumentId)) await DrawCoversAsync(group.Key, [.. group.Select(c => c.Page)], ct);
        return changed;
    }

    /// <summary>The adventures whose pages weren't found in the copy their file shows, for Needs review.</summary>
    public async Task<IReadOnlyList<UnplacedPart>> GetUnplacedAsync(CancellationToken ct = default)
    {
        var found = new List<(PartEntry Part, long DocumentId)>();
        foreach (var fileId in await parts.GetParentsAsync(ct))
        {
            if (await entries.GetCurrentDocumentAsync(fileId, ct) is not { } shown) continue;
            // Nothing is judged before the copy's pages have their fingerprints.
            if ((await queries.GetFingerprintedAsync([shown], ct)).Count == 0) continue;
            found.AddRange((await parts.GetPartsAsync(fileId, ct)).Where(p => p.On(shown) is null).Select(p => (p, shown)));
        }
        if (found.Count == 0) return [];
        var titles = await queries.GetEntryTitlesAsync([.. found.SelectMany(f => new[] { f.Part.EntryId, f.Part.ParentEntryId }).Distinct()], ct);
        return [.. found.Select(f => new UnplacedPart(f.Part.EntryId, f.Part.ParentEntryId, titles.GetValueOrDefault(f.Part.EntryId) ?? "Untitled adventure",
            titles.GetValueOrDefault(f.Part.ParentEntryId) ?? "Untitled", f.DocumentId, f.Part.Sources is [var was, ..] ? was.Pages : null))];
    }

    /// <summary>Why these pages can't be an adventure in <paramref name="documentId"/> beside the others marked there, or null when they can.</summary>
    async Task<string?> CheckAsync(long documentId, PartPages pages, EntryId? except, CancellationToken ct)
    {
        var count = await queries.GetPageCountAsync(documentId, ct) ?? 0;
        var others = (await GetInDocumentAsync(documentId, ct)).Where(p => p.EntryId != except && p.Pages is not null).Select(p => (p.Title, p.Pages!.Value));
        return PartRanges.Check(pages, count, others);
    }

    async Task<long?> CurrentAsync(EntryId fileId, CancellationToken ct) =>
        (await entries.GetCopiesAsync(fileId, ct)).FirstOrDefault(c => c.IsCurrent)?.DocumentId;

    /// <summary>These pages of a document, with the fingerprints of the first and last as index.db has them now.</summary>
    async Task<PartSource> MarkAsync(long documentId, PartPages pages, CancellationToken ct)
    {
        var first = await queries.GetPageMarkAsync(documentId, pages.First, ct);
        var last = pages.Last == pages.First ? first : await queries.GetPageMarkAsync(documentId, pages.Last, ct);
        return new PartSource(documentId, pages, first?.Fingerprint, last?.Fingerprint);
    }

    /// <summary>An adventure's pages in another copy, found by the fingerprints of its first and last pages; null when the first isn't there.</summary>
    async Task<PartPages?> FindAsync(PartSource from, long copy, CancellationToken ct)
    {
        if (from.FirstFingerprint is not { } fingerprint || await queries.FindFingerprintAsync(copy, fingerprint, ct) is not { Count: > 0 } found) return null;
        var span = from.Pages.Last - from.Pages.First;
        var first = found.MinBy(p => Math.Abs(p - from.Pages.First));
        var last = first + span;
        if (span > 0 && from.LastFingerprint is { } lastPrint && (await queries.FindFingerprintAsync(copy, lastPrint, ct)).Where(p => p >= first).ToList() is { Count: > 0 } ends)
            last = ends.MinBy(p => Math.Abs(p - (first + span)));
        if (await queries.GetPageCountAsync(copy, ct) is { } count and > 0) last = Math.Min(last, count - 1);
        return new PartPages(first, last);
    }

    /// <summary>Draws adventures' first pages as their covers, when the file can be read; a cover that can't be drawn now is drawn by its Covers stage.</summary>
    async Task DrawCoversAsync(long documentId, IReadOnlyCollection<int> pages, CancellationToken ct)
    {
        if (pages.All(p => p <= 0)) return;
        var contentHash = (await entries.GetEntryAsync(documentId, ct))?.ContentHash;
        if (contentHash is null) return;
        try
        {
            var (session, _) = await StageHelpers.OpenPdfAsync(stages, new JobRecord(0, documentId, contentHash, Stage.Covers, 0, 0), ct);
            if (session is null) return;
            await using (session) await StageHelpers.DrawPartCoversAsync(stages, session, documentId, contentHash, pages, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only a cover: the adventure is marked either way, and Covers draws it when the file is read again.
            _log.LogWarning(ex, "An adventure's cover in document {DocumentId} couldn't be drawn now", documentId);
        }
    }
}
