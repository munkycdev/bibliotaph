using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Signing in, accounts and a new user's empty library.</summary>
    public sealed class AccountsTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task The_root_is_the_landing_page_for_everyone_and_each_site_page_links_the_rest()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);

            var landing = await Client().GetStringAsync("/", ct);
            var signedIn = await Client(user).GetStringAsync("/", ct);
            using var app = await Client(user).GetAsync("/home", ct);
            var pages = new Dictionary<string, string>();
            foreach (var path in new[] { "/features", "/about", "/changelog", "/terms", "/privacy", "/licences", "/contact" })
            {
                using var response = await Client().GetAsync(path, ct);
                Assert.Equal((path, HttpStatusCode.OK, "text/html"), (path, response.StatusCode, response.Content.Headers.ContentType?.MediaType));
                pages[path] = await response.Content.ReadAsStringAsync(ct);
            }
            var openLibrary = await Client(user).GetStringAsync("/features", ct);
            using var oldPrivacy = await Client().GetAsync("/privacy.html", ct);
            using var picture = await Client().GetAsync("/site/library.png", ct);

            Assert.Contains("The exact page you need.", landing, StringComparison.Ordinal);
            Assert.Contains("""href="/signin">Sign in""", landing, StringComparison.Ordinal);
            // Its icons are drawn in place, none left as a placeholder.
            Assert.Contains("""<svg class="bt-ico" """, landing, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", landing, StringComparison.Ordinal);
            // Signed in, the root is still the landing page, with the way into the app where Sign in was; the app is at /home.
            Assert.Contains("The exact page you need.", signedIn, StringComparison.Ordinal);
            Assert.Contains("""href="/home">Open your library""", signedIn, StringComparison.Ordinal);
            Assert.DoesNotContain("The exact page you need.", await app.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.Contains("Open your library", openLibrary, StringComparison.Ordinal);
            Assert.All(pages.Values, html => Assert.Contains("""<a href="/terms""", html, StringComparison.Ordinal));
            Assert.Contains("""<a href="/terms" aria-current="page">""", pages["/terms"], StringComparison.Ordinal);
            // The footer's tagline is one of the app's, and the version is the one running.
            Assert.All(pages.Values, html => Assert.Contains(Footer.Taglines, t => html.Contains($"""<p class="bt-tagline">{WebUtility.HtmlEncode(t)}</p>""", StringComparison.Ordinal)));
            Assert.All(pages.Values, html => Assert.Contains($"Version {Footer.Version} ·", html, StringComparison.Ordinal));
            // The change log and licences come from the repository's own files.
            Assert.Contains("New in 1.3.590 (19 October 2026):", pages["/changelog"], StringComparison.Ordinal);
            Assert.Contains("Windows SDK projection", pages["/licences"], StringComparison.Ordinal);
            Assert.Contains("Litestream", pages["/licences"], StringComparison.Ordinal);
            Assert.Contains("Privacy policy", pages["/privacy"], StringComparison.Ordinal);
            Assert.Equal(("/privacy", HttpStatusCode.MovedPermanently), (oldPrivacy.Headers.Location?.OriginalString, oldPrivacy.StatusCode));
            Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public async Task Without_a_session_the_API_answers_401()
        {
            using var response = await Client().GetAsync("/api/me", TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Asking_whether_the_library_is_open_opens_it_without_waiting()
        {
            var ct = TestContext.Current.CancellationToken;
            var client = Client(await AddUserAsync(admin: false));

            var opening = (await client.GetFromJsonAsync<LibraryOpening>("/api/library/opening", ct))!;
            while (!opening.Ready)
            {
                await Task.Delay(50, ct);
                opening = (await client.GetFromJsonAsync<LibraryOpening>("/api/library/opening", ct))!;
            }

            Assert.Null(opening.Step);
            using var signedOut = await Client().GetAsync("/api/library/opening", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
        }

        [Fact]
        public async Task Closing_an_account_deletes_its_library_and_ends_its_sessions()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            (await client.GetAsync("/api/library/summary", ct)).EnsureSuccessStatusCode();
            var folder = _factory.Services.GetRequiredService<ServerPaths>().UserRoot(user.Id);
            Assert.True(Directory.Exists(folder));

            using var closed = await client.DeleteAsync("/api/me", ct);

            closed.EnsureSuccessStatusCode();
            Assert.Equal("/", (await closed.Content.ReadFromJsonAsync<SignedOut>(ct))!.Next);
            Assert.False(Directory.Exists(folder));
            Assert.Equal(0, _factory.Services.GetRequiredService<UserLibraries>().OpenCount);
            using var after = await client.GetAsync("/api/library/summary", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            Assert.False(Directory.Exists(folder));
        }

        [Fact]
        public async Task An_admin_deletes_someone_elses_account_but_not_their_own()
        {
            var ct = TestContext.Current.CancellationToken;
            var admin = await AddUserAsync(admin: true);
            var user = await AddUserAsync(admin: false);
            var theirs = Client(user);
            (await theirs.GetAsync("/api/library/summary", ct)).EnsureSuccessStatusCode();
            var folder = _factory.Services.GetRequiredService<ServerPaths>().UserRoot(user.Id);
            var client = Client(admin);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            var listed = await client.GetFromJsonAsync<List<AccountInfo>>("/api/accounts", ct);
            using var own = await client.DeleteAsync($"/api/accounts/{admin.Id}", ct);
            using var deleted = await client.DeleteAsync($"/api/accounts/{user.Id}", ct);

            Assert.Contains(listed!, a => a.Id == admin.Id && a.IsYou);
            Assert.Contains(listed!, a => a.Id == user.Id && !a.IsYou);
            Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
            deleted.EnsureSuccessStatusCode();
            Assert.StartsWith("Deleted Dana's account", (await deleted.Content.ReadFromJsonAsync<ActionResult>(ct))!.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(folder));
            using var after = await theirs.GetAsync("/api/library/summary", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
            Assert.DoesNotContain(await client.GetFromJsonAsync<List<AccountInfo>>("/api/accounts", ct) ?? [], a => a.Id == user.Id);
        }

        [Fact]
        public async Task Only_an_admin_may_list_or_delete_accounts()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var other = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            using var listed = await client.GetAsync("/api/accounts", ct);
            using var deleted = await client.DeleteAsync($"/api/accounts/{other.Id}", ct);

            Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
        }

        [Fact]
        public async Task Me_describes_the_signed_in_user()
        {
            var user = await AddUserAsync(admin: false);

            var me = await Client(user).GetFromJsonAsync<CurrentUser>("/api/me", TestContext.Current.CancellationToken);

            Assert.Equal(user.Id, me!.UserId);
            Assert.Equal("Google", Assert.Single(me.SignIns).Provider);
        }

        [Fact]
        public async Task A_new_users_library_is_empty()
        {
            var user = await AddUserAsync(admin: false);

            var summary = await Client(user).GetFromJsonAsync<LibrarySummary>("/api/library/summary", TestContext.Current.CancellationToken);

            Assert.Equal(new LibrarySummary(0, 0), summary);
            Assert.True(File.Exists(Path.Combine(_folder.Path, "users", user.Id, "catalog.db")));
        }

        [Fact]
        public async Task A_new_users_library_page_asks_for_a_folder()
        {
            var user = await AddUserAsync(admin: false);

            var results = await Client(user).GetFromJsonAsync<LibraryResults>("/api/library?sort=Title&kind=Books&level=3&unknownLevels=true",
                TestContext.Current.CancellationToken);

            Assert.Empty(results!.Books);
            Assert.Equal("add-folder", results.Empty?.Action);
            Assert.Equal("All systems", Assert.Single(results.Menus.Systems).Label);
        }

        [Fact]
        public async Task Only_an_admin_may_invite()
        {
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            using var response = await client.PostAsJsonAsync("/api/invites", new CreateInvite("x"), TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Fact]
        public async Task An_invitation_link_points_at_the_join_page()
        {
            var admin = await AddUserAsync(admin: true);
            var client = Client(admin);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            using var response = await client.PostAsJsonAsync("/api/invites", new CreateInvite("for a friend"), TestContext.Current.CancellationToken);
            var created = await response.Content.ReadFromJsonAsync<CreatedInvite>(TestContext.Current.CancellationToken);

            Assert.Contains("/join?invite=", created!.Link, StringComparison.Ordinal);
            var listed = await client.GetFromJsonAsync<List<InviteInfo>>("/api/invites", TestContext.Current.CancellationToken);
            Assert.Contains(listed!, i => i.Note == "for a friend");
        }

        [Fact]
        public async Task With_no_providers_configured_sign_in_lists_none()
        {
            var providers = await Client().GetFromJsonAsync<List<Bibliotaph.Api.Contracts.SignInProvider>>("/auth/providers", TestContext.Current.CancellationToken);

            Assert.Empty(providers!);
        }

        [Fact]
        public async Task With_Auth0_set_up_sign_in_is_one_button_and_signing_out_ends_its_session_too()
        {
            var ct = TestContext.Current.CancellationToken;
            using var factory = _factory.WithWebHostBuilder(host =>
            {
                host.UseSetting("Auth:auth0:Domain", "tenant.example.auth0.com");
                host.UseSetting("Auth:auth0:ClientId", "abc");
                host.UseSetting("Auth:auth0:ClientSecret", "secret");
                host.UseSetting("Server:PublicOrigin", "https://bibliotaph.online");
            });
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var providers = await client.GetFromJsonAsync<List<Bibliotaph.Api.Contracts.SignInProvider>>("/auth/providers", ct);
            using var direct = await client.GetAsync("/auth/signin/google", ct);
            using var unguarded = await client.PostAsync("/auth/signout", null, ct);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using var signOut = await client.PostAsync("/auth/signout", null, ct);
            var signedOut = await signOut.Content.ReadFromJsonAsync<SignedOut>(ct);

            Assert.Equal([new Bibliotaph.Api.Contracts.SignInProvider("auth0", "Continue")], providers);
            Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, unguarded.StatusCode);
            Assert.Equal("https://tenant.example.auth0.com/v2/logout?client_id=abc&returnTo=https%3A%2F%2Fbibliotaph.online%2F", signedOut!.Next);
        }
    }
}
