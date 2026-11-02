using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Web;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Desktop;
using Bibliotaph.Storage.OneDrive;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// Storage connected on a PC: signing in through the browser and a loopback redirect, the connection kept with its
/// secret apart, access tokens refreshed with a desktop client, and the drive listed, read and asked for changes.
/// </summary>
public sealed class PcStorageTests : IAsyncDisposable
{
    readonly TempFolder _folder = new();
    readonly CatalogDatabase _catalog;
    readonly FakeGraph _graph = new();
    readonly MemorySecrets _secrets = new();
    readonly PcConnections _connections;

    public PcStorageTests()
    {
        _catalog = new CatalogDatabase(Path.Combine(_folder.Path, "catalog.db"), Path.Combine(_folder.Path, "backups"));
        _catalog.MigrateAsync().GetAwaiter().GetResult();
        _connections = new PcConnections(new SettingsStore(new CatalogContexts(_catalog)), _secrets);
        _graph.Add("RPG/one.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 one"));
        _graph.Add("RPG/Maps/map.png", [0x89, 0x50, 0x4E, 0x47]);
    }

    public ValueTask DisposeAsync()
    {
        _connections.Dispose();
        _folder.Dispose();
        return ValueTask.CompletedTask;
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly DesktopClient Client = new("desktop-client");

    HttpClient Http() => new(_graph, disposeHandler: false);

    // The loopback listener is this machine's own, never behind a proxy.
    static HttpClient Browser() => new(new HttpClientHandler { UseProxy = false });

    [Fact]
    public async Task Signing_in_asks_with_pkce_and_takes_the_code_the_browser_brings_back_to_its_own_state()
    {
        var kind = new OneDriveKind();
        Uri? asked = null;
        Task? browsing = null;
        var signIn = new LoopbackSignIn(Http(), authorize =>
        {
            asked = authorize;
            var query = HttpUtility.ParseQueryString(authorize.Query);
            var redirect = query["redirect_uri"]!;
            browsing = Task.Run(async () =>
            {
                using var browser = Browser();
                // A stray request and one with another sign-in's state don't end it.
                Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"{redirect}/favicon.ico", Ct)).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"{redirect}/?code=other&state=wrong", Ct)).StatusCode);
                var answer = await browser.GetAsync($"{redirect}/?code=the-code&state={query["state"]}", Ct);
                Assert.Contains("You can close this tab", await answer.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }, Ct);
            return Task.CompletedTask;
        });

        var signed = await signIn.SignInAsync(kind, Client, Ct);
        await browsing!;

        var parameters = HttpUtility.ParseQueryString(asked!.Query);
        Assert.StartsWith(kind.AuthorizationEndpoint, asked.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("desktop-client", parameters["client_id"]);
        Assert.Equal("S256", parameters["code_challenge_method"]);
        Assert.Equal(43, parameters["code_challenge"]!.Length);
        Assert.StartsWith("http://127.0.0.1:", parameters["redirect_uri"], StringComparison.Ordinal);
        Assert.Equal("offline_access Files.Read", parameters["scope"]);
        Assert.Equal(FakeGraph.DriveId, signed.Identity.AccountId);
        Assert.Equal("OneDrive · Dana", signed.Identity.DisplayName);
        Assert.Equal("rotated", signed.RefreshToken);
    }

    [Fact]
    public async Task A_sign_in_the_user_declines_says_so()
    {
        var signIn = new LoopbackSignIn(Http(), authorize =>
        {
            var query = HttpUtility.ParseQueryString(authorize.Query);
            _ = Browser().GetAsync($"{query["redirect_uri"]}/?error=access_denied&state={query["state"]}", Ct);
            return Task.CompletedTask;
        });

        var refused = await Assert.ThrowsAsync<OAuthRefusedException>(() => signIn.SignInAsync(new OneDriveKind(), Client, Ct));
        Assert.Contains("access wasn't allowed", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connected_drive_lists_reads_and_keeps_the_newest_refresh_token()
    {
        await _connections.SaveAsync(OneDriveKind.Key, new StorageIdentity(FakeGraph.DriveId, "OneDrive · Dana", "personal"), "first", "Files.Read", Ct);
        var drives = Drives();
        await drives.ReloadAsync(Ct);
        var root = StoragePath.Of(OneDriveKind.Key, FakeGraph.DriveId, "RPG");

        var listing = await new StorageSourceLister(drives).ListAsync(root, [], Ct);
        Assert.Equal(["Maps/map.png", "one.pdf"], listing!.Files.Select(f => f.RelativePath).Order());
        await using (var book = new StorageFileReader(drives, Path.Combine(_folder.Path, "temp"), new SourceFileReader()).OpenRead($"{root}/one.pdf"))
            Assert.Equal("%PDF-1.7 one", new StreamReader(book).ReadToEnd());
        Assert.Equal("rotated", _secrets.Find($"{OneDriveKind.Key}/{FakeGraph.DriveId}"));
        Assert.Null(drives.Find(OneDriveKind.Key, "someone-else"));
    }

    [Fact]
    public async Task A_refused_refresh_token_needs_connecting_again()
    {
        await _connections.SaveAsync(OneDriveKind.Key, new StorageIdentity(FakeGraph.DriveId, "OneDrive · Dana", "personal"), "revoked", "Files.Read", Ct);
        _graph.RefuseRefresh = true;
        var drives = Drives();
        await drives.ReloadAsync(Ct);

        Assert.Null(await new StorageSourceLister(drives).ListAsync(StoragePath.Of(OneDriveKind.Key, FakeGraph.DriveId, "RPG"), [], Ct));
        Assert.True((await _connections.FindAsync(OneDriveKind.Key, FakeGraph.DriveId, Ct))!.NeedsReconnect);
    }

    [Fact]
    public async Task Disconnecting_forgets_the_secret_and_the_drive()
    {
        var connection = await _connections.SaveAsync(OneDriveKind.Key, new StorageIdentity(FakeGraph.DriveId, "OneDrive · Dana", "personal"), "first", "Files.Read", Ct);
        var drives = Drives();
        await drives.ReloadAsync(Ct);

        await _connections.RemoveAsync(connection.Id, Ct);
        await drives.ReloadAsync(Ct);

        Assert.Null(_secrets.Find(connection.Id));
        Assert.Null(drives.Find(OneDriveKind.Key, FakeGraph.DriveId));
        Assert.Empty(await _connections.ListAsync(Ct));
    }

    [Fact]
    public async Task Changes_in_a_connected_drive_have_its_folders_looked_at_again()
    {
        await _connections.SaveAsync(OneDriveKind.Key, new StorageIdentity(FakeGraph.DriveId, "OneDrive · Dana", "personal"), "first", "Files.Read", Ct);
        var drives = Drives();
        await drives.ReloadAsync(Ct);
        var feed = new StorageChangeFeed();
        var heard = 0;
        using var watch = new StorageAwareFolderWatcher(feed, new NoWatcher())
            .Watch(StoragePath.Of(OneDriveKind.Key, FakeGraph.DriveId, "RPG"), () => Interlocked.Increment(ref heard), () => { });
        var changes = new PcStorageChanges(drives, feed);

        await changes.CheckNowAsync(Ct);
        await changes.CheckNowAsync(Ct);
        Assert.Equal(0, heard);

        _graph.Add("RPG/two.pdf", Encoding.ASCII.GetBytes("%PDF-1.7 two"));
        await changes.CheckNowAsync(Ct);
        Assert.Equal(1, heard);
    }

    [Fact]
    public async Task Storage_paths_go_to_storage_and_every_other_folder_where_it_went_before()
    {
        var local = new RecordingLister();
        var storage = new RecordingLister();
        var lister = new StorageAwareSourceLister(storage, local);
        var inStorage = StoragePath.Of(OneDriveKind.Key, FakeGraph.DriveId, "RPG");

        await lister.ListAsync(inStorage, [@"C:\Books"], Ct);
        await lister.ListAsync(@"C:\Books", [inStorage], Ct);

        Assert.Equal([inStorage], storage.Listed);
        Assert.Equal([@"C:\Books"], local.Listed);
        Assert.Empty(local.Others);
        var identity = new StorageAwareFileIdentity(new NoIdentityExpected());
        Assert.Null(identity.Volume(inStorage));
        Assert.Null(identity.FileId($"{inStorage}/one.pdf"));
    }

    PcDrives Drives()
    {
        var tokens = new PcTokens(Http(), _connections, new Clients());
        return new PcDrives(_ => Http(), _connections, tokens);
    }

    sealed class Clients : IDesktopClients
    {
        public DesktopClient? Find(string provider) => Client;
    }

    sealed class MemorySecrets : IConnectionSecrets
    {
        readonly ConcurrentDictionary<string, string> _secrets = new();

        public string? Find(string connectionId) => _secrets.GetValueOrDefault(connectionId);

        public bool Remember(string connectionId, string secret)
        {
            _secrets[connectionId] = secret;
            return true;
        }

        public void Forget(string connectionId) => _secrets.TryRemove(connectionId, out _);
    }

    sealed class CatalogContexts(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
    }

    sealed class NoWatcher : IFolderWatcher
    {
        public IDisposable Watch(string folder, Action changed, Action stopped) => throw new InvalidOperationException("A storage folder went to the file system.");
    }

    sealed class NoIdentityExpected : IFileIdentity
    {
        public VolumeIdentity? Volume(string folder) => throw new InvalidOperationException("A storage folder went to Windows.");

        public string? FileId(string path) => throw new InvalidOperationException("A storage file went to Windows.");
    }

    sealed class RecordingLister : ISourceLister
    {
        public List<string> Listed { get; } = [];
        public List<string> Others { get; } = [];

        public Task<SourceListing?> ListAsync(string root, IReadOnlyCollection<string> otherRoots, CancellationToken ct)
        {
            Listed.Add(root);
            Others.AddRange(otherRoots.Where(StoragePath.IsStoragePath));
            return Task.FromResult<SourceListing?>(new SourceListing([], []));
        }
    }
}
