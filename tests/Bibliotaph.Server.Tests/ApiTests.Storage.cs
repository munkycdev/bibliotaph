using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Connected drives and reading books through the server.</summary>
    public sealed class StorageTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task A_folder_picked_from_a_connected_drive_joins_the_library_once()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/Adventures/one.pdf", [1, 2, 3]);
            _graph.Add("RPG/Maps/map.png", [4, 5]);
            _graph.Add("RPG/notes.txt", [6]);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            var storage = await client.GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct);
            var folders = await client.GetFromJsonAsync<StorageFolderList>($"/api/storage/{drive.Id}/folders?path=RPG", ct);
            using var added = await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG/Adventures"), ct);
            using var again = await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG/Adventures"), ct);
            var after = await client.GetFromJsonAsync<StorageFolderList>($"/api/storage/{drive.Id}/folders?path=RPG", ct);

            Assert.Equal("OneDrive · Dana", Assert.Single(storage!).Name);
            Assert.Equal(["Adventures", "Maps"], folders!.Folders.Select(f => f.Name));
            Assert.Equal(1, folders.Files);
            var library = Assert.Single((await added.Content.ReadFromJsonAsync<List<LibraryFolder>>(ct))!);
            Assert.Equal(("Adventures", "OneDrive · Dana › RPG › Adventures"), (library.Name, library.Location));
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.True(after!.Folders.Single(f => f.Name == "Adventures").InLibrary);
        }

        [Fact]
        public async Task Disconnecting_a_drive_forgets_it()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            using var removed = await client.DeleteAsync($"/api/storage/{drive.Id}", ct);
            using var stranger = await Client(await AddUserAsync(admin: false)).GetAsync($"/api/storage/{drive.Id}/folders", ct);

            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
            Assert.Empty((await client.GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct))!);
            Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        }

        [Fact]
        public async Task Graphs_validation_request_is_echoed_and_a_notification_with_no_match_is_accepted_and_ignored()
        {
            var ct = TestContext.Current.CancellationToken;

            using var validation = await Client().PostAsync("/storage/notifications/onedrive?validationToken=abc%20123", null, ct);
            using var unknown = await Client().PostAsync("/storage/notifications/onedrive",
                new StringContent("""{"value":[{"clientState":"nobody.secret","subscriptionId":"x"}]}""", System.Text.Encoding.UTF8, "application/json"), ct);

            Assert.Equal("abc 123", await validation.Content.ReadAsStringAsync(ct));
            Assert.Equal("text/plain", validation.Content.Headers.ContentType!.MediaType);
            Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        }

        [Fact]
        public async Task Storage_is_offered_once_its_app_is_set_up_and_connecting_asks_for_that_providers_own_access()
        {
            var ct = TestContext.Current.CancellationToken;
            var unconfigured = await Client(await AddUserAsync(admin: false)).GetFromJsonAsync<List<StorageProviderInfo>>("/api/storage/providers", ct);
            using var factory = _factory.WithWebHostBuilder(host =>
            {
                host.UseSetting("Storage:OneDrive:ClientId", "client");
                host.UseSetting("Storage:OneDrive:ClientSecret", "secret");
            });
            var user = await AddUserAsync(admin: false);
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(TestSession.Header, $"{user.Id};{user.Name};{user.IsAdmin}");

            var providers = await client.GetFromJsonAsync<List<StorageProviderInfo>>("/api/storage/providers", ct);
            using var connect = await client.GetAsync("/storage/connect/onedrive?returnUrl=%2Fsettings", ct);
            using var unknown = await client.GetAsync("/storage/connect/elsewhere", ct);

            // WebDAV needs no app of its own, so it's always offered.
            var webDav = new StorageProviderInfo("webdav", "WebDAV", "your server", SignsInWithPassword: true);
            Assert.Equal([webDav], unconfigured);
            Assert.Equal([new StorageProviderInfo("onedrive", "OneDrive", "Microsoft"), webDav], providers);
            Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);
            var authorize = connect.Headers.Location!.ToString();
            Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?", authorize, StringComparison.Ordinal);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(authorize).Query);
            Assert.Equal("offline_access Files.Read", query["scope"].ToString());
            Assert.EndsWith("/storage/callback/onedrive", query["redirect_uri"].ToString(), StringComparison.Ordinal);
            Assert.Equal("/settings?problem=storage", unknown.Headers.Location!.ToString());
        }

        [Fact]
        public async Task A_webdav_folder_connects_with_its_password_and_says_why_when_it_cant()
        {
            var ct = TestContext.Current.CancellationToken;
            var client = Client(await AddUserAsync(admin: false));
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            using var refused = await client.PostAsJsonAsync("/api/storage/webdav", new WebDavConnect(FakeWebDav.Root, FakeWebDav.User, "wrong"), ct);
            using var connected = await client.PostAsJsonAsync("/api/storage/webdav", new WebDavConnect(FakeWebDav.Root, FakeWebDav.User, FakeWebDav.Password), ct);
            var account = await connected.Content.ReadFromJsonAsync<StorageAccount>(ct);
            var listed = await client.GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct);

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("didn't accept", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
            Assert.Equal("WebDAV · dana on dav.example.com", account!.Name);
            Assert.Equal(["read your files with the password you gave", "keep access while you're signed out"], account.Access);
            Assert.Null(account.Scopes);
            Assert.Equal(account.Id, Assert.Single(listed!).Id);
            Assert.DoesNotContain(FakeWebDav.Password, await connected.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_book_on_dropbox_is_read_through_the_server_by_a_link_only_its_owner_can_follow()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId, "Dropbox · Dana", "personal", "refresh", null, ct);
            _dropbox.Add("RPG/one.pdf", System.Text.Encoding.ASCII.GetBytes("0123456789"));
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var documentId = await FirstDocumentAsync(user.Id, ct);

            var link = (await client.GetFromJsonAsync<ReaderLink>($"/api/library/documents/{documentId}/link", ct))!;
            HttpRequestMessage Range(long from, long to)
            {
                var ranged = new HttpRequestMessage(HttpMethod.Get, link.Url);
                ranged.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                return ranged;
            }
            using var read = await client.SendAsync(Range(2, 5), ct);
            using var anonymous = await Client().SendAsync(Range(2, 5), ct);
            using var someoneElse = await Client(await AddUserAsync(admin: false)).SendAsync(Range(2, 5), ct);
            using var whole = await client.GetAsync(link.Url, ct);
            using var tooMuch = await client.SendAsync(Range(0, ReadLinks.MostBytesARead), ct);
            using var forged = await client.GetAsync("/storage/read/not-a-link", ct);

            Assert.StartsWith("/storage/read/", link.Url, StringComparison.Ordinal);
            Assert.DoesNotContain("RPG", link.Url, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.PartialContent, read.StatusCode);
            Assert.Equal("2345", await read.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, someoneElse.StatusCode);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, whole.StatusCode);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, tooMuch.StatusCode);
            Assert.Equal(HttpStatusCode.Gone, forged.StatusCode);
        }

        [Fact]
        public void A_users_reads_through_the_server_stop_at_the_days_limit()
        {
            var clock = new ManualClock(new DateTimeOffset(2026, 10, 27, 23, 0, 0, TimeSpan.Zero));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:ReadBytesPerDay"] = "100" }).Build();
            var links = new ReadLinks(_factory.Services.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), clock, configuration);

            Assert.True(links.TryRead("a", 60));
            Assert.False(links.TryRead("a", 41));
            Assert.True(links.TryRead("b", 100));
            Assert.True(links.TryRead("a", 40));
            clock.Advance(TimeSpan.FromHours(2));
            Assert.True(links.TryRead("a", 100));
        }

        [Fact]
        public async Task Dropboxs_webhook_is_answered_and_a_signed_notice_finds_whats_new()
        {
            var ct = TestContext.Current.CancellationToken;
            using var factory = _factory.WithWebHostBuilder(host =>
            {
                host.UseSetting("Storage:Dropbox:ClientId", "dropbox-client");
                host.UseSetting("Storage:Dropbox:ClientSecret", "dropbox-secret");
            });
            var accounts = factory.Services.GetRequiredService<AccountService>();
            var (code, _) = await accounts.CreateInviteAsync(null, null, admin: false);
            var user = (await accounts.SignInAsync(new ExternalIdentity("google", Guid.NewGuid().ToString(), "Dana", null), code, null, ct)).User!;
            var drive = await factory.Services.GetRequiredService<StorageConnections>()
                .SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId, "Dropbox · Dana", "personal", "refresh", null, ct);
            _dropbox.Add("RPG/one.pdf", [1, 2, 3]);
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(TestSession.Header, $"{user.Id};{user.Name};{user.IsAdmin}");
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            var libraries = factory.Services.GetRequiredService<UserLibraries>();
            await WaitForFilesAsync(libraries, user.Id, 1, ct);
            _dropbox.Add("RPG/two.pdf", [4, 5, 6]);
            var body = System.Text.Encoding.UTF8.GetBytes($$$"""{"list_folder":{"accounts":["{{{FakeDropbox.AccountId}}}"]}}""");
            var signature = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData("dropbox-secret"u8, body));
            var webhook = factory.CreateClient();

            var challenge = await webhook.GetStringAsync("/storage/notifications/dropbox?challenge=echo-me", ct);
            using var unsigned = await webhook.PostAsync("/storage/notifications/dropbox", new ByteArrayContent(body), ct);
            using var signedNotice = new HttpRequestMessage(HttpMethod.Post, "/storage/notifications/dropbox") { Content = new ByteArrayContent(body) };
            signedNotice.Headers.Add("X-Dropbox-Signature", signature);
            using var signed = await webhook.SendAsync(signedNotice, ct);

            Assert.Equal("echo-me", challenge);
            Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
            Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
            await WaitForFilesAsync(libraries, user.Id, 2, ct);
        }
    }
}
