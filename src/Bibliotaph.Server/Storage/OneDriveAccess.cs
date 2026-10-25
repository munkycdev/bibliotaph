using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bibliotaph.Server.Auth;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Storage;

/// <summary>OneDrive as a storage provider: the Microsoft identity platform's endpoints and the access Bibliotaph asks for.</summary>
public static class OneDriveProvider
{
    public const string Id = "onedrive";
    public const string Name = "OneDrive";

    public const string AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    public const string TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    /// <summary>
    /// Read every file (the library folders), read and write only Bibliotaph's own app folder (where imported books
    /// go), and keep access while the user is away. Nothing broader: no write access to the user's own folders.
    /// </summary>
    public static readonly string[] Scopes = ["offline_access", "Files.Read", "Files.ReadWrite.AppFolder"];
}

/// <summary>
/// Access tokens for connected OneDrives, made from each connection's refresh token when needed and kept in memory
/// until shortly before they expire. A refused refresh token marks the connection as needing to be connected again.
/// </summary>
public sealed class DriveTokens(IHttpClientFactory http, IConfiguration configuration, StorageConnections connections, TimeProvider clock,
    ILogger<DriveTokens> log)
{
    static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshing = new();

    public async Task<string> AccessTokenAsync(string connectionId, CancellationToken ct)
    {
        if (Cached(connectionId) is { } token) return token;
        var gate = _refreshing.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return Cached(connectionId) ?? await RefreshAsync(connectionId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Forgets a connection's access token, as when it's disconnected.</summary>
    public void Forget(string connectionId) => _cache.TryRemove(connectionId, out _);

    string? Cached(string connectionId) =>
        _cache.TryGetValue(connectionId, out var cached) && cached.Expires - Margin > clock.GetUtcNow() ? cached.Token : null;

    async Task<string> RefreshAsync(string connectionId, CancellationToken ct)
    {
        var connection = await connections.FindAsync(connectionId, ct)
            ?? throw new OneDriveException(HttpStatusCode.Unauthorized, "That drive isn't connected any more.");
        if (connection.NeedsReconnect) throw new OneDriveException(HttpStatusCode.Unauthorized, "OneDrive needs connecting again.");

        var client = configuration.GetSection("Auth:microsoft").Get<ProviderClient>() ?? new ProviderClient();
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = client.ClientId ?? "",
            ["client_secret"] = client.ClientSecret ?? "",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = connections.RefreshToken(connection),
            ["scope"] = string.Join(' ', OneDriveProvider.Scopes),
        });
        using var response = await http.CreateClient(nameof(DriveTokens)).PostAsync(OneDriveProvider.TokenEndpoint, request, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            // invalid_grant: revoked, expired, or the password changed. Only the user can fix that, by connecting again.
            if (error == "invalid_grant")
            {
                log.LogWarning("OneDrive connection {Connection} was refused and needs connecting again", connectionId);
                await connections.SetNeedsReconnectAsync(connectionId, ct);
            }
            throw new OneDriveException(response.StatusCode, $"Signing in to OneDrive failed: {error ?? response.StatusCode.ToString()}");
        }

        var token = root.GetProperty("access_token").GetString()!;
        var expires = clock.GetUtcNow() + TimeSpan.FromSeconds(root.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3600);
        if (root.TryGetProperty("refresh_token", out var rotated) && rotated.GetString() is { Length: > 0 } refresh)
            await connections.SaveRefreshTokenAsync(connectionId, refresh, ct);
        _cache[connectionId] = (token, expires);
        return token;
    }
}

/// <summary>
/// The OneDrives one user has connected, by drive ID, for that user's library. Loaded from accounts.db when the library
/// opens and again whenever the user connects or disconnects a drive.
/// </summary>
public sealed class UserDrives(string userId, StorageConnections connections, DriveTokens tokens, IHttpClientFactory http, TimeProvider clock) : IDriveClients
{
    volatile IReadOnlyDictionary<string, (ConnectedAccount Account, OneDriveClient Client)> _drives =
        new Dictionary<string, (ConnectedAccount, OneDriveClient)>();

    public string UserId => userId;

    public IReadOnlyCollection<ConnectedAccount> Accounts => _drives.Values.Select(d => d.Account).ToList();

    public OneDriveClient? Find(string driveId) => _drives.TryGetValue(driveId, out var drive) ? drive.Client : null;

    public ConnectedAccount? Account(string driveId) => _drives.TryGetValue(driveId, out var drive) ? drive.Account : null;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var drives = new Dictionary<string, (ConnectedAccount, OneDriveClient)>();
        foreach (var account in await connections.ListAsync(userId, ct))
        {
            if (account.Provider != OneDriveProvider.Id) continue;
            var id = account.Id;
            drives[account.AccountId] = (account, new OneDriveClient(http.CreateClient(nameof(OneDriveClient)), c => tokens.AccessTokenAsync(id, c), clock));
        }
        _drives = drives;
    }
}

/// <summary>Makes each library's <see cref="UserDrives"/>; the server's own services are shared between them.</summary>
public sealed class UserDriveFactory(StorageConnections connections, DriveTokens tokens, IHttpClientFactory http, TimeProvider clock)
{
    public UserDrives For(string userId) => new(userId, connections, tokens, http, clock);

    /// <summary>A client for a connection that isn't in a library yet, as while it's being connected.</summary>
    public OneDriveClient ForToken(string accessToken) =>
        new(http.CreateClient(nameof(OneDriveClient)), _ => Task.FromResult(accessToken), clock);

    public OneDriveClient ForConnection(string connectionId) =>
        new(http.CreateClient(nameof(OneDriveClient)), c => tokens.AccessTokenAsync(connectionId, c), clock);
}
