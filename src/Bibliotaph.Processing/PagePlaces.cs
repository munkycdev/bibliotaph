using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>How a place's pages (a session item's or a page note's) open now.</summary>
public enum PagePlaceState
{
    /// <summary>In the file it was made in, as it is.</summary>
    Ready,

    /// <summary>In the book's current copy or version, found by the pages' fingerprints, maybe at other page numbers.</summary>
    OtherCopy,

    /// <summary>The current copy doesn't have the pages, or can't be reached, but the file they were made in is there: it opens that.</summary>
    Original,

    /// <summary>The pages couldn't be found in the book as it is now: it opens at the same printed page, to be checked.</summary>
    Changed,

    /// <summary>
    /// A new version of the book is still being read, so its pages haven't been looked for yet (slice 4h plan, choice
    /// 1): nothing is judged changed until they have. It opens at the same printed page meanwhile.
    /// </summary>
    Pending,

    /// <summary>No file to open: its folder is offline, or it's gone.</summary>
    Unreachable,
}

/// <summary>
/// Where a place's pages open: a document and its pages (zero-based, inclusive), how they were found, and for a place
/// that can't open, why and the last place its file was.
/// </summary>
public sealed record PagePlace(long? DocumentId, int FirstPage, int LastPage, PagePlaceState State, string? Reason = null, string? LastPath = null)
{
    public bool CanOpen => DocumentId is not null && State != PagePlaceState.Unreachable;
}

/// <summary>A place looked for in a new version of its book, for its Needs review card: what was found, and the printed label of that page.</summary>
public sealed record RevisionPlace(PlacedPages Place, PageRefCheck Check, string? PageLabel)
{
    public PageCheck Outcome => Check.Outcome;
}

/// <summary>
/// "New version of a book: 12 places found their page, 2 need a look" (slice 4h plan, choice 3): one Needs review card
/// per version a book opens now, while any of the places looked for in it needs a look.
/// </summary>
public sealed record RevisionItem(EntryId EntryId, long DocumentId, string BookTitle, string? Path, IReadOnlyList<RevisionPlace> Places)
{
    public int Found => Places.Count(p => p.Outcome == PageCheck.Found);

    public int NeedLook => Places.Count(p => p.Outcome == PageCheck.NeedsLook);
}

/// <summary>
/// Where the places in a book open (slice 3 plan, choice 14; slice 4h plan, choices 1 to 5): session items' pages and
/// page notes alike. A place opens in its book's current copy; made in another copy or an earlier version, it opens at
/// the pages whose fingerprints match, or failing that at the same printed page, marked "This page changed: check it",
/// where Use this page points it at the right one. What was found is kept on the page reference when a new version has
/// been read, so a session or a note shows it without looking again and Needs review lists the places that need a
/// look. Until a new version has its fingerprints, nothing is judged. The reading position moves to the matching page too.
/// </summary>
public sealed class PagePlaces(PageRefStore refs, EntryStore entries, LibraryStore library, IndexQueries index, ReadingStore reading,
    ILogger<PagePlaces>? log = null)
{
    public const string ChangedReason = "This page changed: check it.";
    public const string PendingReason = "New version: still being read. Until it is, this opens at the same page.";

    readonly ILogger _log = log ?? NullLogger<PagePlaces>.Instance;

    /// <summary>
    /// Where each place opens now, in the order given. A place's pages are looked for in its book's current copy at most
    /// once: what was found is kept, here for places made before slice 4h or in a copy no version check covered.
    /// </summary>
    public async Task<IReadOnlyList<PagePlace>> ResolveAsync(IReadOnlyList<(EntryId EntryId, PageRange Range)> places, CancellationToken ct = default)
    {
        if (places.Count == 0) return [];
        var current = await CurrentDocumentsAsync([.. places.Select(p => p.EntryId).Distinct()], ct);
        var copies = current.Values.OfType<long>().Distinct().ToList();
        var readable = (await library.GetReadableAsync([.. places.Select(p => p.Range.DocumentId).Concat(copies).Distinct()], ct)).ToHashSet();
        var fingerprinted = (await index.GetFingerprintedAsync(copies, ct)).ToHashSet();
        var found = new List<(long RefId, PageRefCheck? Check)>();
        var result = new List<PagePlace>(places.Count);
        foreach (var (entryId, range) in places)
            result.Add(await ResolveAsync(range, current.GetValueOrDefault(entryId), readable, fingerprinted, found, ct));
        if (found.Count > 0) await refs.SaveChecksAsync(found, ct);
        return result;
    }

    async Task<PagePlace> ResolveAsync(PageRange range, long? current, HashSet<long> readable, HashSet<long> fingerprinted,
        List<(long RefId, PageRefCheck? Check)> found, CancellationToken ct)
    {
        if (current == range.DocumentId || current is null)
        {
            return readable.Contains(range.DocumentId)
                ? new PagePlace(range.DocumentId, range.FirstPdfPage, range.LastPdfPage, PagePlaceState.Ready)
                : await UnreachableAsync(range.DocumentId, ct);
        }
        var copy = current.Value;
        var check = range.Check is { } kept && kept.DocumentId == copy ? kept : null;
        if (check is null)
        {
            // Nothing is judged before the copy's pages have their fingerprints (choice 1).
            if (!fingerprinted.Contains(copy))
            {
                if (readable.Contains(range.DocumentId))
                    return new PagePlace(range.DocumentId, range.FirstPdfPage, range.LastPdfPage, PagePlaceState.Pending,
                        "New version: still being read. Until it is, this opens the file these pages were added from.");
                if (!readable.Contains(copy)) return await UnreachableAsync(copy, ct);
                var page = await SamePageAsync(range, copy, ct);
                return new PagePlace(copy, page, page + Span(range), PagePlaceState.Pending, PendingReason);
            }
            check = await FindAsync(range, copy, ct);
            if (range.RefId > 0) found.Add((range.RefId, check));
        }
        if (check.Outcome != PageCheck.NeedsLook && readable.Contains(copy))
            return new PagePlace(copy, check.FirstPdfPage, check.LastPdfPage, PagePlaceState.OtherCopy);
        if (readable.Contains(range.DocumentId))
            return new PagePlace(range.DocumentId, range.FirstPdfPage, range.LastPdfPage, PagePlaceState.Original, check.Outcome == PageCheck.NeedsLook
                ? "These pages aren't in the book's current copy, so this opens the file they were added from."
                : "The book's current copy can't be reached, so this opens the file these pages were added from.");
        if (readable.Contains(copy)) return new PagePlace(copy, check.FirstPdfPage, check.LastPdfPage, PagePlaceState.Changed, ChangedReason);
        return await UnreachableAsync(copy, ct);
    }

    /// <summary>
    /// Why a document can't be opened, and where its file last was. A file whose new content is still being hashed is
    /// a new version still being read, not a file that has gone.
    /// </summary>
    public async Task<PagePlace> UnreachableAsync(long documentId, CancellationToken ct = default)
    {
        if (await library.IsBeingReplacedAsync(documentId, ct)) return new PagePlace(null, 0, 0, PagePlaceState.Pending, "New version: still being read.");
        var locations = await library.GetLocationsAsync(documentId, ct);
        var offline = locations.FirstOrDefault(l => l.RootAvailability == SourceRootAvailability.Offline);
        var last = offline ?? (locations.Count > 0 ? locations[0] : null);
        return new PagePlace(null, 0, 0, PagePlaceState.Unreachable,
            offline is not null ? "Its folder is offline. It opens again once the folder is back." : "Its file is gone from your library folders.",
            last?.ExplorerPath);
    }

    /// <summary>
    /// The document each entry opens: the one its card shows, or for an image hidden in a pack, its own file (slice 3
    /// plan, choice 19: those keep their items and still open). A book owned elsewhere has none.
    /// </summary>
    public async Task<Dictionary<EntryId, long?>> CurrentDocumentsAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        var result = (await entries.GetCurrentAsync(entryIds, ct)).ToDictionary(e => e.EntryId, e => e.DocumentId);
        foreach (var missing in entryIds.Where(e => !result.ContainsKey(e)))
            result[missing] = (await entries.GetCopiesAsync(missing, ct)).FirstOrDefault(c => c.IsShown)?.DocumentId;
        return result;
    }

    /// <summary>
    /// Looks for every place in a book in the version it opens now (choices 1 to 5), once that version's pages have
    /// their fingerprints: as Match finishes reading a new version (<paramref name="fingerprinted"/>), or when a copy is
    /// made current. What was found is kept, a place the user already checked there stays as they chose, and where the
    /// book was left moves to the matching page. Returns how many places need a look.
    /// </summary>
    public async Task<int> CheckAsync(long documentId, bool fingerprinted = false, CancellationToken ct = default)
    {
        if (await entries.GetEntryAsync(documentId, ct) is not { } entry) return 0;
        if (await entries.GetCurrentDocumentAsync(entry.EntryId, ct) != documentId) return 0;
        if (!fingerprinted && (await index.GetFingerprintedAsync([documentId], ct)).Count == 0) return 0;
        var checks = new List<(long RefId, PageRefCheck? Check)>();
        foreach (var place in await refs.GetForEntryAsync(entry.EntryId, ct))
        {
            var range = place.Range;
            if (range.DocumentId == documentId || range.Check is { Outcome: PageCheck.Checked } done && done.DocumentId == documentId) continue;
            checks.Add((range.RefId, await FindAsync(range, documentId, ct)));
        }
        await refs.SaveChecksAsync(checks, ct);
        await MoveReadingPlaceAsync(documentId, ct);
        var look = checks.Count(c => c.Check!.Outcome == PageCheck.NeedsLook);
        if (checks.Count > 0)
            _log.LogInformation("Looked for {Count} places of entry {EntryId} in document {DocumentId}; {Look} need a look", checks.Count, entry.EntryId, documentId, look);
        return look;
    }

    /// <summary>Where a book was left, in another of its copies, moves to the matching page of the one it opens now (choice 5).</summary>
    async Task MoveReadingPlaceAsync(long documentId, CancellationToken ct)
    {
        if (await reading.GetPlaceAsync(documentId, ct) is not { } place || place.DocumentId == documentId) return;
        var page = await MapPageAsync(place.DocumentId, place.PageIndex, documentId, ct);
        if (await reading.MoveAsync(place.DocumentId, documentId, page, ct))
            _log.LogInformation("Moved where document {From} was left, page {Page}, to page {To} of document {DocumentId}", place.DocumentId, place.PageIndex, page, documentId);
    }

    /// <summary>
    /// The page of <paramref name="toDocumentId"/> that matches a page of another copy: the page with its fingerprint
    /// nearest where it was, else the page with its printed label, else the same page as near as the book has one.
    /// </summary>
    public async Task<int> MapPageAsync(long fromDocumentId, int page, long toDocumentId, CancellationToken ct = default)
    {
        if (fromDocumentId == toDocumentId) return page;
        var mark = await index.GetPageMarkAsync(fromDocumentId, page, ct);
        if (mark?.Fingerprint is { } fingerprint && (await index.FindFingerprintAsync(toDocumentId, fingerprint, ct)) is { Count: > 0 } found)
            return found.MinBy(p => Math.Abs(p - page));
        if (mark?.Label is { } label && await index.FindLabelAsync(toDocumentId, label, ct) is { } labelled) return labelled;
        return await ClampAsync(toDocumentId, page, ct);
    }

    /// <summary>
    /// Looks for a place's pages in another copy (choice 2): the first page by its fingerprint, the one nearest where it
    /// was when a page is repeated, and the last by its own; failing that, the same printed page, to be checked.
    /// </summary>
    async Task<PageRefCheck> FindAsync(PageRange range, long copy, CancellationToken ct)
    {
        var span = Span(range);
        if (range.FirstFingerprint is { } fingerprint && (await index.FindFingerprintAsync(copy, fingerprint, ct)) is { Count: > 0 } found)
        {
            var first = found.MinBy(p => Math.Abs(p - range.FirstPdfPage));
            var last = first + span;
            if (span > 0 && range.LastFingerprint is { } lastPrint && (await index.FindFingerprintAsync(copy, lastPrint, ct)).Where(p => p >= first).ToList() is { Count: > 0 } ends)
                last = ends.MinBy(p => Math.Abs(p - (first + span)));
            return new PageRefCheck(PageCheck.Found, copy, first, last);
        }
        var page = await SamePageAsync(range, copy, ct);
        return new PageRefCheck(PageCheck.NeedsLook, copy, page, page + span);
    }

    /// <summary>The page of a copy with the place's first printed label, or else the same page, as near as the copy has one.</summary>
    async Task<int> SamePageAsync(PageRange range, long copy, CancellationToken ct) =>
        (range.FirstLabel is { } label ? await index.FindLabelAsync(copy, label, ct) : null) ?? await ClampAsync(copy, range.FirstPdfPage, ct);

    async Task<int> ClampAsync(long documentId, int page, CancellationToken ct) =>
        await index.GetPageCountAsync(documentId, ct) is { } count and > 0 ? Math.Clamp(page, 0, count - 1) : Math.Max(0, page);

    static int Span(PageRange range) => range.LastPdfPage - range.FirstPdfPage;

    /// <summary>A page range with the printed labels and fingerprints of its first and last pages, as index.db has them now.</summary>
    public async Task<PageRange> MakeRangeAsync(long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        var (first, last) = (Math.Min(firstPage, lastPage), Math.Max(firstPage, lastPage));
        var a = await index.GetPageMarkAsync(documentId, first, ct);
        var b = first == last ? a : await index.GetPageMarkAsync(documentId, last, ct);
        return new PageRange(documentId, first, last, a?.Label, b?.Label, a?.Fingerprint, b?.Fingerprint);
    }

    /// <summary>
    /// The Needs review cards (choice 3): for each version a book opens now, the places looked for in it, while any of
    /// them needs a look. Those that need one come first, then those the user checked, then those found by themselves.
    /// </summary>
    public async Task<IReadOnlyList<RevisionItem>> GetRevisionsAsync(CancellationToken ct = default)
    {
        var groups = await RevisionsAsync(await refs.GetCheckedAsync(ct: ct), ct);
        if (groups.Count == 0) return [];
        var titles = await index.GetEntryTitlesAsync([.. groups.Select(g => g.EntryId).Distinct()], ct);
        var items = new List<RevisionItem>(groups.Count);
        foreach (var (entryId, documentId, places) in groups)
        {
            var path = (await library.GetLocationsAsync(documentId, ct)).FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
            var shown = new List<RevisionPlace>(places.Count);
            foreach (var place in places.OrderBy(p => p.Range.Check!.Outcome switch { PageCheck.NeedsLook => 0, PageCheck.Checked => 1, _ => 2 })
                .ThenBy(p => p.Kind).ThenBy(p => p.PackTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Range.Check!.FirstPdfPage))
            {
                var check = place.Range.Check!;
                shown.Add(new RevisionPlace(place, check, (await index.GetPageMarkAsync(documentId, check.FirstPdfPage, ct))?.Label));
            }
            items.Add(new RevisionItem(entryId, documentId, titles.GetValueOrDefault(entryId) ?? System.IO.Path.GetFileNameWithoutExtension(path) ?? "Untitled", path, shown));
        }
        return items;
    }

    /// <summary>How many "new version" cards there are, for the sidebar's count.</summary>
    public async Task<int> CountRevisionsAsync(CancellationToken ct = default) =>
        (await RevisionsAsync(await refs.GetCheckedAsync(needingALook: true, ct), ct)).Count;

    /// <summary>
    /// The places checked against the version each book opens now, by book, for the books where one of them needs a
    /// look. Places checked against a version the book doesn't open any more (another was made current since) are left out.
    /// </summary>
    async Task<List<(EntryId EntryId, long DocumentId, List<PlacedPages> Places)>> RevisionsAsync(IReadOnlyList<PlacedPages> placed, CancellationToken ct)
    {
        var groups = placed
            .Where(p => p.Range.Check is { } check && (check.Outcome == PageCheck.Checked || p.Range.DocumentId != check.DocumentId))
            .GroupBy(p => (p.EntryId, p.Range.Check!.DocumentId))
            .Where(g => g.Any(p => p.Range.Check!.Outcome == PageCheck.NeedsLook))
            .ToList();
        if (groups.Count == 0) return [];
        var current = await CurrentDocumentsAsync([.. groups.Select(g => g.Key.EntryId).Distinct()], ct);
        return [.. groups.Where(g => current.GetValueOrDefault(g.Key.EntryId) == g.Key.DocumentId).Select(g => (g.Key.EntryId, g.Key.DocumentId, g.ToList()))];
    }

    /// <summary>
    /// Use this page on a card (choice 3): the place points at these pages of the version from now on, checked.
    /// Returns what undoes it, or null when the place is gone.
    /// </summary>
    public async Task<Func<Task>?> UseAsync(PlacedPages place, long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        var before = await refs.UseAsync(place.Range.RefId, await MakeRangeAsync(documentId, firstPage, lastPage, ct), ct);
        return before is null ? null : () => refs.RestoreAsync(before);
    }
}
