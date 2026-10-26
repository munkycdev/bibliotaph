using Bibliotaph.Core.Sync;

namespace Bibliotaph.Processing;

/// <summary>Other devices' sync files as a channel had them, and what to show when some couldn't be had.</summary>
public sealed record SyncChannelRead(IReadOnlyList<SyncFileContent> Peers, string? Problem);

/// <summary>
/// A way, besides a folder, for library sync to swap sync files with other devices: the web library, for a PC
/// connected to it, or the server's own store of its devices' files, for the web library itself. The files and the
/// merge are the same as through a folder; only where they are kept differs.
/// </summary>
public interface ISyncChannel
{
    /// <summary>Whether the channel is in use now, as when the PC is connected to the web library.</summary>
    bool IsOn { get; }

    /// <summary>The other devices' files. One that can't be had now is left out, with a problem to show.</summary>
    Task<SyncChannelRead> ReadPeersAsync(CancellationToken ct);

    /// <summary>Writes this device's file, as <paramref name="write"/> writes it to a stream. Returns a problem to show, or null.</summary>
    Task<string?> WriteOwnAsync(Func<Stream, CancellationToken, Task> write, CancellationToken ct);

    /// <summary>Whether another device's file changed since it was last read: asked every minute, so it must be cheap.</summary>
    Task<bool> HasChangesAsync(CancellationToken ct);

    /// <summary>Raised when another device's file changed, for a channel that knows at once.</summary>
    event EventHandler? Changed;
}
