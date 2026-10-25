using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Tests;

public sealed class ApiTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly WebApplicationFactory<Program> _factory;

    public ApiTests() =>
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("Server:DataRoot", _folder.Path);
            host.ConfigureServices(services => services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestSession>(AuthSetup.SessionScheme + "-test", _ => { }));
            host.ConfigureServices(services => services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultAuthenticateScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultChallengeScheme = AuthSetup.SessionScheme + "-test";
            }));
        });

    public void Dispose()
    {
        _factory.Dispose();
        _folder.Dispose();
    }

    HttpClient Client(User? user = null)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add(TestSession.Header, $"{user.Id};{user.Name};{user.IsAdmin}");
        return client;
    }

    async Task<User> AddUserAsync(bool admin)
    {
        var accounts = _factory.Services.GetRequiredService<AccountService>();
        var (code, _) = await accounts.CreateInviteAsync(null, null, admin);
        var outcome = await accounts.SignInAsync(new ExternalIdentity("google", Guid.NewGuid().ToString(), "Dana", null), code, null);
        return outcome.User!;
    }

    [Fact]
    public async Task Without_a_session_the_API_answers_401()
    {
        using var response = await Client().GetAsync("/api/me", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_describes_the_signed_in_user()
    {
        var user = await AddUserAsync(admin: false);

        var me = await Client(user).GetFromJsonAsync<CurrentUser>("/api/me", TestContext.Current.CancellationToken);

        Assert.Equal(user.Id, me!.UserId);
        Assert.Equal("google", Assert.Single(me.SignIns).Provider);
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
    public async Task A_change_without_the_client_header_is_refused_as_cross_site()
    {
        var admin = await AddUserAsync(admin: true);
        var client = Client(admin);

        using var bare = await client.PostAsJsonAsync("/api/invites", new CreateInvite("x"), TestContext.Current.CancellationToken);
        client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
        using var withHeader = await client.PostAsJsonAsync("/api/invites", new CreateInvite("x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withHeader.StatusCode);
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
    public async Task An_unknown_API_path_is_404_not_the_web_page()
    {
        var user = await AddUserAsync(admin: false);

        using var response = await Client(user).GetAsync("/api/nothing-here", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task With_no_providers_configured_sign_in_lists_none()
    {
        var providers = await Client().GetFromJsonAsync<List<Bibliotaph.Api.Contracts.SignInProvider>>("/auth/providers", TestContext.Current.CancellationToken);

        Assert.Empty(providers!);
    }

    [Fact]
    public async Task The_health_check_needs_no_session()
    {
        using var response = await Client().GetAsync("/healthz", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Signs in whoever the test header names, in place of the session cookie.</summary>
    sealed class TestSession(IOptionsMonitor<AuthenticationSchemeOptions> options, Microsoft.Extensions.Logging.ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Header = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(Header, out var value)) return Task.FromResult(AuthenticateResult.NoResult());
            var parts = value.ToString().Split(';');
            var user = new User { Id = parts[0], Name = parts[1], IsAdmin = bool.Parse(parts[2]) };
            var principal = AuthSetup.SessionPrincipal(user);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(principal.Identities), Scheme.Name)));
        }
    }
}
