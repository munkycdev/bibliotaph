using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bibliotaph.Storage;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Access tokens for connected storage, made from each connection's refresh token when needed and kept in memory
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
            ?? throw new StorageException(HttpStatusCode.Unauthorized, "That storage isn't connected any more.");
        var provider = StorageProviders.Find(connection.Provider)
            ?? throw new StorageException(HttpStatusCode.Unauthorized, $"Bibliotaph can't read {connection.Provider} storage.");
        if (connection.NeedsReconnect) throw new StorageException(HttpStatusCode.Unauthorized, $"{provider.Name} needs connecting again.");

        var app = provider.App(configuration) ?? new StorageApp();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = app.ClientId ?? "",
            ["client_secret"] = app.ClientSecret ?? "",
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = connections.RefreshToken(connection),
        };
        if (provider.ScopesOnRefresh) form["scope"] = string.Join(' ', provider.Scopes);
        using var request = new FormUrlEncodedContent(form);
        using var response = await http.CreateClient(nameof(DriveTokens)).PostAsync(provider.TokenEndpoint, request, ct);
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            // invalid_grant: revoked, expired, or the password changed. Only the user can fix that, by connecting again.
            if (error == "invalid_grant")
            {
                log.LogWarning("Storage connection {Connection} was refused and needs connecting again", connectionId);
                await connections.SetNeedsReconnectAsync(connectionId, ct);
            }
            throw new StorageException(response.StatusCode, $"Signing in to {provider.Name} failed: {error ?? response.StatusCode.ToString()}");
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
/// The storage one user has connected, by provider and account, for that user's library. Loaded from accounts.db when
/// the library opens and again whenever the user connects or disconnects storage.
/// </summary>
public sealed class UserDrives(string userId, StorageConnections connections, UserDriveFactory factory) : IStorageDrives
{
    volatile IReadOnlyDictionary<(string Provider, string Account), (ConnectedAccount Account, IStorageDrive Drive)> _drives =
        new Dictionary<(string, string), (ConnectedAccount, IStorageDrive)>();

    public string UserId => userId;

    public IReadOnlyCollection<ConnectedAccount> Accounts => _drives.Values.Select(d => d.Account).ToList();

    public IStorageDrive? Find(string provider, string account) => _drives.TryGetValue((provider, account), out var drive) ? drive.Drive : null;

    public ConnectedAccount? Account(string provider, string account) => _drives.TryGetValue((provider, account), out var drive) ? drive.Account : null;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var drives = new Dictionary<(string, string), (ConnectedAccount, IStorageDrive)>();
        foreach (var account in await connections.ListAsync(userId, ct))
            if (factory.Drive(account) is { } drive) drives[(account.Provider, account.AccountId)] = (account, drive);
        _drives = drives;
    }
}

/// <summary>Makes each library's <see cref="UserDrives"/>; the server's own services are shared between them.</summary>
public sealed class UserDriveFactory(StorageConnections connections, DriveTokens tokens, IHttpClientFactory http, TimeProvider clock)
{
    public UserDrives For(string userId) => new(userId, connections, this);

    /// <summary>A connection's drive, or null for a provider this server doesn't know.</summary>
    public IStorageDrive? Drive(ConnectedAccount account) =>
        StorageProviders.Find(account.Provider)?.Drive(http.CreateClient(account.Provider), account.AccountId,
            c => tokens.AccessTokenAsync(account.Id, c), clock);

    /// <summary>The HTTP client a provider's calls go through, as while an account is being connected.</summary>
    public HttpClient Http(StorageProvider provider) => http.CreateClient(provider.Id);

    /// <summary>A OneDrive connection's Graph client, for its change subscriptions.</summary>
    public OneDriveClient OneDrive(string connectionId) =>
        new(http.CreateClient(OneDriveProvider.Key), c => tokens.AccessTokenAsync(connectionId, c), clock);
}
