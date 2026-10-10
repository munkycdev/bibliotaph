using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>How a session item will open, worked out each time its pack is shown (slice 3 plan, choices 14 and 15).</summary>
public enum SessionItemState
{
    /// <summary>Opens as it was added: the whole book, or its pages in the same file.</summary>
    Ready,

    /// <summary>The book's current copy or version has the pages, found by their fingerprints, maybe at other page numbers.</summary>
    OtherCopy,

    /// <summary>The current copy doesn't have the pages, but the file they were added from is still there: it opens that.</summary>
    Original,

    /// <summary>The pages couldn't be found in the book as it is now: it opens at the same printed page, to be checked.</summary>
    Changed,

    /// <summary>No file to open: its folder is offline, or it's gone. The item stays, dimmed.</summary>
    Unreachable,

    /// <summary>A book owned elsewhere, with no file at all.</summary>
    NoFile,
}

/// <summary>
/// Where a session item opens: a document and its pages (zero-based, inclusive), how they were found, and for an item
/// that can't open, why and the last place its file was.
/// </summary>
public sealed record SessionItemTarget(long? DocumentId, int FirstPage, int LastPage, SessionItemState State, string? Reason = null, string? LastPath = null)
{
    public bool CanOpen => DocumentId is not null && State is not (SessionItemState.Unreachable or SessionItemState.NoFile);
}

/// <summary>
/// Session packs (slice 3 plan, choices 11 to 15): each change goes to catalog.db, then the books' marks are projected,
/// so a pack's books show as a group in index.db. Page ranges get their labels and fingerprints from index.db when
/// they are made, and are looked for again in the book's current copy each time the pack is shown.
/// </summary>
public sealed class SessionsService(SessionStore sessions, EntryStore entries, LibraryStore library, IndexQueries index, MetadataProjector projector)
{
    /// <summary>Raised after any pack changed, on the thread that changed it.</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<SessionPackInfo>> ListAsync(CancellationToken ct = default) => sessions.ListAsync(ct);

    public Task<SessionPackContents?> GetAsync(long packId, CancellationToken ct = default) => sessions.GetAsync(packId, ct);

    /// <summary>Opening a pack makes it the current one, which Add to session adds to.</summary>
    public async Task<SessionPackContents?> OpenAsync(long packId, CancellationToken ct = default)
    {
        if (!await sessions.TouchAsync(packId, ct)) return null;
        Changed?.Invoke(this, EventArgs.Empty);
        return await sessions.GetAsync(packId, ct);
    }

    public async Task<SessionPackInfo> CreateAsync(string title, DateOnly? date = null, CancellationToken ct = default)
    {
        var created = await sessions.CreateAsync(title, date, ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    public async Task UpdateAsync(long packId, string title, DateOnly? date, CancellationToken ct = default)
    {
        if (await sessions.UpdateAsync(packId, title, date, ct)) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The pack's notes, saved as they are typed. Raises no <see cref="Changed"/>: nothing else shows them.</summary>
    public Task SetNotesAsync(long packId, string? notes, CancellationToken ct = default) => sessions.SetNotesAsync(packId, notes, ct);

    public async Task<SessionPackSnapshot?> DeleteAsync(long packId, CancellationToken ct = default)
    {
        var deleted = await sessions.DeleteAsync(packId, ct);
        if (deleted is not null) await ProjectAsync([.. deleted.Items.Select(i => i.EntryId)], ct);
        return deleted;
    }

    public async Task<SessionPackInfo> RestoreAsync(SessionPackSnapshot deleted, CancellationToken ct = default)
    {
        var restored = await sessions.RestoreAsync(deleted, ct);
        await ProjectAsync([.. deleted.Items.Select(i => i.EntryId)], ct);
        return restored;
    }

    public async Task<SessionPackInfo?> DuplicateAsync(long packId, string title, CancellationToken ct = default)
    {
        var copy = await sessions.DuplicateAsync(packId, title, ct);
        if (copy is not null) await ProjectAsync(await sessions.GetEntriesAsync(copy.Id, ct), ct);
        return copy;
    }

    /// <summary>Adds whole books or page ranges to a pack (see <see cref="SessionStore.AddItemsAsync"/> for <paramref name="sectionId"/>).</summary>
    public async Task<IReadOnlyList<long>> AddAsync(long packId, IReadOnlyList<NewSessionItem> items, long? sectionId = null, CancellationToken ct = default)
    {
        var added = await sessions.AddItemsAsync(packId, items, sectionId, ct);
        await ProjectAsync([.. items.Select(i => i.EntryId)], ct);
        return added;
    }

    /// <summary>
    /// Add page and Add pages… (choice 13): pages of the document the reader has open, as an item of the card it shows
    /// under. Without a label, the item is called after the bookmark the first page sits under. Returns the new item's id.
    /// </summary>
    public async Task<long?> AddPagesAsync(long packId, long documentId, int firstPage, int lastPage, string? label = null, long? sectionId = null,
        CancellationToken ct = default)
    {
        if (await entries.GetEntryAsync(documentId, ct) is not { } entry) return null;
        var range = await MakeRangeAsync(documentId, firstPage, lastPage, ct);
        label = string.IsNullOrWhiteSpace(label) ? await SuggestLabelAsync(documentId, range.FirstPdfPage, ct) : label;
        return (await AddAsync(packId, [new NewSessionItem(entry.EntryId, range, label)], sectionId, ct)) is [var id] ? id : null;
    }

    /// <summary>Add page on an image: the whole file, as an item of its own entry. Returns the new item's id.</summary>
    public async Task<long?> AddDocumentAsync(long packId, long documentId, long? sectionId = null, CancellationToken ct = default) =>
        await entries.GetEntryAsync(documentId, ct) is { } entry && (await AddAsync(packId, [new NewSessionItem(entry.EntryId)], sectionId, ct)) is [var id] ? id : null;

    /// <summary>The bookmark a page sits under, as a label for an item made of it; null when the book has none there.</summary>
    public async Task<string?> SuggestLabelAsync(long documentId, int page, CancellationToken ct = default) =>
        (await index.GetBookmarkAsync(documentId, page, ct))?.Trim() is { Length: > 0 } title ? title : null;

    /// <summary>A page range with the printed labels and fingerprints of its first and last pages, as index.db has them now.</summary>
    public async Task<PageRange> MakeRangeAsync(long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        var (first, last) = (Math.Min(firstPage, lastPage), Math.Max(firstPage, lastPage));
        var a = await index.GetPageMarkAsync(documentId, first, ct);
        var b = first == last ? a : await index.GetPageMarkAsync(documentId, last, ct);
        return new PageRange(documentId, first, last, a?.Label, b?.Label, a?.Fingerprint, b?.Fingerprint);
    }

    public async Task<IReadOnlyList<SessionItemInfo>> RemoveItemsAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct = default)
    {
        var removed = await sessions.RemoveItemsAsync(itemIds, ct);
        if (removed.Count > 0) await ProjectAsync([.. removed.Select(i => i.EntryId)], ct);
        return removed;
    }

    public async Task RestoreItemsAsync(IReadOnlyList<SessionItemInfo> removed, CancellationToken ct = default)
    {
        await sessions.RestoreItemsAsync(removed, ct);
        await ProjectAsync([.. removed.Select(i => i.EntryId)], ct);
    }

    public async Task UpdateItemAsync(long itemId, string? label, string? note, CancellationToken ct = default)
    {
        if (await sessions.UpdateItemAsync(itemId, label, note, ct)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> MoveItemAsync(long itemId, int by, CancellationToken ct = default)
    {
        var moved = await sessions.MoveItemAsync(itemId, by, ct);
        if (moved) Changed?.Invoke(this, EventArgs.Empty);
        return moved;
    }

    public async Task<bool> MoveItemToAsync(long itemId, long sectionId, int index, CancellationToken ct = default)
    {
        var moved = await sessions.MoveItemToAsync(itemId, sectionId, index, ct);
        if (moved) Changed?.Invoke(this, EventArgs.Empty);
        return moved;
    }

    public async Task<SessionSectionInfo> AddSectionAsync(long packId, string name, CancellationToken ct = default)
    {
        var section = await sessions.AddSectionAsync(packId, name, ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return section;
    }

    public async Task RenameSectionAsync(long sectionId, string name, CancellationToken ct = default)
    {
        if (await sessions.RenameSectionAsync(sectionId, name, ct)) Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteSectionAsync(long sectionId, CancellationToken ct = default)
    {
        if (await sessions.DeleteSectionAsync(sectionId, ct)) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Use this page" (choice 14): the item points at these pages of the document the reader has open from now on.</summary>
    public async Task RepointAsync(long itemId, long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        if (await sessions.RepointAsync(itemId, await MakeRangeAsync(documentId, firstPage, lastPage, ct), ct)) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Where each item opens now (choice 14). A page range opens in its entry's current copy: in the same file as it was
    /// added, as it is; in another copy or version, at the pages whose fingerprints match; failing that, in the file it
    /// was added from if that is still there; otherwise at the same printed page of the current copy, marked
    /// <see cref="SessionItemState.Changed"/>. It never jumps somewhere else silently. An item with no file to open
    /// says why (choice 15).
    /// </summary>
    public async Task<IReadOnlyDictionary<long, SessionItemTarget>> ResolveAsync(IReadOnlyList<SessionItemInfo> items, CancellationToken ct = default)
    {
        var result = new Dictionary<long, SessionItemTarget>();
        if (items.Count == 0) return result;
        var current = await CurrentDocumentsAsync([.. items.Select(i => i.EntryId).Distinct()], ct);
        var documents = items.Select(i => i.Range?.DocumentId).Concat(current.Values).OfType<long>().Distinct().ToList();
        var readable = (await library.GetReadableAsync(documents, ct)).ToHashSet();
        foreach (var item in items) result[item.Id] = await ResolveAsync(item, current.GetValueOrDefault(item.EntryId), readable, ct);
        return result;
    }

    async Task<SessionItemTarget> ResolveAsync(SessionItemInfo item, long? current, HashSet<long> readable, CancellationToken ct)
    {
        if (item.Range is not { } range)
        {
            if (current is not { } whole) return new SessionItemTarget(null, 0, 0, SessionItemState.NoFile, "You own this book elsewhere: there's no file to open.");
            return readable.Contains(whole) ? new SessionItemTarget(whole, 0, 0, SessionItemState.Ready) : await UnreachableAsync(whole, ct);
        }
        var span = range.LastPdfPage - range.FirstPdfPage;
        if (current == range.DocumentId || current is null)
        {
            return readable.Contains(range.DocumentId)
                ? new SessionItemTarget(range.DocumentId, range.FirstPdfPage, range.LastPdfPage, SessionItemState.Ready)
                : await UnreachableAsync(range.DocumentId, ct);
        }
        var copy = current.Value;
        if (readable.Contains(copy) && range.FirstFingerprint is { } fingerprint && (await index.FindFingerprintAsync(copy, fingerprint, ct)) is { Count: > 0 } found)
        {
            // A page found more than once (a repeated handout): the one nearest where it was.
            var first = found.MinBy(p => Math.Abs(p - range.FirstPdfPage));
            var last = first + span;
            if (span > 0 && range.LastFingerprint is { } lastPrint && (await index.FindFingerprintAsync(copy, lastPrint, ct)).Where(p => p >= first) is var ends && ends.Any())
                last = ends.MinBy(p => Math.Abs(p - (first + span)));
            return new SessionItemTarget(copy, first, last, SessionItemState.OtherCopy);
        }
        if (readable.Contains(range.DocumentId))
            return new SessionItemTarget(range.DocumentId, range.FirstPdfPage, range.LastPdfPage, SessionItemState.Original,
                "These pages aren't in the book's current copy, so this opens the file they were added from.");
        if (readable.Contains(copy))
        {
            var page = range.FirstLabel is { } label ? await index.FindLabelAsync(copy, label, ct) : null;
            var start = page ?? range.FirstPdfPage;
            return new SessionItemTarget(copy, start, start + span, SessionItemState.Changed, "This page changed: check it.");
        }
        return await UnreachableAsync(copy, ct);
    }

    async Task<SessionItemTarget> UnreachableAsync(long documentId, CancellationToken ct)
    {
        var locations = await library.GetLocationsAsync(documentId, ct);
        var offline = locations.FirstOrDefault(l => l.RootAvailability == SourceRootAvailability.Offline);
        var last = offline ?? (locations.Count > 0 ? locations[0] : null);
        return new SessionItemTarget(null, 0, 0, SessionItemState.Unreachable,
            offline is not null ? "Its folder is offline. It opens again once the folder is back." : "Its file is gone from your library folders.",
            last?.ExplorerPath);
    }

    /// <summary>
    /// The document each entry opens: the one its card shows, or for an image hidden in a pack, its own file (choice 19:
    /// those keep their items and still open). A book owned elsewhere has none.
    /// </summary>
    async Task<Dictionary<EntryId, long?>> CurrentDocumentsAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct)
    {
        var result = (await entries.GetCurrentAsync(entryIds, ct)).ToDictionary(e => e.EntryId, e => e.DocumentId);
        foreach (var missing in entryIds.Where(e => !result.ContainsKey(e)))
            result[missing] = (await entries.GetCopiesAsync(missing, ct)).FirstOrDefault(c => c.IsShown)?.DocumentId;
        return result;
    }

    async Task ProjectAsync(IReadOnlyCollection<EntryId> changed, CancellationToken ct)
    {
        await projector.ProjectMarksAsync([.. changed.Distinct()], ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
