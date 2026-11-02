using System.Collections.Concurrent;
using Bibliotaph.Processing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Storage.Desktop;

/// <summary>Lists library folders in connected storage through their drives, and every other folder as before.</summary>
public sealed class StorageAwareSourceLister(ISourceLister storage, ISourceLister other) : ISourceLister
{
    public Task<SourceListing?> ListAsync(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct) =>
        StoragePath.IsStoragePath(root)
            ? storage.ListAsync(root, otherRoots, ct)
            : other.ListAsync(root, [.. otherRoots.Where(r => !StoragePath.IsStoragePath(r))], ct);
}

/// <summary>Watches library folders in connected storage through their change logs, and every other folder as before.</summary>
public sealed class StorageAwareFolderWatcher(StorageChangeFeed storage, IFolderWatcher other) : IFolderWatcher
{
    public IDisposable Watch(string folder, Action changed, Action stopped) =>
        StoragePath.IsStoragePath(folder) ? storage.Watch(folder, changed, stopped) : other.Watch(folder, changed, stopped);
}

/// <summary>
/// A folder in connected storage is on no disk of this PC's, and its files have no NTFS IDs: moves there are found by
/// name, size and time, or by content, as on any disk without IDs. A storage path must never reach Windows, where
/// "/onedrive/..." would be a folder on the current drive.
/// </summary>
public sealed class StorageAwareFileIdentity(IFileIdentity other) : IFileIdentity
{
    public VolumeIdentity? Volume(string folder) => StoragePath.IsStoragePath(folder) ? null : other.Volume(folder);

    public string? FileId(string path) => StoragePath.IsStoragePath(path) ? null : other.FileId(path);
}

/// <summary>
/// Asks each drive connected on this PC whether anything in it changed, every five minutes while the app is open, as a
/// PC can't be sent the providers' notifications. Where each drive stood is kept in memory: the scan at every start
/// finds what changed while the app was closed. <see cref="CheckNowAsync"/> asks at once, for Look for changes.
/// </summary>
public sealed class PcStorageChanges(PcDrives drives, StorageChangeFeed feed, TimeProvider? clock = null, ILogger<PcStorageChanges>? log = null) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<PcStorageChanges>.Instance;
    readonly ConcurrentDictionary<string, string> _cursors = new();
    readonly SemaphoreSlim _round = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await drives.ReloadAsync(stoppingToken);
        await CheckNowAsync(stoppingToken);
        using var timer = new PeriodicTimer(Every, _clock);
        while (await timer.WaitForNextTickAsync(stoppingToken)) await CheckNowAsync(stoppingToken);
    }

    /// <summary>One look at every connected drive's changes; a drive asked for the first time only says where it stands.</summary>
    public async Task CheckNowAsync(CancellationToken ct)
    {
        await _round.WaitAsync(ct);
        try
        {
            var all = drives.All.Where(d => !d.Connection.NeedsReconnect && d.Changes is not null).ToList();
            foreach (var gone in _cursors.Keys.Except(all.Select(d => d.Connection.Id))) _cursors.TryRemove(gone, out _);
            foreach (var drive in all)
            {
                try
                {
                    if (!_cursors.TryGetValue(drive.Connection.Id, out var since))
                    {
                        _cursors[drive.Connection.Id] = await drive.Changes!.StartAsync(ct);
                        continue;
                    }
                    var (changed, next) = await drive.Changes!.ChangesSinceAsync(since, ct);
                    _cursors[drive.Connection.Id] = next;
                    if (changed) feed.Notify(drive.Connection.Provider, drive.Connection.AccountId);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or System.Xml.XmlException or System.Text.Json.JsonException)
                {
                    _log.LogInformation("Couldn't check {Provider} changes: {Reason}", drive.Connection.Provider, ex.Message);
                }
            }
        }
        finally
        {
            _round.Release();
        }
    }
}
