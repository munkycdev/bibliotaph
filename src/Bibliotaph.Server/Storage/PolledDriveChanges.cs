using System.Collections.Concurrent;
using Bibliotaph.Storage;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Asks each connected drive whose provider sends no notifications (Google Drive, WebDAV) every five minutes whether
/// anything changed since it last asked: Drive's push notifications need a renewed channel per drive, and a short
/// question is cheaper than keeping them. Where each drive stood is kept in memory; after a restart, the twice-daily look
/// through every library finds what changed meanwhile.
/// </summary>
public sealed class PolledDriveChanges(StorageConnections connections, UserDriveFactory drives, DriveNotifications notifications, TimeProvider clock,
    ILogger<PolledDriveChanges> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<string, string> _cursors = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RoundAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Checking drives for changes failed");
            }
        }
    }

    /// <summary>One look at every such drive's changes.</summary>
    public async Task RoundAsync(CancellationToken ct)
    {
        var all = (await connections.AllAsync(ct)).Where(c => !c.NeedsReconnect).ToList();
        foreach (var gone in _cursors.Keys.Except(all.Select(c => c.Id))) _cursors.TryRemove(gone, out _);
        foreach (var connection in all)
        {
            if (drives.Drive(StorageConnections.Summary(connection)) is not IStorageChangeLog changes) continue;
            try
            {
                if (!_cursors.TryGetValue(connection.Id, out var since))
                {
                    _cursors[connection.Id] = await changes.StartAsync(ct);
                    continue;
                }
                var (changed, next) = await changes.ChangesSinceAsync(since, ct);
                _cursors[connection.Id] = next;
                if (changed) notifications.ChangedIn(connection);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or System.Xml.XmlException)
            {
                logger.LogInformation("Couldn't check {Provider} changes for {Connection}: {Reason}", connection.Provider, connection.Id, ex.Message);
            }
        }
    }
}
