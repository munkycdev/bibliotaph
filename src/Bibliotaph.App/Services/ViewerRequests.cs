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
}

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
