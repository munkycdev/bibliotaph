using System.Net;
using System.Text.Json;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Indexing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Microsoft Graph and the Microsoft token endpoint, answering from an in-memory drive: enough of OneDrive for listing,
/// downloading and subscriptions. Files are added by path; folders exist because files are in them.
/// </summary>
sealed class FakeGraph : HttpMessageHandler
{
    public const string DriveId = "drive1";

    readonly Dictionary<string, (byte[] Bytes, DateTime Modified)> _files = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    public int Downloads { get; private set; }
    public int TokenRequests { get; private set; }
    public List<string> Subscriptions { get; } = [];

    /// <summary>The token endpoint answers invalid_grant, as for a revoked refresh token.</summary>
    public bool RefuseRefresh { get; set; }

    public void Add(string path, byte[] bytes, DateTime? modified = null)
    {
        lock (_lock) _files[path.Trim('/')] = (bytes, modified ?? new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    public void Remove(string path)
    {
        lock (_lock) _files.Remove(path.Trim('/'));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "login.microsoftonline.com") return Task.FromResult(Token());
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path == "/v1.0/me/drive") return Json(new { id = DriveId, driveType = "personal", owner = new { user = new { displayName = "Dana" } } });
        if (path.StartsWith("/v1.0/subscriptions", StringComparison.Ordinal))
        {
            Subscriptions.Add($"{request.Method} {path}");
            return Json(new { id = "subscription1" });
        }

        var prefix = $"/v1.0/drives/{DriveId}/root";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return Status(HttpStatusCode.NotFound);
        var rest = path[prefix.Length..];
        if (rest == "/children") return Children("");
        if (!rest.StartsWith(":/", StringComparison.Ordinal)) return rest.Length == 0 ? Item("") : Status(HttpStatusCode.NotFound);
        rest = rest[2..];
        if (rest.EndsWith(":/children", StringComparison.Ordinal)) return Children(rest[..^":/children".Length]);
        if (rest.EndsWith(":/content", StringComparison.Ordinal)) return Content(rest[..^":/content".Length]);
        return Item(rest.TrimEnd(':'));
    }

    HttpResponseMessage Token()
    {
        TokenRequests++;
        if (RefuseRefresh)
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
        return JsonResponse(new { access_token = "access", expires_in = 3600, refresh_token = "rotated" });
    }

    Task<HttpResponseMessage> Children(string folder)
    {
        lock (_lock)
        {
            if (folder.Length > 0 && !IsFolder(folder)) return Status(HttpStatusCode.NotFound);
            var start = folder.Length == 0 ? "" : folder + "/";
            var entries = _files.Keys.Where(k => k.StartsWith(start, StringComparison.Ordinal))
                .Select(k => k[start.Length..])
                .GroupBy(k => k.Split('/')[0])
                .Select(g => g.Any(k => k.Contains('/', StringComparison.Ordinal)) ? Folder(g.Key) : File(start + g.Key))
                .ToList();
            return Json(new Dictionary<string, object> { ["value"] = entries });
        }
    }

    Task<HttpResponseMessage> Item(string path)
    {
        lock (_lock)
        {
            if (path.Length == 0 || IsFolder(path)) return Json(Folder(path.Split('/')[^1]));
            return _files.ContainsKey(path) ? Json(File(path)) : Status(HttpStatusCode.NotFound);
        }
    }

    Task<HttpResponseMessage> Content(string path)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(path, out var file)) return Status(HttpStatusCode.NotFound);
            Downloads++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Bytes) });
        }
    }

    bool IsFolder(string path) => _files.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal));

    static Dictionary<string, object> Folder(string name) =>
        new() { ["id"] = "f-" + name, ["name"] = name, ["size"] = 0, ["lastModifiedDateTime"] = "2026-10-01T00:00:00Z", ["folder"] = new { childCount = 1 } };

    Dictionary<string, object> File(string path) => new()
    {
        ["id"] = "i-" + path,
        ["name"] = path.Split('/')[^1],
        ["size"] = _files[path].Bytes.Length,
        ["lastModifiedDateTime"] = _files[path].Modified.ToString("o"),
        ["file"] = new { mimeType = "application/octet-stream" },
    };

    static Task<HttpResponseMessage> Json(object value) => Task.FromResult(JsonResponse(value));

    static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };

    static Task<HttpResponseMessage> Status(HttpStatusCode status) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("""{"error":{"code":"itemNotFound"}}""") });
}

/// <summary>Hands out clients that talk to one handler, as the server's IHttpClientFactory would to Graph.</summary>
sealed class FakeHttp(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>A library host with accounts.db and OneDrive wired to a <see cref="FakeGraph"/>, without the web host.</summary>
sealed class LibraryFixture : IAsyncDisposable
{
    public LibraryFixture(string root, TimeProvider clock, FakeGraph? graph = null)
    {
        Graph = graph ?? new FakeGraph();
        Paths = new ServerPaths(root);
        Accounts = new AccountsDatabase(Paths.Accounts);
        Accounts.MigrateAsync().GetAwaiter().GetResult();
        Connections = new StorageConnections(Accounts, new EphemeralDataProtectionProvider(), clock);
        var http = new FakeHttp(Graph);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:microsoft:ClientId"] = "client",
            ["Auth:microsoft:ClientSecret"] = "secret",
        }).Build();
        Tokens = new DriveTokens(http, configuration, Connections, clock, NullLogger<DriveTokens>.Instance);
        Drives = new UserDriveFactory(Connections, Tokens, http, clock);
        Pdf = new PdfWorkerPool(new PdfWorkerPoolOptions());
        Libraries = new UserLibraries(Paths, new NoRestore(), new LibraryHost(NullLoggerFactory.Instance, clock, Drives, Paths.Downloads,
            new WorkerPdfEngine(Pdf), new SkiaImageCodec()));
    }

    public FakeGraph Graph { get; }
    public ServerPaths Paths { get; }
    public AccountsDatabase Accounts { get; }
    public StorageConnections Connections { get; }
    public DriveTokens Tokens { get; }
    public UserDriveFactory Drives { get; }
    public UserLibraries Libraries { get; }
    public PdfWorkerPool Pdf { get; }

    /// <summary>Makes a user and connects the fake drive to them, as the OAuth callback does.</summary>
    public async Task<(string UserId, ConnectedAccount Drive)> UserWithDriveAsync(string name = "Dana")
    {
        var accounts = new AccountService(Accounts, TimeProvider.System);
        var (code, _) = await accounts.CreateInviteAsync(null, null, admin: false);
        var user = (await accounts.SignInAsync(new ExternalIdentity("google", Guid.NewGuid().ToString(), name, null), code, null)).User!;
        var drive = await Connections.SaveAsync(user.Id, OneDriveProvider.Id, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null);
        return (user.Id, drive);
    }

    public async ValueTask DisposeAsync()
    {
        await Libraries.DisposeAsync();
        await Pdf.DisposeAsync();
    }
}
