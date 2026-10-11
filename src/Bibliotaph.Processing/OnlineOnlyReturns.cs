using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// "Return files to online-only after indexing" (Dale's review, row 2): reading an online-only OneDrive file downloads
/// it, so a cloud library bigger than the disk would fill it. Each file that was online-only when Bibliotaph read it is
/// marked, and goes back to online-only once its stages are done with it, so the space comes back and indexing carries
/// on. Never a file that was already on this device, one the user chose to always keep there, or one open in the
/// reader; opening a book afterwards downloads it, as Windows always does. On unless the user turns it off.
/// </summary>
public sealed class OnlineOnlyReturns(LibraryStore library, JobBoard queue, SettingsStore settings, ICloudFiles cloud, SourceFiles sources,
    ILogger<OnlineOnlyReturns>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<OnlineOnlyReturns>.Instance;

    /// <summary>Raised when the setting changes, so files waiting to go back are looked at again.</summary>
    public event EventHandler? SettingChanged;

    public async Task<bool> IsOnAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.ReturnOnlineOnly, ct) != bool.FalseString;

    public async Task SetOnAsync(bool on, CancellationToken ct = default)
    {
        await settings.SetAsync(SettingKeys.ReturnOnlineOnly, on.ToString(), ct);
        SettingChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Called by the hasher just before it reads a file: one that is online-only now is about to be downloaded by
    /// Bibliotaph, so it is marked to go back. A file inside a ZIP goes back with its ZIP. Marked whether or not the
    /// setting is on, so turning it on later gives back what indexing downloaded meanwhile.
    /// </summary>
    public async Task BeforeReadAsync(UnhashedFile file, CancellationToken ct = default)
    {
        if (file.EntryPath is null && cloud.StateOf(file.FullPath) == CloudFileState.OnlineOnly) await library.MarkToReturnAsync(file.LocationId, ct);
    }

    /// <summary>
    /// Gives back every marked file whose documents are done with it: none of their jobs for <paramref name="holding"/>
    /// (normally <see cref="Pipeline.FileStages"/>) is waiting or running. One open in the reader waits for the next
    /// look; one the user has since pinned, or that isn't a cloud file after all, stays and is no longer marked.
    /// </summary>
    /// <returns>How many files went back.</returns>
    public async Task<int> ReturnDoneAsync(IReadOnlyCollection<Stage> holding, CancellationToken ct = default)
    {
        if (!await IsOnAsync(ct)) return 0;
        var waiting = await library.ListToReturnAsync(ct);
        if (waiting.Count == 0) return 0;
        var done = await queue.DoneReadingAsync([.. waiting.SelectMany(w => w.DocumentIds)], holding, ct);
        var (returned, bytes) = (0, 0L);
        foreach (var file in waiting)
        {
            ct.ThrowIfCancellationRequested();
            if (!file.DocumentIds.All(done.Contains) || sources.IsInUse(file.FullPath)) continue;
            switch (cloud.StateOf(file.FullPath))
            {
                case CloudFileState.Pinned:
                    _log.LogInformation("Keeping location {LocationId} on this device, as its owner chose", file.LocationId);
                    await library.UnmarkToReturnAsync(file.LocationId, ct);
                    continue;
                case CloudFileState.OnlineOnly:
                    // Gone back already, as when the user chose Free up space.
                    await library.ReturnedToOnlineOnlyAsync(file.LocationId, ct);
                    continue;
                case CloudFileState.Unknown:
                    // Gone or unreachable: the next scan says which.
                    continue;
            }
            try
            {
                cloud.ReturnToOnlineOnly(file.FullPath);
            }
            catch (IOException ex)
            {
                _log.LogWarning("Location {LocationId} couldn't go back to online-only, so it stays on this device: {Reason}", file.LocationId, ex.Message);
                await library.UnmarkToReturnAsync(file.LocationId, ct);
                continue;
            }
            await library.ReturnedToOnlineOnlyAsync(file.LocationId, ct);
            returned++;
            bytes += file.SizeBytes;
        }
        if (returned > 0) _log.LogInformation("Returned {Count} files to online-only, {Bytes} bytes", returned, bytes);
        return returned;
    }
}
