using System.Collections.Concurrent;
using Bibliotaph.Storage;
using Bibliotaph.Storage.GoogleDrive;

namespace Bibliotaph.Server.Storage;

/// <summary>Google Drive, through Google's OAuth 2 endpoints and Drive API v3.</summary>
public sealed class GoogleDriveProvider : StorageProvider
{
    public const string Key = "googledrive";

    public override string Id => Key;
    public override string Name => "Google Drive";
    public override string Owner => "Google";
    protected override string ConfigurationName => "GoogleDrive";

    public override string AuthorizationEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";
    public override string TokenEndpoint => "https://oauth2.googleapis.com/token";

    /// <summary>
    /// Read every file, and nothing that writes. Library folders can be anywhere in a drive, and Drive's narrower
    /// scope covers only files a user picks one by one.
    /// </summary>
    public override IReadOnlyList<string> Scopes { get; } = ["https://www.googleapis.com/auth/drive.readonly"];

    /// <summary>
    /// Google hands out a refresh token only for offline access, and only the first time unless consent is asked again,
    /// which a reconnect needs.
    /// </summary>
    public override IReadOnlyDictionary<string, string> AuthorizationParameters { get; } =
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" };

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var user = await new GoogleDriveClient(http, _ => Task.FromResult(accessToken)).AboutAsync(ct);
        var who = user.Name is { Length: > 0 } name ? name : user.Email;
        return new StorageIdentity(user.PermissionId, who is { Length: > 0 } ? $"Google Drive · {who}" : "Google Drive", null);
    }

    public override IStorageDrive Drive(HttpClient http, ConnectedAccount account, Func<CancellationToken, Task<string>> accessToken, TimeProvider clock) =>
        new GoogleDriveDrive(new GoogleDriveClient(http, accessToken, clock), clock);
}

/// <summary>
/// Asks each connected Google Drive every five minutes whether anything changed, from where it last stood: Drive's push
/// notifications need a renewed channel per drive, and a short question to its change log is cheaper than keeping them.
/// Where each drive stood is kept in memory; after a restart, the twice-daily look through every library finds what
/// changed meanwhile.
/// </summary>
public sealed class GoogleDriveChanges(StorageConnections connections, DriveTokens tokens, IHttpClientFactory http, DriveNotifications notifications,
    TimeProvider clock, ILogger<GoogleDriveChanges> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<string, string> _tokens = new();

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
                log.LogWarning(ex, "Checking Google Drives for changes failed");
            }
        }
    }

    /// <summary>One look at every connected Google Drive's changes.</summary>
    public async Task RoundAsync(CancellationToken ct)
    {
        var all = (await connections.AllAsync(ct)).Where(c => c.Provider == GoogleDriveProvider.Key && !c.NeedsReconnect).ToList();
        foreach (var gone in _tokens.Keys.Except(all.Select(c => c.Id))) _tokens.TryRemove(gone, out _);
        foreach (var connection in all)
        {
            var client = new GoogleDriveClient(http.CreateClient(GoogleDriveProvider.Key), c => tokens.AccessTokenAsync(connection.Id, c), clock);
            try
            {
                if (!_tokens.TryGetValue(connection.Id, out var since))
                {
                    _tokens[connection.Id] = await client.StartPageTokenAsync(ct);
                    continue;
                }
                var (changed, next) = await client.ChangesSinceAsync(since, ct);
                _tokens[connection.Id] = next;
                if (changed) notifications.ChangedIn(connection);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                log.LogInformation("Couldn't check Google Drive changes for {Connection}: {Reason}", connection.Id, ex.Message);
            }
        }
    }
}
