using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using Bibliotaph.Processing;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Desktop;
using Bibliotaph.Storage.WebDav;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Each provider's desktop OAuth app, as this build was made with (assembly metadata from the build's variables, so no
/// client ID lives in the source). A fork built without them offers WebDAV only.
/// </summary>
public sealed class DesktopStorageApps : IDesktopClients
{
    /// <summary>The loopback port Dropbox redirects to: it matches a redirect exactly, port and all.</summary>
    public const int FixedPort = 41583;

    readonly Dictionary<string, DesktopClient> _clients;

    public DesktopStorageApps() : this(Metadata(typeof(DesktopStorageApps).Assembly)) { }

    public DesktopStorageApps(IReadOnlyDictionary<string, string> values)
    {
        var clients = new Dictionary<string, DesktopClient>(StringComparer.Ordinal);
        void Add(string provider, int? port = null)
        {
            if (values.TryGetValue($"Storage.{provider}.ClientId", out var id) && id.Length > 0)
                clients[provider] = new DesktopClient(id, values.GetValueOrDefault($"Storage.{provider}.ClientSecret") is { Length: > 0 } secret ? secret : null, port);
        }
        Add("onedrive");
        Add("dropbox", FixedPort);
        Add("googledrive");
        _clients = clients;
    }

    public DesktopClient? Find(string provider) => _clients.GetValueOrDefault(provider);

    /// <summary>Whether a provider can be connected from this PC: WebDAV always, the rest with their app.</summary>
    public bool CanConnect(StorageKind kind) => kind is not OAuthStorageKind || _clients.ContainsKey(kind.Id);

    static Dictionary<string, string> Metadata(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(a => a.Key.StartsWith("Storage.", StringComparison.Ordinal) && a.Value is { Length: > 0 })
            .ToDictionary(a => a.Key, a => a.Value!, StringComparer.Ordinal);
}

/// <summary>
/// The HTTP clients this PC's storage calls go through: one per provider, as the web server's are, with WebDAV's
/// following no redirect itself so its client can keep the password to the same server.
/// </summary>
public sealed class StorageHttp(HttpMessageHandler? webDav = null) : IDisposable
{
    readonly Lock _lock = new();
    readonly Dictionary<string, HttpClient> _clients = [];

    public StorageHttp() : this(null) { }

    public HttpClient Tokens { get; } = new();

    public HttpClient For(string provider)
    {
        lock (_lock)
        {
            if (_clients.TryGetValue(provider, out var client)) return client;
            client = provider != WebDavKind.Key ? new HttpClient()
                : webDav is not null ? new HttpClient(webDav, disposeHandler: false)
                : new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
            _clients[provider] = client;
            return client;
        }
    }

    public void Dispose()
    {
        Tokens.Dispose();
        lock (_lock)
            foreach (var client in _clients.Values) client.Dispose();
    }
}

/// <summary>How connecting storage on this PC ended: connected, or why not, in words for Settings.</summary>
public sealed record StorageConnectOutcome(PcConnection? Connection, string? Problem);

/// <summary>
/// Connecting and disconnecting storage on this PC: a provider's sign-in in the browser, or a WebDAV folder's address
/// and password, then the account kept with its secret, and the library told so its folders there are read again.
/// </summary>
public sealed class StorageConnecting(PcConnections connections, PcDrives drives, PcTokens tokens, StorageHttp http, IDesktopClients clients,
    IDesktopShell shell, IJobControl indexing, PcStorageChanges changes, ILogger<StorageConnecting> log)
{
    public async Task<StorageConnectOutcome> ConnectAsync(OAuthStorageKind kind, CancellationToken ct)
    {
        if (clients.Find(kind.Id) is not { } client) return new(null, $"This build of Bibliotaph can't connect {kind.Name}.");
        var signIn = new LoopbackSignIn(http.Tokens, uri =>
        {
            if (shell.Open(uri.AbsoluteUri) is { } problem) throw new IOException($"Windows couldn't open your browser: {problem.Message}", problem);
            return Task.CompletedTask;
        });
        try
        {
            var signed = await signIn.SignInAsync(kind, client, ct);
            return new(await SaveAsync(kind, signed.Identity, signed.RefreshToken, signed.Scopes, ct), null);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TimeoutException or System.Text.Json.JsonException)
        {
            log.LogWarning(ex, "Connecting {Provider} failed", kind.Name);
            return new(null, ex.Message);
        }
    }

    public async Task<StorageConnectOutcome> ConnectWebDavAsync(string address, string user, string password, CancellationToken ct)
    {
        // Unlike the web server, a PC may reach a server on its own network, so only the scheme is checked.
        var found = await WebDavKind.ConnectAsync(http.For(WebDavKind.Key), address, user, password, ct,
            uri => uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp ? null : "A WebDAV address starts https:// (or http:// on your own network).");
        if (found.Problem is { } problem) return new(null, problem);
        return new(await SaveAsync(StorageKinds.Find(WebDavKind.Key)!, found.Identity!, found.Credential!.ToJson(), "", ct), null);
    }

    public async Task DisconnectAsync(PcConnection connection, CancellationToken ct)
    {
        tokens.Forget(connection.Id);
        await connections.RemoveAsync(connection.Id, ct);
        await drives.ReloadAsync(ct);
        // Its folders go offline at this look, rather than their books going missing.
        indexing.RequestScan();
        log.LogInformation("Disconnected {Provider} storage", connection.Provider);
    }

    async Task<PcConnection> SaveAsync(StorageKind kind, StorageIdentity identity, string secret, string scopes, CancellationToken ct)
    {
        var connection = await connections.SaveAsync(kind.Id, identity, secret, scopes, ct);
        tokens.Forget(connection.Id);
        await drives.ReloadAsync(ct);
        // A reconnect brings back folders that went offline while it couldn't be read.
        indexing.RequestScan();
        _ = changes.CheckNowAsync(CancellationToken.None);
        log.LogInformation("Connected {Provider} storage", kind.Name);
        return connection;
    }

    /// <summary>Whom a connection is, for Settings: "OneDrive · Dana", and the account kind where that matters.</summary>
    public static string Describe(PcConnection connection) => connection.DisplayName.Length > 0
        ? connection.DisplayName
        : string.Create(CultureInfo.CurrentCulture, $"{StorageKinds.Find(connection.Provider)?.Name ?? connection.Provider}");
}
