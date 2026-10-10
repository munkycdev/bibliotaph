using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Image packs (F4 plan): makes folders and ZIPs of many images one card after hashing, proposes smaller and mixed
/// ones in Needs review, and splits packs again on request. Each change stores the packs' hints from their folder
/// names and projects the cards that changed, so the library shows them.
/// </summary>
public sealed class PackService(PackStore packs, MetadataProjector projector, MetadataHints hints, ILogger<PackService>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<PackService>.Instance;

    /// <summary>Packs and proposes what should be (<see cref="PackStore.PlanAsync"/>) and projects the cards that changed.</summary>
    public async Task PlanAsync(CancellationToken ct = default)
    {
        var changed = await packs.PlanAsync(ct);
        if (changed.Count == 0) return;
        _log.LogInformation("Packed images: {Count} cards changed", changed.Count);
        await ProjectAsync(changed, ct);
    }

    /// <summary>"Split into separate images": every image gets its card back. False for an entry that isn't a pack.</summary>
    public async Task<bool> SplitAsync(EntryId packId, CancellationToken ct = default)
    {
        if (await packs.SplitAsync(packId, ct) is not { } changed) return false;
        await ProjectAsync(changed, ct);
        return true;
    }

    /// <summary>Undoes a split: the folder or ZIP is one card again.</summary>
    public async Task RepackAsync(EntryId packId, CancellationToken ct = default) => await ProjectAsync(await packs.RepackAsync(packId, ct), ct);

    /// <summary>
    /// Answers a proposal in Needs review: <see cref="PackAnswer.Packed"/> for Make a pack, <see cref="PackAnswer.Split"/>
    /// for Keep separate. Returns the answer it had before, which <see cref="UndoAsync"/> puts back; null when the
    /// folder isn't waiting for an answer any more (it grew into a pack by itself meanwhile).
    /// </summary>
    public async Task<PackAnswer?> AnswerAsync(EntryId packId, PackAnswer answer, CancellationToken ct = default)
    {
        if (await packs.AnswerAsync(packId, answer, PackAnswer.Proposed, ct) is not { } change) return null;
        await ProjectAsync(change.Changed, ct);
        return change.Previous;
    }

    /// <summary>Undoes <see cref="AnswerAsync"/>: the folder is asked about again.</summary>
    public async Task UndoAsync(EntryId packId, PackAnswer previous, CancellationToken ct = default)
    {
        if (await packs.AnswerAsync(packId, previous, ct: ct) is { } change) await ProjectAsync(change.Changed, ct);
    }

    public Task<IReadOnlyList<PackProposal>> GetProposalsAsync(CancellationToken ct = default) => packs.GetProposalsAsync(ct);

    public Task<PackPlace?> GetPlaceAsync(EntryId packId, CancellationToken ct = default) => packs.GetPlaceAsync(packId, ct);

    async Task ProjectAsync(IReadOnlyList<EntryId> changed, CancellationToken ct)
    {
        await hints.StorePacksAsync(changed, ct);
        await projector.ProjectAsync(changed, ct);
    }
}
