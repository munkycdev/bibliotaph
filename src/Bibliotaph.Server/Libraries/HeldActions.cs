using System.Security.Cryptography;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// What the web client can ask for once, by a token it was given: an Undo, or Change them on an offer to correct the
/// look-alikes of a book. The Windows app keeps these in its view models; the browser keeps only the token, so the
/// action itself stays on the server, for a while, with the most recent few.
/// </summary>
public sealed class HeldActions(TimeProvider clock)
{
    const int Most = 50;
    static readonly TimeSpan Life = TimeSpan.FromMinutes(30);

    readonly Lock _gate = new();
    readonly Dictionary<string, (Func<CancellationToken, Task<ActionResult>> Run, DateTimeOffset Held)> _held = new(StringComparer.Ordinal);

    /// <summary>Keeps an action, which returns what to tell the user once it has run and its own Undo, and gives its token.</summary>
    public string Hold(Func<CancellationToken, Task<ActionResult>> action)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            foreach (var old in _held.Where(h => now - h.Value.Held > Life).Select(h => h.Key).ToList()) _held.Remove(old);
            while (_held.Count >= Most) _held.Remove(_held.MinBy(h => h.Value.Held).Key);
            _held[token] = (action, now);
        }
        return token;
    }

    /// <summary>The action held under a token, taken so it can run only once; null when it has gone or never was.</summary>
    public Func<CancellationToken, Task<ActionResult>>? Take(string token)
    {
        lock (_gate)
        {
            if (!_held.Remove(token, out var held)) return null;
            return clock.GetUtcNow() - held.Held > Life ? null : held.Run;
        }
    }
}
