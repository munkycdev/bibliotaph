using System.Collections.Concurrent;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// What each connected PC's AI says it is doing (<see cref="PcActivity"/>), held in memory only: a PC says again every
/// half minute while it is busy, so after <see cref="ForgetAfter"/> without a word it is taken to be closed or asleep and
/// left out. A restart loses nothing a PC doesn't send again within the half minute.
/// </summary>
public sealed class PcActivityBoard(TimeProvider clock)
{
    /// <summary>Three missed half minutes: long enough for a slow network, short enough that a closed PC soon drops off.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromSeconds(90);

    public sealed record Entry(string DeviceName, PcActivity Activity, DateTimeOffset At);

    readonly ConcurrentDictionary<(string UserId, string DeviceId), Entry> _entries = new();

    public void Report(string userId, string deviceId, string deviceName, PcActivity activity) =>
        _entries[(userId, deviceId)] = new Entry(deviceName, activity, clock.GetUtcNow());

    /// <summary>The PC's AI has nothing left, or the PC was disconnected or is closing.</summary>
    public void Forget(string userId, string deviceId) => _entries.TryRemove((userId, deviceId), out _);

    /// <summary>The user's PCs heard from lately, by name.</summary>
    public IReadOnlyList<Entry> Of(string userId)
    {
        var since = clock.GetUtcNow() - ForgetAfter;
        List<Entry> found = [];
        foreach (var ((user, device), entry) in _entries)
        {
            if (user != userId) continue;
            if (entry.At >= since) found.Add(entry);
            else _entries.TryRemove(KeyValuePair.Create((user, device), entry));
        }
        return [.. found.OrderBy(e => e.DeviceName, StringComparer.CurrentCultureIgnoreCase)];
    }
}
