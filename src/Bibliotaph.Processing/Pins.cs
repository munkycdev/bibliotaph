using Bibliotaph.Catalog;

namespace Bibliotaph.Processing;

/// <summary>
/// The sidebar's Pinned group (Pinned Views plan): pins and unpins, and says so. Pins also change when their targets
/// are deleted or brought back, which the services for those targets report.
/// </summary>
public sealed class PinsService(PinStore pins)
{
    /// <summary>Raised after a pin was added, removed or moved here, on the thread that changed it.</summary>
    public event EventHandler? Changed;

    public Task<IReadOnlyList<PinInfo>> ListAsync(CancellationToken ct = default) => pins.ListAsync(ct);

    /// <summary>Pins at the bottom of the list. Returns false if it was pinned already.</summary>
    public async Task<bool> PinAsync(PinTarget target, CancellationToken ct = default)
    {
        var pinned = await pins.PinAsync(target, ct);
        if (pinned) Changed?.Invoke(this, EventArgs.Empty);
        return pinned;
    }

    /// <summary>Moves the pin to that place in the list. Returns false if it isn't pinned or is already there.</summary>
    public async Task<bool> MoveAsync(PinTarget target, int index, CancellationToken ct = default)
    {
        var moved = await pins.MoveAsync(target, index, ct);
        if (moved) Changed?.Invoke(this, EventArgs.Empty);
        return moved;
    }

    /// <summary>Unpins. Returns false if it wasn't pinned.</summary>
    public async Task<bool> UnpinAsync(PinTarget target, CancellationToken ct = default)
    {
        var unpinned = await pins.UnpinAsync(target, ct) is not null;
        if (unpinned) Changed?.Invoke(this, EventArgs.Empty);
        return unpinned;
    }
}
