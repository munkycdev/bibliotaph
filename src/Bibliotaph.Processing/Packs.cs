using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>What Pack together did, with the pack's title from before when the dialog's name replaced it, for Undo.</summary>
public sealed record PackTogetherResult(PackTogether Together, FieldSnapshot? Title);

/// <summary>
/// Image packs: makes folders and ZIPs of many images one card after hashing, proposes smaller and mixed ones in
/// Needs review, and splits packs again on request; Pack together makes and merges packs by hand. Each change stores
/// the packs' hints from their folder names and projects the cards that changed, so the library shows them.
/// </summary>
public sealed class PackService(PackStore packs, MetadataProjector projector, MetadataHints hints, ILogger<PackService>? log = null,
    MetadataStore? metadata = null)
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

    /// <summary>
    /// "Split into separate images": every image gets its card back. Returns the images, which
    /// <see cref="RepackAsync"/> puts back, or null for an entry that isn't a pack.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>?> SplitAsync(EntryId packId, CancellationToken ct = default)
    {
        if (await packs.SplitAsync(packId, ct) is not { } changed) return null;
        await ProjectAsync(changed, ct);
        return [.. changed.Skip(1)];
    }

    /// <summary>Undoes a split: the pack is one card again, with its folders' images and <paramref name="images"/>.</summary>
    public async Task RepackAsync(EntryId packId, IReadOnlyCollection<EntryId> images, CancellationToken ct = default) =>
        await ProjectAsync(await packs.RepackAsync(packId, images, ct), ct);

    /// <summary>
    /// Pack together: the cards become one pack named <paramref name="name"/>. The name is set as the pack's title,
    /// as the user's own, when the pack is new or merged, or when it was renamed in the dialog. Null when the cards
    /// can't go together.
    /// </summary>
    public async Task<PackTogetherResult?> PackTogetherAsync(IReadOnlyCollection<EntryId> entryIds, string name, CancellationToken ct = default)
    {
        if (await packs.PackTogetherAsync(entryIds, ct) is not { } together) return null;
        FieldSnapshot? title = null;
        name = name.Trim();
        if (metadata is not null && name.Length > 0)
        {
            var current = (await metadata.GetAsync(together.PackId, ct)).Compute()[MetadataFields.Title].First;
            // A hint can give way to another pack's after a merge, so a merged pack's name is made the user's own.
            if (together.Created || together.Merged.Count > 0 || current is null || current.Value != name)
            {
                title = await metadata.SnapshotAsync(together.PackId, MetadataFields.Title, ct);
                await metadata.SetValuesAsync(together.PackId, MetadataFields.Title, [name], ct);
            }
        }
        await ProjectAsync(together.Changed, ct);
        return new PackTogetherResult(together, title);
    }

    /// <summary>Undoes <see cref="PackTogetherAsync"/>: the cards are as they were, and so is the pack's title.</summary>
    public async Task UndoPackTogetherAsync(PackTogetherResult result, CancellationToken ct = default)
    {
        var changed = await packs.UndoPackTogetherAsync(result.Together, ct);
        if (result.Title is { } title && metadata is not null) await metadata.RestoreAsync(title, ct);
        await ProjectAsync(changed, ct);
    }

    /// <summary>The folders and ZIPs a pack's images are in, for its details.</summary>
    public Task<IReadOnlyList<PackFolder>> GetFoldersAsync(EntryId packId, CancellationToken ct = default) => packs.GetFoldersAsync(packId, ct);

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
