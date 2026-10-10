using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Image packs (F4 plan): makes folders and ZIPs of many images one card after hashing, and splits them again on
/// request, projecting the cards that changed so the library shows them.
/// </summary>
public sealed class PackService(PackStore packs, MetadataProjector projector, ILogger<PackService>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<PackService>.Instance;

    /// <summary>Packs what should be packed (<see cref="PackStore.PlanAsync"/>) and projects the cards that changed.</summary>
    public async Task PlanAsync(CancellationToken ct = default)
    {
        var changed = await packs.PlanAsync(ct);
        if (changed.Count == 0) return;
        _log.LogInformation("Packed images: {Count} cards changed", changed.Count);
        await projector.ProjectAsync(changed, ct);
    }

    /// <summary>"Split into separate images": every image gets its card back. False for an entry that isn't a pack.</summary>
    public async Task<bool> SplitAsync(EntryId packId, CancellationToken ct = default)
    {
        if (await packs.SplitAsync(packId, ct) is not { } changed) return false;
        await projector.ProjectAsync(changed, ct);
        return true;
    }

    /// <summary>Undoes a split: the folder or ZIP is one card again.</summary>
    public async Task RepackAsync(EntryId packId, CancellationToken ct = default) =>
        await projector.ProjectAsync(await packs.RepackAsync(packId, ct), ct);

    public Task<PackPlace?> GetPlaceAsync(EntryId packId, CancellationToken ct = default) => packs.GetPlaceAsync(packId, ct);
}
