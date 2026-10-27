using System.Security.Cryptography;
using Bibliotaph.Processing;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// The library pages a user uploaded to import from, each with the images given for its covers, kept in memory while
/// the preview is looked at: a few at a time, for two hours. Nothing of a page outlives its import but what the
/// import keeps. The pictures count against everyone's <see cref="UploadMemory"/> for as long as they're kept.
/// </summary>
public sealed class HeldOnlinePages(TimeProvider clock, UploadMemory? memory = null)
{
    const int Most = 3;
    static readonly TimeSpan Life = TimeSpan.FromHours(2);

    /// <summary>The most image bytes kept for one page's covers.</summary>
    public const long MostImageBytes = 64L * 1024 * 1024;

    readonly Lock _gate = new();
    readonly Dictionary<string, HeldPage> _pages = new(StringComparer.Ordinal);

    /// <summary>A page as read, and the images given for it by file name.</summary>
    public sealed class HeldPage(IOnlineLibraryReader reader, OnlinePage page, DateTimeOffset kept, UploadMemory? memory)
    {
        readonly Dictionary<string, byte[]> _images = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, UploadMemory.Reservation> _reserved = new(StringComparer.OrdinalIgnoreCase);
        long _bytes;

        public IOnlineLibraryReader Reader { get; } = reader;
        public OnlinePage Page { get; } = page;
        public DateTimeOffset Kept { get; } = kept;

        /// <summary>Keeps an image by its name; or says why not, when the page's images or everyone's uploads are already as much as is kept.</summary>
        public string? AddImage(string name, byte[] bytes)
        {
            lock (_images)
            {
                var replaced = _images.TryGetValue(name, out var old) ? old.Length : 0;
                if (_bytes - replaced + bytes.Length > MostImageBytes) return TooManyImages;
                if (memory is not null)
                {
                    // The picture it replaces gives its memory back first.
                    if (_reserved.Remove(name, out var previous)) previous.Dispose();
                    if (memory.TryReserve(bytes.Length, Kept + Life) is not { } reservation)
                    {
                        if (old is not null && memory.TryReserve(old.Length, Kept + Life) is { } kept) _reserved[name] = kept;
                        return UploadMemory.Busy;
                    }
                    _reserved[name] = reservation;
                }
                _images[name] = bytes;
                _bytes += bytes.Length - replaced;
                return null;
            }
        }

        /// <summary>Lets go of the pictures' memory, once the page has gone.</summary>
        public void Release()
        {
            lock (_images)
            {
                foreach (var reservation in _reserved.Values) reservation.Dispose();
                _reserved.Clear();
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

    public const string TooManyImages = "That's as many pictures as one page keeps.";

    public string Keep(IOnlineLibraryReader reader, OnlinePage page)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var old in _pages.Where(p => now - p.Value.Kept > Life).Select(p => p.Key).ToList()) Remove(old);
            while (_pages.Count >= Most) Remove(_pages.MinBy(p => p.Value.Kept).Key);
            _pages[token] = new HeldPage(reader, page, now, memory);
        }
        return token;
    }

    void Remove(string token)
    {
        if (_pages.Remove(token, out var page)) page.Release();
    }

    /// <summary>The page kept under a token, or null when it has gone or never was.</summary>
    public HeldPage? Find(string token)
    {
        lock (_gate)
        {
            if (!_pages.TryGetValue(token, out var held)) return null;
            if (clock.GetUtcNow() - held.Kept <= Life) return held;
            Remove(token);
            return null;
        }
    }
}
