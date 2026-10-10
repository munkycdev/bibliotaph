using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// How a session item will open, worked out each time its pack is shown (slice 3 plan, choices 14 and 15) from what
/// was found when a new version was read (slice 4h plan, choice 4).
/// </summary>
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

    /// <summary>
    /// A new version of the book is still being read (slice 4h plan, choice 1): its pages haven't been looked for yet,
    /// so nothing is judged changed. It opens at the same printed page meanwhile.
    /// </summary>
    Pending,

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
/// they are made, and are looked for in a new version of the book once it has been read (<see cref="PagePlaces"/>).
/// </summary>
public sealed class SessionsService(SessionStore sessions, EntryStore entries, LibraryStore library, IndexQueries index, MetadataProjector projector,
    PagePlaces places)
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
    public Task<PageRange> MakeRangeAsync(long documentId, int firstPage, int lastPage, CancellationToken ct = default) =>
        places.MakeRangeAsync(documentId, firstPage, lastPage, ct);

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

    /// <summary>"Use this page" (choice 14): the item points at these pages of the document the reader has open from now on, checked.</summary>
    public async Task RepointAsync(long itemId, long documentId, int firstPage, int lastPage, CancellationToken ct = default)
    {
        if (await sessions.RepointAsync(itemId, await MakeRangeAsync(documentId, firstPage, lastPage, ct), ct)) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Where each item opens now (choice 14; slice 4h plan, choices 1 and 2). A page range opens in its entry's current
    /// copy: in the same file as it was added, as it is; in another copy or version, at the pages whose fingerprints
    /// match; failing that, in the file it was added from if that is still there; otherwise at the same printed page of
    /// the current copy, marked <see cref="SessionItemState.Changed"/>. A new version still being read isn't judged
    /// yet. It never jumps somewhere else silently. An item with no file to open says why (choice 15).
    /// </summary>
    public async Task<IReadOnlyDictionary<long, SessionItemTarget>> ResolveAsync(IReadOnlyList<SessionItemInfo> items, CancellationToken ct = default)
    {
        var result = new Dictionary<long, SessionItemTarget>();
        if (items.Count == 0) return result;
        var ranged = items.Where(i => i.Range is not null).ToList();
        var resolved = await places.ResolveAsync([.. ranged.Select(i => (i.EntryId, i.Range!))], ct);
        for (var i = 0; i < ranged.Count; i++) result[ranged[i].Id] = ToTarget(resolved[i]);
        var wholes = items.Where(i => i.Range is null).ToList();
        if (wholes.Count == 0) return result;
        var current = await places.CurrentDocumentsAsync([.. wholes.Select(i => i.EntryId).Distinct()], ct);
        var readable = (await library.GetReadableAsync([.. current.Values.OfType<long>().Distinct()], ct)).ToHashSet();
        foreach (var item in wholes)
        {
            result[item.Id] = current.GetValueOrDefault(item.EntryId) is not { } whole
                ? new SessionItemTarget(null, 0, 0, SessionItemState.NoFile, "You own this book elsewhere: there's no file to open.")
                : readable.Contains(whole) ? new SessionItemTarget(whole, 0, 0, SessionItemState.Ready) : ToTarget(await places.UnreachableAsync(whole, ct));
        }
        return result;
    }

    static SessionItemTarget ToTarget(PagePlace place) => new(place.DocumentId, place.FirstPage, place.LastPage, place.State switch
    {
        PagePlaceState.Ready => SessionItemState.Ready,
        PagePlaceState.OtherCopy => SessionItemState.OtherCopy,
        PagePlaceState.Original => SessionItemState.Original,
        PagePlaceState.Changed => SessionItemState.Changed,
        PagePlaceState.Pending => SessionItemState.Pending,
        _ => SessionItemState.Unreachable,
    }, place.Reason, place.LastPath);

    async Task ProjectAsync(IReadOnlyCollection<EntryId> changed, CancellationToken ct)
    {
        await projector.ProjectMarksAsync([.. changed.Distinct()], ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
