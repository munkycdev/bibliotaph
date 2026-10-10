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

    /// <summary>For an image in a pack, every image in it in order, for Previous and Next (F4 plan, choice 7).</summary>
    public IReadOnlyList<PackStep>? Pack { get; init; }

    /// <summary>The pack's title, under the image's.</summary>
    public string? PackTitle { get; init; }

    /// <summary>
    /// An ordinary open, from the Library or Home (slice 3 plan, choice 6): the book opens at the page it was left at
    /// last time, and the page it is left at is kept for next time. A search hit opens at its page and keeps nothing.
    /// </summary>
    public bool Resume { get; init; }

    /// <summary>
    /// Keeps the page the book is left at, like <see cref="Resume"/>, but opens at <see cref="PageIndex"/>: the same
    /// read moving to another window.
    /// </summary>
    public bool KeepsPlace { get; init; }

    /// <summary>A session item opened from its pack: it opens at its first page and, like a search hit, keeps no reading position.</summary>
    public SessionItemOpen? SessionItem { get; init; }
}

/// <summary>
/// A session item being opened (slice 3 plan, choice 14): which, from which pack, and how its pages were found, so the
/// reader can say when they are in the original file or changed, and re-point the item with Use this page.
/// </summary>
public sealed record SessionItemOpen(long ItemId, string PackTitle, Bibliotaph.Processing.SessionItemState State, string? Reason, int FirstPage, int LastPage);

/// <summary>An image of a pack the viewer can step to.</summary>
public sealed record PackStep(long DocumentId, string Title);

/// <summary>
/// Passwords that opened a book in this sitting, by content hash, so the same book opening in another window (or
/// again in this one) isn't asked for twice, even when the reader didn't tick Remember. Held in memory only and never
/// written anywhere; they go when the app exits.
/// </summary>
public sealed class UnlockedPasswords
{
    readonly Lock _lock = new();
    readonly Dictionary<string, string> _passwords = [];

    public string? Find(string contentHash)
    {
        lock (_lock) return _passwords.GetValueOrDefault(contentHash);
    }

    public void Add(string contentHash, string password)
    {
        lock (_lock) _passwords[contentHash] = password;
    }

    public void Forget(string contentHash)
    {
        lock (_lock) _passwords.Remove(contentHash);
    }
}
