using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Storage.Desktop;

/// <summary>A storage account connected on this PC, as Settings lists it. Its refresh token or password is kept apart, in <see cref="IConnectionSecrets"/>.</summary>
public sealed record PcConnection(string Provider, string AccountId, string DisplayName, string? AccountKind, string Scopes, bool NeedsReconnect,
    DateTime Connected)
{
    /// <summary>One connection per provider and account, as library paths name them.</summary>
    public string Id => $"{Provider}/{AccountId}";
}

/// <summary>Where this PC keeps each connection's refresh token or password: Windows Credential Manager, or memory in tests.</summary>
public interface IConnectionSecrets
{
    string? Find(string connectionId);

    /// <summary>Returns false when it couldn't be stored.</summary>
    bool Remember(string connectionId, string secret);

    void Forget(string connectionId);
}

/// <summary>Each provider's desktop OAuth app, as this build was made with; a provider with none can't be connected here.</summary>
public interface IDesktopClients
{
    DesktopClient? Find(string provider);
}

/// <summary>
/// The storage accounts connected on this PC, in a setting of their own that never leaves it: not synced, not backed
/// up, as its secrets wouldn't open on another PC. A drive connected on the web library is the web's; a PC connects
/// its own.
/// </summary>
public sealed class PcConnections(SettingsStore settings, IConnectionSecrets secrets, TimeProvider? clock = null) : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Connecting or disconnecting changed what's connected.</summary>
    public event EventHandler? Changed;

    public async Task<IReadOnlyList<PcConnection>> ListAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.StorageConnections, ct) is { Length: > 0 } json
            ? JsonSerializer.Deserialize<List<PcConnection>>(json, Json) ?? []
            : [];

    public async Task<PcConnection?> FindAsync(string provider, string accountId, CancellationToken ct = default) =>
        (await ListAsync(ct)).FirstOrDefault(c => c.Provider == provider && c.AccountId == accountId);

    /// <summary>Connects an account, or refreshes one connected before, keeping its library folders.</summary>
    public async Task<PcConnection> SaveAsync(string provider, StorageIdentity identity, string secret, string scopes, CancellationToken ct = default)
    {
        var connection = new PcConnection(provider, identity.AccountId, identity.DisplayName, identity.AccountKind, scopes, false, _clock.GetUtcNow().UtcDateTime);
        if (!secrets.Remember(connection.Id, secret)) throw new IOException("Windows didn't let Bibliotaph keep this connection's sign-in.");
        await UpdateAsync(all => [.. all.Where(c => c.Id != connection.Id), connection], ct);
        return connection;
    }

    public async Task RemoveAsync(string connectionId, CancellationToken ct = default)
    {
        await UpdateAsync(all => [.. all.Where(c => c.Id != connectionId)], ct);
        secrets.Forget(connectionId);
    }

    public Task SetNeedsReconnectAsync(string connectionId, CancellationToken ct = default) =>
        UpdateAsync(all => [.. all.Select(c => c.Id == connectionId ? c with { NeedsReconnect = true } : c)], ct);

    public string? Secret(string connectionId) => secrets.Find(connectionId);

    public void Dispose() => _gate.Dispose();

    public Task SaveSecretAsync(string connectionId, string secret)
    {
        secrets.Remember(connectionId, secret);
        return Task.CompletedTask;
    }

    async Task UpdateAsync(Func<IReadOnlyList<PcConnection>, IReadOnlyList<PcConnection>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = change(await ListAsync(ct)).OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            await settings.SetAsync(SettingKeys.StorageConnections, JsonSerializer.Serialize(next, Json), ct);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Access tokens for this PC's connections, made from each one's refresh token when needed and kept in memory until
/// shortly before they expire, as the web server's are; a provider that rotates refresh tokens has the newest kept. A
/// refused refresh token marks the connection as needing to be connected again. WebDAV's password is its credential as it is.
/// </summary>
public sealed class PcTokens(HttpClient http, PcConnections connections, IDesktopClients clients, TimeProvider? clock = null, ILogger<PcTokens>? log = null)
{
    static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<PcTokens>.Instance;
    readonly ConcurrentDictionary<string, (string Token, DateTimeOffset Expires)> _cache = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshing = new();

    public async Task<string> CredentialAsync(PcConnection connection, CancellationToken ct)
    {
        if (Cached(connection.Id) is { } token) return token;
        var gate = _refreshing.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return Cached(connection.Id) ?? await RefreshAsync(connection, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Forget(string connectionId) => _cache.TryRemove(connectionId, out _);

    /// <summary>The provider refused a connection's credential: only the user can fix that, by connecting again.</summary>
    public async Task RefusedAsync(PcConnection connection, CancellationToken ct)
    {
        _log.LogWarning("{Provider} storage {Account} was refused and needs connecting again", connection.Provider, connection.DisplayName);
        Forget(connection.Id);
        await connections.SetNeedsReconnectAsync(connection.Id, ct);
    }

    string? Cached(string connectionId) =>
        _cache.TryGetValue(connectionId, out var cached) && cached.Expires - Margin > _clock.GetUtcNow() ? cached.Token : null;

    async Task<string> RefreshAsync(PcConnection connection, CancellationToken ct)
    {
        var kind = StorageKinds.Find(connection.Provider)
            ?? throw new StorageException(HttpStatusCode.Unauthorized, $"Bibliotaph can't read {connection.Provider} storage.");
        var current = await connections.FindAsync(connection.Provider, connection.AccountId, ct);
        if (current is null) throw new StorageException(HttpStatusCode.Unauthorized, $"{kind.Name} isn't connected any more.");
        if (current.NeedsReconnect) throw new StorageException(HttpStatusCode.Unauthorized, $"{kind.Name} needs connecting again.");
        var secret = connections.Secret(connection.Id) ?? throw new StorageException(HttpStatusCode.Unauthorized, $"{kind.Name} needs connecting again.");
        if (kind is not OAuthStorageKind oauth)
        {
            _cache[connection.Id] = (secret, DateTimeOffset.MaxValue);
            return secret;
        }

        var client = clients.Find(connection.Provider) ?? throw new StorageException(HttpStatusCode.Unauthorized, $"This build of Bibliotaph can't connect {kind.Name}.");
        OAuthGrant grant;
        try
        {
            grant = await OAuthGrants.RefreshAsync(http, oauth, client.OAuth, secret, ct);
        }
        catch (OAuthRefusedException)
        {
            await RefusedAsync(connection, ct);
            throw new StorageException(HttpStatusCode.Unauthorized, $"{kind.Name} needs connecting again.");
        }
        if (grant.RefreshToken is { } rotated && rotated != secret) await connections.SaveSecretAsync(connection.Id, rotated);
        _cache[connection.Id] = (grant.AccessToken, _clock.GetUtcNow() + grant.ExpiresIn);
        return grant.AccessToken;
    }
}

/// <summary>
/// The storage connected on this PC, by provider and account, as the library reads it. Rebuilt whenever a connection
/// is made or taken away; each drive's change log is what the PC asks for changes, since webhooks can't reach it.
/// Each provider's calls go through its own HTTP client.
/// </summary>
public sealed class PcDrives : IStorageDrives, IStoragePathNames
{
    readonly Func<string, HttpClient> _http;
    readonly PcConnections _connections;
    readonly PcTokens _tokens;
    readonly TimeProvider _clock;
    volatile IReadOnlyDictionary<string, Connected> _drives = new Dictionary<string, Connected>();

    public sealed record Connected(PcConnection Connection, StorageKind Kind, IStorageDrive Drive, IStorageChangeLog? Changes);

    public PcDrives(Func<string, HttpClient> http, PcConnections connections, PcTokens tokens, TimeProvider? clock = null)
    {
        (_http, _connections, _tokens, _clock) = (http, connections, tokens, clock ?? TimeProvider.System);
        connections.Changed += (_, _) => _ = ReloadAsync();
    }

    public IReadOnlyCollection<Connected> All => [.. _drives.Values];

    public IStorageDrive? Find(string provider, string account) => _drives.TryGetValue($"{provider}/{account}", out var drive) ? drive.Drive : null;

    public PcConnection? Connection(string provider, string account) => _drives.TryGetValue($"{provider}/{account}", out var drive) ? drive.Connection : null;

    /// <summary>"OneDrive · Dana › RPG › Maps", or the provider's name alone for an account that isn't connected here.</summary>
    public string? Show(string path)
    {
        if (!StoragePath.TryParse(path, out var location)) return null;
        var account = Connection(location.Provider, location.Account)?.DisplayName is { Length: > 0 } name
            ? name
            : $"{StorageKinds.Find(location.Provider)?.Name ?? location.Provider} (not connected on this PC)";
        return string.Join(" › ", [account, .. location.Path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)]);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var drives = new Dictionary<string, Connected>();
        foreach (var connection in await _connections.ListAsync(ct))
        {
            if (StorageKinds.Find(connection.Provider) is not { } kind) continue;
            var context = new DriveContext(_http(connection.Provider), connection.AccountId, connection.AccountKind, c => _tokens.CredentialAsync(connection, c),
                c => _tokens.RefusedAsync(connection, c), _clock);
            var drive = kind.Drive(context);
            drives[connection.Id] = new Connected(connection, kind, drive, kind.ChangeLog(drive, context));
        }
        _drives = drives;
    }
}
