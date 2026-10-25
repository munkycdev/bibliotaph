using System.Security.Cryptography;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// Covers picked in "Add a book I own elsewhere" before the book exists: read when picked, so a picture that can't be
/// a cover says so at once, as on Windows, and kept here at cover size until Add to library takes it by its token.
/// </summary>
public sealed class PendingCovers(TimeProvider clock)
{
    const int Most = 20;
    static readonly TimeSpan Life = TimeSpan.FromMinutes(30);

    readonly Lock _gate = new();
    readonly Dictionary<string, (byte[] Jpeg, DateTimeOffset Kept)> _covers = new(StringComparer.Ordinal);

    public string Keep(byte[] jpeg)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var old in _covers.Where(c => now - c.Value.Kept > Life).Select(c => c.Key).ToList()) _covers.Remove(old);
            while (_covers.Count >= Most) _covers.Remove(_covers.MinBy(c => c.Value.Kept).Key);
            _covers[token] = (jpeg, now);
        }
        return token;
    }

    /// <summary>The cover kept under a token, taken so it is used once; null when it has gone or never was.</summary>
    public byte[]? Take(string token)
    {
        lock (_gate)
        {
            if (!_covers.Remove(token, out var kept)) return null;
            return clock.GetUtcNow() - kept.Kept > Life ? null : kept.Jpeg;
        }
    }
}
