using Bibliotaph.Core.Search;

namespace Bibliotaph.App.Services;

/// <summary>
/// A book to open: from a search hit (with its page and the search, so the words can be highlighted), from the
/// inspector (at the first page), or moving between windows (at the page in view and its zoom). Each reader is made
/// with its request (<see cref="ReaderWindows.Create"/>), so Back returns to the page as it was, not to a fresh request.
/// </summary>
public sealed record ViewerRequest(long DocumentId, string Title, int PageIndex = 0, SearchQuery? Query = null)
{
    /// <summary>The zoom to open at, as the reader's Zoom; null fits the width.</summary>
    public double? Zoom { get; init; }

    /// <summary>For an image in a pack, every image in it in order, for Previous and Next.</summary>
    public IReadOnlyList<PackStep>? Pack { get; init; }

    /// <summary>The pack's title, under the image's.</summary>
    public string? PackTitle { get; init; }

    /// <summary>
    /// An ordinary open, from the Library or Home: the book opens at the page it was left at last time, and the page it
    /// is left at is kept for next time. A search hit opens at its page and keeps nothing.
    /// </summary>
    public bool Resume { get; init; }

    /// <summary>
    /// Keeps the page the book is left at, like <see cref="Resume"/>, but opens at <see cref="PageIndex"/>: the same
    /// read moving to another window.
    /// </summary>
    public bool KeepsPlace { get; init; }

    /// <summary>A session item opened from its pack: it opens at its first page and, like a search hit, keeps no reading position.</summary>
    public SessionItemOpen? SessionItem { get; init; }

    /// <summary>"Ask this book" from a card's menu or the details: the book opens with its Ask panel showing.</summary>
    public bool Ask { get; init; }

    /// <summary>A page note opened at its pages, which may have moved or changed in the book as it is now.</summary>
    public PageNoteOpen? PageNote { get; init; }

    /// <summary>
    /// An adventure inside a file, opened from its card: the file opens at the adventure's first page, its pages stand
    /// out in the Contents panel, and Find looks in them unless told to look in the whole file.
    /// </summary>
    public PartOpen? Part { get; init; }
}

/// <summary>An adventure inside a file being read: its card, the file's title, and its pages in the file that opens.</summary>
public sealed record PartOpen(Bibliotaph.Core.EntryId EntryId, string FileTitle, Bibliotaph.Core.PartPages Pages)
{
    /// <summary>The request that opens it from its card, at its first page.</summary>
    public static ViewerRequest Open(long documentId, string title, Bibliotaph.Core.EntryId entryId, string? fileTitle, Bibliotaph.Core.PartPages pages) =>
        new(documentId, title, pages.First) { Part = new PartOpen(entryId, fileTitle ?? "its file", pages) };
}

/// <summary>
/// A session item being opened: which, from which pack, and how its pages were found, so the reader can say when they
/// are in the original file or changed, and re-point the item with Use this page.
/// </summary>
public sealed record SessionItemOpen(long ItemId, string PackTitle, Bibliotaph.Processing.SessionItemState State, string? Reason, int FirstPage, int LastPage)
{
    /// <summary>The item is a whole book, so the reader's thumbnail strip marks no pages as the item's.</summary>
    public bool WholeBook { get; init; }
}

/// <summary>
/// A page note being opened: which, and how its pages were found, so the reader can say when they changed or a new
/// version is still being read, and re-point the note with Use this page.
/// </summary>
public sealed record PageNoteOpen(long NoteId, Bibliotaph.Processing.PagePlaceState State, string? Reason, int FirstPage, int LastPage);

/// <summary>An image of a pack the viewer can step to.</summary>
public sealed record PackStep(long DocumentId, string Title);

/// <summary>
/// Passwords that opened a book in this sitting, by content hash, so the same book opening in another window (or
/// again in this one) isn't asked for twice, even when the reader didn't tick Remember. Those entered with "Make its
/// text searchable" ticked are lent to indexing too, so the book is read for search while the app runs. Held in memory
/// only and never written anywhere; they go when the app exits.
/// </summary>
public sealed class UnlockedPasswords
{
    readonly Lock _lock = new();
    readonly Dictionary<string, (string Password, bool ForIndexing)> _passwords = [];

    public string? Find(string contentHash)
    {
        lock (_lock) return _passwords.TryGetValue(contentHash, out var entry) ? entry.Password : null;
    }

    /// <summary>The password, if the reader let indexing use it.</summary>
    public string? FindForIndexing(string contentHash)
    {
        lock (_lock) return _passwords.TryGetValue(contentHash, out var entry) && entry.ForIndexing ? entry.Password : null;
    }

    /// <summary>
    /// Keeps a password that opened the book. Opening it again with the same password never takes back the loan to
    /// indexing; a new password replaces the old one, and its loan with it.
    /// </summary>
    public void Add(string contentHash, string password, bool forIndexing = false)
    {
        lock (_lock)
        {
            var kept = _passwords.TryGetValue(contentHash, out var entry) && entry.Password == password && entry.ForIndexing;
            _passwords[contentHash] = (password, forIndexing || kept);
        }
    }

    public void Forget(string contentHash)
    {
        lock (_lock) _passwords.Remove(contentHash);
    }
}
