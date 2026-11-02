using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Nornis;
using Bibliotaph.Server.Storage;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Nornis;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Connect Nornis, and Nornis's tokens for the Bibliotaph API.</summary>
    public sealed partial class NornisTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        const string Return = FakeNornis.Origin + "/settings/connected-apps";
        const string Subject = "oauth2|discord|5678";

        readonly FakeNornis _nornis = new();

        static CancellationToken Ct => TestContext.Current.CancellationToken;

        /// <summary>The server with Nornis configured, Auth0's keys and endpoints standing in for the tenant's.</summary>
        WebApplicationFactory<Program> Server(bool configured = true) => _factory.WithWebHostBuilder(host =>
        {
            if (configured)
            {
                host.UseSetting("Auth:auth0:Domain", "auth.test");
                host.UseSetting("Auth:auth0:ClientId", "bibliotaph-client");
                host.UseSetting("Auth:auth0:ClientSecret", "bibliotaph-secret");
                host.UseSetting("Nornis:Audience", NornisTokens.Audience);
                host.UseSetting("Nornis:Origin", FakeNornis.Origin);
                host.UseSetting("Nornis:ApiBase", FakeNornis.ApiBase);
                host.UseSetting("Storage:Nornis:Audience", "https://api.nornis.test");
            }
            host.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(new FakeHttp(new NornisFirst(_nornis, new FakeHosts(_graph, _dropbox))));
                services.PostConfigure<JwtBearerOptions>(NornisAuthentication.Scheme, options =>
                {
                    var tenant = new OpenIdConnectConfiguration { Issuer = NornisTokens.Issuer };
                    tenant.SigningKeys.Add(NornisTokens.Key);
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(tenant);
                });
                services.PostConfigure<OpenIdConnectOptions>(Auth0SignIn.Id, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                    {
                        Issuer = NornisTokens.Issuer,
                        AuthorizationEndpoint = "https://auth.test/authorize",
                        TokenEndpoint = "https://auth.test/oauth/token",
                    }));
            });
        });

        /// <summary>A game master who connected from Nornis: their account, its Auth0 sign-in and, unless not, the storage connection.</summary>
        static async Task<(User User, ConnectedAccount? Connection)> GameMasterAsync(WebApplicationFactory<Program> server, bool connected = true)
        {
            var user = (await server.Services.GetRequiredService<AccountService>().SignInFromNornisAsync(
                new ExternalIdentity(Auth0SignIn.Id, Subject, "Dana", null), null, Ct)).User!;
            var connection = connected
                ? await server.Services.GetRequiredService<StorageConnections>().SaveAsync(user.Id, NornisProvider.Key, FakeNornis.UserId, "Nornis · Dana",
                    null, "refresh", "openid offline_access published:read", Ct)
                : null;
            return (user, connection);
        }

        static HttpClient Nornis(WebApplicationFactory<Program> server, string token)
        {
            var client = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "nornis");
            return client;
        }

        static HttpClient Browser(WebApplicationFactory<Program> server, User? user = null)
        {
            var client = server.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            if (user is not null) client.DefaultRequestHeaders.Add(TestSession.Header, $"{user.Id};{user.Name};{user.IsAdmin}");
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            return client;
        }

        static async Task<string?> CodeAsync(HttpResponseMessage response)
        {
            if (response.Content.Headers.ContentType?.MediaType != "application/problem+json") return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }

        [Fact]
        public async Task A_nornis_token_reaches_only_its_own_endpoints_and_only_with_their_scope()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server);

            using var warm = await Nornis(server, NornisTokens.For(Subject, "books:read")).PostAsync("/api/nornis/v1/warm", null, Ct);
            using var noScope = await Nornis(server, NornisTokens.For(Subject, "tickets:write")).PostAsync("/api/nornis/v1/warm", null, Ct);
            using var otherAudience = await Nornis(server, NornisTokens.For(Subject, audience: "https://api.nornis.test")).PostAsync("/api/nornis/v1/warm", null, Ct);
            var forged = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("somebody else's key that is also long enough to sign"));
            using var otherKey = await Nornis(server, NornisTokens.For(Subject, key: forged)).PostAsync("/api/nornis/v1/warm", null, Ct);
            using var library = await Nornis(server, NornisTokens.For(Subject)).GetAsync("/api/storage", Ct);
            using var session = await Browser(server, user).PostAsync("/api/nornis/v1/warm", null, Ct);

            Assert.Equal(HttpStatusCode.Accepted, warm.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, noScope.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, otherAudience.StatusCode);
            Assert.Null(await CodeAsync(otherAudience));
            Assert.Equal(HttpStatusCode.Unauthorized, otherKey.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, library.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        }

        [Fact]
        public async Task Nornis_is_told_to_reconnect_once_the_account_or_its_connection_is_gone()
        {
            using var server = Server();
            var (user, connection) = await GameMasterAsync(server);
            var connections = server.Services.GetRequiredService<StorageConnections>();

            // The login was saved as Discord's custom connection spells it; Auth0's built-in one spells it without oauth2|.
            using var formerSpelling = await Nornis(server, NornisTokens.For("discord|5678")).PostAsync("/api/nornis/v1/warm", null, Ct);
            using var stranger = await Nornis(server, NornisTokens.For("google-oauth2|999")).PostAsync("/api/nornis/v1/warm", null, Ct);
            await connections.SetNeedsReconnectAsync(connection!.Id, Ct);
            using var needsReconnect = await Nornis(server, NornisTokens.For(Subject)).PostAsync("/api/nornis/v1/warm", null, Ct);
            await connections.RemoveAsync(user.Id, connection.Id, Ct);
            using var disconnected = await Nornis(server, NornisTokens.For(Subject)).PostAsync("/api/nornis/v1/warm", null, Ct);
            await connections.SaveAsync(user.Id, NornisProvider.Key, FakeNornis.UserId, null, null, "refresh", null, Ct);
            await server.Services.GetRequiredService<AccountService>().CloseAsync(user.Id, Ct);
            using var closed = await Nornis(server, NornisTokens.For(Subject)).PostAsync("/api/nornis/v1/warm", null, Ct);

            Assert.Equal(HttpStatusCode.Accepted, formerSpelling.StatusCode);
            foreach (var refused in new[] { stranger, needsReconnect, disconnected, closed })
            {
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                Assert.Equal(NornisProblems.NotConnected, await CodeAsync(refused));
            }
        }

        [Fact]
        public async Task A_call_from_Nornis_notes_when_the_connection_was_last_used()
        {
            using var server = Server();
            var (_, connection) = await GameMasterAsync(server);

            (await Nornis(server, NornisTokens.For(Subject, "books:read")).PostAsync("/api/nornis/v1/warm", null, Ct)).EnsureSuccessStatusCode();

            await using var db = server.Services.GetRequiredService<AccountsDatabase>().CreateContext();
            Assert.NotNull((await db.StorageConnections.FindAsync([connection!.Id], Ct))!.LastUsed);
        }

        [Fact]
        public async Task Connect_goes_back_only_to_Nornis_s_own_site()
        {
            using var server = Server();
            using var unconfigured = Server(configured: false);
            var browser = Browser(server);

            using var lookAlike = await browser.GetAsync("/connect/nornis?return=" + Uri.EscapeDataString("https://nornis.test.example.com/back"), Ct);
            using var otherSite = await browser.GetAsync("/connect/nornis?return=" + Uri.EscapeDataString("https://evil.example/back"), Ct);
            using var plainHttp = await browser.GetAsync("/connect/nornis?return=" + Uri.EscapeDataString("http://nornis.test/back"), Ct);
            using var missing = await browser.GetAsync("/connect/nornis", Ct);
            using var signIn = await browser.GetAsync("/connect/nornis?return=" + Uri.EscapeDataString(Return), Ct);
            using var off = await Browser(unconfigured).GetAsync("/connect/nornis?return=" + Uri.EscapeDataString(Return), Ct);

            Assert.All(new[] { lookAlike, otherSite, plainHttp, missing }, r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
            Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
            Assert.StartsWith("https://auth.test/authorize?", signIn.Headers.Location!.ToString(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        }

        [Fact]
        public async Task Connecting_storage_asks_Auth0_for_Nornis_s_API_and_comes_back_to_finish()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server, connected: false);

            using var grant = await Browser(server, user).GetAsync("/connect/nornis/storage?return=" + Uri.EscapeDataString(Return), Ct);
            using var anonymous = await Browser(server).GetAsync("/connect/nornis/storage?return=" + Uri.EscapeDataString(Return), Ct);

            Assert.Equal(HttpStatusCode.Redirect, grant.StatusCode);
            var authorize = grant.Headers.Location!;
            Assert.StartsWith("https://auth.test/authorize?", authorize.ToString(), StringComparison.Ordinal);
            var query = QueryHelpers.ParseQuery(authorize.Query);
            Assert.Equal("https://api.nornis.test", query["audience"].ToString());
            Assert.Equal("openid offline_access published:read", query["scope"].ToString());
            Assert.Equal("bibliotaph-client", query["client_id"].ToString());
            Assert.EndsWith("/storage/callback/nornis", query["redirect_uri"].ToString(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        [Fact]
        public async Task Finishing_makes_the_Nornis_collection_once_and_tells_Nornis_how_it_went()
        {
            using var server = Server();
            var (user, _) = await GameMasterAsync(server, connected: false);
            var browser = Browser(server, user);
            var finish = "/connect/nornis/finish?return=" + Uri.EscapeDataString(Return);

            using var notYet = await browser.GetAsync(finish, Ct);
            await server.Services.GetRequiredService<StorageConnections>().SaveAsync(user.Id, NornisProvider.Key, FakeNornis.UserId, null, null, "refresh",
                null, Ct);
            using var done = await browser.GetAsync(finish, Ct);
            using var again = await browser.GetAsync(finish, Ct);

            Assert.Equal(Return + "?bibliotaph=failed", notYet.Headers.Location!.ToString());
            Assert.Equal(Return + "?bibliotaph=connected", done.Headers.Location!.ToString());
            Assert.Equal(Return + "?bibliotaph=connected", again.Headers.Location!.ToString());
            var libraries = server.Services.GetRequiredService<UserLibraries>();
            long first;
            await using (var lease = await libraries.OpenAsync(user.Id, Ct))
            {
                var nornis = Assert.Single(await lease.Library.Get<CollectionsService>().ListAsync(Ct), c => c.Name == "Nornis");
                Assert.Null(nornis.ParentId);
                first = nornis.Id;
                await lease.Library.Get<CollectionsService>().DeleteAsync(first, Ct);
            }
            (await browser.GetAsync(finish, Ct)).Dispose();
            await using (var lease = await libraries.OpenAsync(user.Id, Ct))
                Assert.NotEqual(first, Assert.Single(await lease.Library.Get<CollectionsService>().ListAsync(Ct), c => c.Name == "Nornis").Id);
        }

        [Fact]
        public async Task Settings_describes_Nornis_and_leaves_connecting_it_to_Nornis()
        {
            using var server = Server();
            using var unconfigured = Server(configured: false);
            var (user, _) = await GameMasterAsync(server);

            var providers = await Browser(server, user).GetFromJsonAsync<List<StorageProviderInfo>>("/api/storage/providers", Ct);
            var storage = await Browser(server, user).GetFromJsonAsync<List<StorageAccount>>("/api/storage", Ct);
            var without = await Browser(unconfigured, user).GetFromJsonAsync<List<StorageProviderInfo>>("/api/storage/providers", Ct);

            var nornis = Assert.Single(providers!, p => p.Id == NornisProvider.Key);
            Assert.True(nornis.StartsInItsOwnApp);
            Assert.StartsWith("Nornis can search and share books", nornis.About, StringComparison.Ordinal);
            Assert.Equal(["read the books you uploaded to Nornis", "keep access while you're signed out"], Assert.Single(storage!).Access);
            Assert.DoesNotContain(without!, p => p.Id == NornisProvider.Key);
        }

        [Fact]
        public async Task Connecting_storage_names_the_account_by_Nornis_s_own_user()
        {
            using var server = Server();
            var provider = (OAuthStorageProvider)StorageProviders.Find(NornisProvider.Key, server.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>())!;

            var identity = await provider.IdentifyAsync(server.Services.GetRequiredService<IHttpClientFactory>().CreateClient(NornisProvider.Key),
                FakeNornis.AccessToken, Ct);

            Assert.Equal(new StorageIdentity(FakeNornis.UserId, "Nornis · Dana", null), identity);
        }

        [Fact]
        public async Task A_Nornis_drive_lists_worlds_and_their_files_across_pages_and_reads_a_range()
        {
            for (var i = 1; i <= 3; i++) _nornis.AddWorld($"world-{i}", $"World {i}");
            for (var i = 1; i <= 5; i++) _nornis.AddFile("world-2", $"file-{i}", $"Book {i}.pdf", Encoding.ASCII.GetBytes($"%PDF-1.7 book {i}"));
            var refused = 0;
            var drive = new NornisDrive(new NornisClient(new HttpClient(_nornis), new Uri(FakeNornis.ApiBase), _ => Task.FromResult(FakeNornis.AccessToken),
                _ => { refused++; return Task.CompletedTask; }));

            var worlds = await drive.ChildrenAsync("", Ct);
            var files = await drive.ChildrenAsync("world-2", Ct);
            var file = await drive.ItemAsync("world-2/Book 4.pdf", Ct);
            using var range = await drive.OpenReadAsync("world-2/Book 4.pdf", new ByteRange(9, 12), Ct);
            var link = await drive.ReadLinkAsync("world-2/Book 4.pdf", Ct);
            var noWorld = await drive.ChildrenAsync("world-9", Ct);
            var noFile = await drive.ItemAsync("world-2/Missing.pdf", Ct);
            _nornis.Refuses = true;
            await Assert.ThrowsAsync<StorageException>(() => drive.ChildrenAsync("", Ct));

            Assert.Equal(["world-1", "world-2", "world-3"], worlds!.Select(w => w.Name));
            Assert.All(worlds!, w => Assert.True(w.IsFolder));
            Assert.Equal(["Book 1.pdf", "Book 2.pdf", "Book 3.pdf", "Book 4.pdf", "Book 5.pdf"], files!.Select(f => f.Name));
            Assert.Equal(new StorageEntry("Book 4.pdf", false, 15, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc)), file);
            Assert.Equal("book", new StreamReader(range).ReadToEnd());
            Assert.Equal($"https://{FakeNornis.BlobHost}/file-4?sig=read", link);
            Assert.Null(noWorld);
            Assert.Null(noFile);
            Assert.Equal(1, refused);
        }

        /// <summary>Nornis's hosts go to the fake Nornis; everything else as in the other tests.</summary>
        sealed class NornisFirst(FakeNornis nornis, HttpMessageHandler rest) : HttpMessageHandler
        {
            readonly HttpMessageInvoker _nornis = new(nornis, disposeHandler: false);
            readonly HttpMessageInvoker _rest = new(rest, disposeHandler: false);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                (FakeNornis.Handles(request.RequestUri!) ? _nornis : _rest).SendAsync(request, cancellationToken);
        }
    }
}
