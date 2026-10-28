using System.Security.Claims;
using System.Text.Encodings.Web;
using Bibliotaph.Catalog;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Ai;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// The server through its API, each test with its own server and data folder. The tests are split into classes by
/// area so that xUnit runs the classes side by side; one class ran them one after another.
/// </summary>
public abstract partial class ApiTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly FakeGraph _graph = new();
    readonly FakeDropbox _dropbox = new();
    readonly FakeModelService _models = new();
    readonly SyntheticPdfs _pdfs;
    readonly WebApplicationFactory<Program> _factory;

    protected ApiTests(SyntheticPdfs pdfs)
    {
        _pdfs = pdfs;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.UseSetting("Server:DataRoot", _folder.Path);
            host.ConfigureServices(services => services.AddSingleton<IHttpClientFactory>(new FakeHttp(new FakeHosts(_graph, _dropbox))));
            host.ConfigureServices(services => services.AddSingleton(new ModelClientFactory(_models.ClientFor, TimeSpan.FromMilliseconds(50))));
            host.ConfigureServices(services => services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestSession>(AuthSetup.SessionScheme + "-test", _ => { }));
            host.ConfigureServices(services => services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultAuthenticateScheme = AuthSetup.SessionScheme + "-test";
                o.DefaultChallengeScheme = AuthSetup.SessionScheme + "-test";
            }));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _folder.Dispose();
        GC.SuppressFinalize(this);
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

    static async Task WaitForFilesAsync(UserLibraries libraries, string userId, int files, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            await using (var lease = await libraries.OpenAsync(userId, timeout.Token))
                if ((await lease.Library.Get<LibraryStore>().GetCountsAsync(timeout.Token)).Files >= files) return;
            await Task.Delay(50, timeout.Token);
        }
    }

    async Task<long> FirstDocumentAsync(string userId, CancellationToken ct)
    {
        var libraries = _factory.Services.GetRequiredService<UserLibraries>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            await using (var lease = await libraries.OpenAsync(userId, timeout.Token))
                if (await lease.Library.Get<LibraryStore>().GetRelativePathsAsync(timeout.Token) is [var first, ..])
                    return first.DocumentId;
            await Task.Delay(50, timeout.Token);
        }
    }

    static byte[] Png(SkiaSharp.SKColor? color = null)
    {
        using var bitmap = new SkiaSharp.SKBitmap(600, 400);
        bitmap.Erase(color ?? SkiaSharp.SKColors.SeaGreen);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A PC connected to the user's web library, as the Windows app connects, with its key.</summary>
    async Task<(WebLibrary Pc, string Key)> ConnectPcAsync(HttpClient web, CancellationToken ct)
    {
        var path = Path.Combine(_folder.Path, $"pc-{Guid.NewGuid():N}.db");
        var catalog = new CatalogDatabase(path, Path.Combine(_folder.Path, "pc-backups"));
        await using (var db = catalog.CreateContext()) await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database, ct);
        var settings = new SettingsStore(new CatalogFile(path));
        await settings.SetAsync(SettingKeys.WebAddress, "http://localhost", ct);
        var key = new MemoryWebLibraryKey();
        var pc = new WebLibrary(new HttpClient(_factory.Server.CreateHandler()), key, settings, TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebLibrary>.Instance);
        var started = (await pc.StartAsync("DESKTOP-1", ct))!;
        var waiting = pc.WaitAsync(started, ct);
        (await web.PostAsync($"/api/devices/connect/{started.Code}", null, ct)).EnsureSuccessStatusCode();
        Assert.Equal(WebConnectOutcome.Connected, await waiting);
        return (pc, key.Find()!);
    }

    static async Task<T> WaitAsync<T>(Func<Task<T?>> done, CancellationToken ct) where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        while (true)
        {
            if (await done() is { } result) return result;
            await Task.Delay(100, timeout.Token);
        }
    }

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
