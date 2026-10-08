using System.Collections.Concurrent;

namespace Bibliotaph.Pdf.Host;

/// <summary>
/// A page of a file. <see cref="PageIndex"/> is -1 for work on the file as a whole (open, whole-book find).
/// </summary>
public sealed record PageKey(string Path, int PageIndex)
{
    public const int WholeFile = -1;

    public bool Equals(PageKey? other) =>
        other is not null && PageIndex == other.PageIndex && StringComparer.OrdinalIgnoreCase.Equals(Path, other.Path);

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Path), PageIndex);
}

/// <summary>
/// Remembers which pages killed a worker (crash or timeout). After <see cref="KillsToPoison"/> kills the page
/// is poisoned and refused, so one bad book cannot loop the queue (acceptance scenario A18).
/// In memory for now; slice 1 persists it with the job queue.
/// </summary>
public sealed class PoisonTracker
{
    public const int KillsToPoison = 2;

    readonly ConcurrentDictionary<PageKey, int> _kills = new();

    /// <returns>The number of kills recorded for this page so far.</returns>
    public int RecordKill(PageKey page) => _kills.AddOrUpdate(page, 1, (_, kills) => kills + 1);

    public bool IsPoisoned(PageKey page) => _kills.TryGetValue(page, out var kills) && kills >= KillsToPoison;

    public int KillCount(PageKey page) => _kills.GetValueOrDefault(page);
}
