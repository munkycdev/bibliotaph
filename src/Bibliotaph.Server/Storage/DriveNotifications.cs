using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Storage.OneDrive;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Graph's change notifications, and the subscriptions that ask for them. Graph wants an answer within seconds, so a
/// notification is only checked and queued here; the library is opened and scanned in the background.
/// </summary>
public sealed class DriveNotifications(StorageConnections connections, UserDriveFactory drives, UserLibraries libraries,
    IOptions<ServerOptions> options, ILogger<DriveNotifications> log) : BackgroundService
{
    readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });

    abstract record Work;
    sealed record Changed(string UserId, string DriveId) : Work;
    sealed record SubscribeTo(string ConnectionId) : Work;

    /// <summary>Asks for change notifications on a newly connected drive, in the background.</summary>
    public void Subscribe(string connectionId) => _work.Writer.TryWrite(new SubscribeTo(connectionId));

    /// <summary>Reads Graph's notification body: each entry names its subscription and carries the connection's secret.</summary>
    public async Task ReceiveAsync(Stream body, CancellationToken ct)
    {
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        if (!json.RootElement.TryGetProperty("value", out var values)) return;
        foreach (var value in values.EnumerateArray())
        {
            var state = value.TryGetProperty("clientState", out var s) ? s.GetString() : null;
            if (ClientState.Parse(state) is not { } parsed) continue;
            var connection = await connections.FindAsync(parsed.ConnectionId, ct);
            if (connection is null || !ClientState.Matches(connection, parsed.Secret))
            {
                log.LogWarning("Ignored a change notification that didn't match its connection");
                continue;
            }
            _work.Writer.TryWrite(new Changed(connection.UserId, connection.AccountId));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A burst of notifications for one drive becomes one scan; the scanner's own quiet period does the rest.
        await foreach (var work in _work.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                switch (work)
                {
                    case Changed changed:
                        await libraries.DriveChangedAsync(changed.UserId, changed.DriveId, stoppingToken);
                        break;
                    case SubscribeTo subscribe when await connections.FindAsync(subscribe.ConnectionId, stoppingToken) is { } connection:
                        await EnsureSubscribedAsync(connection, stoppingToken);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Handling a drive change failed");
            }
        }
    }

    /// <summary>
    /// Makes sure Graph will tell the server about changes to this connection's drive: subscribes, or renews a
    /// subscription that expires within <paramref name="renewWithin"/>. Does nothing where the server can't be reached
    /// from the internet, as on a developer's machine.
    /// </summary>
    public async Task EnsureSubscribedAsync(StorageConnection connection, CancellationToken ct, TimeSpan? renewWithin = null)
    {
        if (StorageSetup.NotificationUrl(options) is not { } url || connection.NeedsReconnect || connection.Provider != OneDriveProvider.Id) return;
        var client = drives.ForConnection(connection.Id);
        var now = DateTime.UtcNow;
        if (connection.SubscriptionId is { } id && connection.SubscriptionExpires is { } expires)
        {
            if (expires - now > (renewWithin ?? TimeSpan.FromDays(3))) return;
            if (await client.RenewAsync(id, ct) is { } renewed)
            {
                await connections.SetSubscriptionAsync(connection.Id, id, renewed, ct);
                return;
            }
        }
        var drive = new DriveSummary(connection.AccountId, connection.AccountKind ?? "personal", null);
        var subscription = await client.SubscribeAsync(drive, url, ClientState.For(connection), ct);
        await connections.SetSubscriptionAsync(connection.Id, subscription.Id, subscription.Expires, ct);
        log.LogInformation("Watching drive changes for connection {Connection} until {Expires}", connection.Id, subscription.Expires);
    }

    /// <summary>Cancels a disconnected drive's notifications, as far as Graph still allows with the token that's left.</summary>
    public async Task UnsubscribeAsync(StorageConnection connection, CancellationToken ct)
    {
        if (connection.SubscriptionId is not { } id) return;
        try
        {
            await drives.ForConnection(connection.Id).UnsubscribeAsync(id, ct);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // It lapses on its own within days; until then its notifications match no connection and are ignored.
            log.LogInformation("Couldn't cancel drive notifications for {Connection}: {Reason}", connection.Id, ex.Message);
        }
    }
}

/// <summary>
/// The clientState Graph returns with each notification: the connection's ID and its secret, which only the server
/// and Graph know. Graph limits it to 128 characters.
/// </summary>
static class ClientState
{
    public static string For(StorageConnection connection) => $"{connection.Id}.{connection.NotificationSecret}";

    public static (string ConnectionId, string Secret)? Parse(string? state)
    {
        var dot = state?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        return dot > 0 ? (state![..dot], state[(dot + 1)..]) : null;
    }

    public static bool Matches(StorageConnection connection, string secret) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(connection.NotificationSecret), Encoding.UTF8.GetBytes(secret));
}

/// <summary>
/// Keeps every connected drive's change notifications alive, and twice a day opens each library that has drives so
/// changes Graph never announced (while the server was restarting) are found anyway.
/// </summary>
public sealed class DriveWatch(StorageConnections connections, DriveNotifications notifications, UserLibraries libraries, TimeProvider clock,
    ILogger<DriveWatch> log) : BackgroundService
{
    static readonly TimeSpan Every = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Leaves the server time to start before every library is opened.
        await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken);
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            try
            {
                await RoundAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Checking connected drives failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    async Task RoundAsync(CancellationToken ct)
    {
        var all = await connections.AllAsync(ct);
        foreach (var connection in all)
        {
            try
            {
                await notifications.EnsureSubscribedAsync(connection, ct);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                log.LogWarning("Couldn't watch drive changes for connection {Connection}: {Reason}", connection.Id, ex.Message);
            }
        }
        foreach (var userId in all.Where(c => !c.NeedsReconnect).Select(c => c.UserId).Distinct())
        {
            // Opening a library scans its folders; it stays open while there's anything new to read.
            await using var _ = await libraries.OpenAsync(userId, ct);
        }
    }
}
