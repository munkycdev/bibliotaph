using System.Security.Cryptography;
using Bibliotaph.Processing;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// The library pages a user uploaded to import from, each with the images given for its covers, kept in memory while
/// the preview is looked at: a few at a time, for two hours. Nothing of a page outlives its import but what the
/// import keeps.
/// </summary>
public sealed class HeldOnlinePages(TimeProvider clock)
{
    const int Most = 3;
    static readonly TimeSpan Life = TimeSpan.FromHours(2);

    /// <summary>The most image bytes kept for one page's covers.</summary>
    public const long MostImageBytes = 64L * 1024 * 1024;

    readonly Lock _gate = new();
    readonly Dictionary<string, HeldPage> _pages = new(StringComparer.Ordinal);

    /// <summary>A page as read, and the images given for it by file name.</summary>
    public sealed class HeldPage(IOnlineLibraryReader reader, OnlinePage page, DateTimeOffset kept)
    {
        readonly Dictionary<string, byte[]> _images = new(StringComparer.OrdinalIgnoreCase);
        long _bytes;

        public IOnlineLibraryReader Reader { get; } = reader;
        public OnlinePage Page { get; } = page;
        public DateTimeOffset Kept { get; } = kept;

        /// <summary>Keeps an image by its name; false when the page's images are already as many bytes as are kept.</summary>
        public bool AddImage(string name, byte[] bytes)
        {
            lock (_images)
            {
                var replaced = _images.TryGetValue(name, out var old) ? old.Length : 0;
                if (_bytes - replaced + bytes.Length > MostImageBytes) return false;
                _images[name] = bytes;
                _bytes += bytes.Length - replaced;
                return true;
            }
        }

        public bool HasImage(string name)
        {
            lock (_images) return _images.ContainsKey(name);
        }

        /// <summary>The covers as the import asks for them: each looked up by its file name among the images given.</summary>
        public Task<byte[]?> CoverAsync(string url, CancellationToken ct)
        {
            if (OnlineImports.CoverFileName(url) is not { } name) return Task.FromResult<byte[]?>(null);
            lock (_images) return Task.FromResult(_images.GetValueOrDefault(name));
        }
    }

    public string Keep(IOnlineLibraryReader reader, OnlinePage page)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var old in _pages.Where(p => now - p.Value.Kept > Life).Select(p => p.Key).ToList()) _pages.Remove(old);
            while (_pages.Count >= Most) _pages.Remove(_pages.MinBy(p => p.Value.Kept).Key);
            _pages[token] = new HeldPage(reader, page, now);
        }
        return token;
    }

    /// <summary>The page kept under a token, or null when it has gone or never was.</summary>
    public HeldPage? Find(string token)
    {
        lock (_gate)
        {
            if (!_pages.TryGetValue(token, out var held)) return null;
            if (clock.GetUtcNow() - held.Kept <= Life) return held;
            _pages.Remove(token);
            return null;
        }
    }
}
