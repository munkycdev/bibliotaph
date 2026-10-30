using Azure.Identity;
using Azure.Storage.Blobs;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Ai;
using Bibliotaph.Server.Api;
using Bibliotaph.Server.Auth;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Processing;
using Bibliotaph.Server.Indexing;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Site;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server;

/// <summary>Registration and the request pipeline, shared with the tests' WebApplicationFactory.</summary>
public static class ServerSetup
{
    public static IServiceCollection AddBibliotaphServer(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<ServerOptions>(configuration.GetSection(ServerOptions.Section));
        var options = configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
        var root = Path.GetFullPath(options.DataRoot, environment.ContentRootPath);
        var paths = new ServerPaths(root);

        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AccountsDatabase(paths.Accounts));
        // Invite-only unless Accounts:SignUp:Open is set; then anyone who signs in, up to Accounts:SignUp:MostAccounts.
        var signUp = configuration.GetSection(SignUpOptions.Section).Get<SignUpOptions>() ?? new SignUpOptions();
        services.AddSingleton(sp => new AccountService(sp.GetRequiredService<AccountsDatabase>(), sp.GetRequiredService<TimeProvider>(), signUp));
        if (string.IsNullOrEmpty(options.ReplicaUrl)) services.AddSingleton<IDatabaseRestore, NoRestore>();
        else services.AddSingleton<IDatabaseRestore, LitestreamRestore>();
        // One PDF worker reads for every library in turn, within the container's memory. The books are anyone's, so it
        // may read only the books being read, never a library or the server's secrets.
        services.AddSingleton(sp => new PdfWorkerPool(new PdfWorkerPoolOptions { ConfinedTo = [paths.Downloads, paths.Extracts] },
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(sp => new LibraryHost(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<UserDriveFactory>(), paths.Downloads, new WorkerPdfEngine(sp.GetRequiredService<PdfWorkerPool>()),
            new SkiaImageCodec(), sp.GetRequiredService<ICoverStore>(), sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<ModelClientFactory>(), sp.GetRequiredService<ForegroundActivity>(), sp.GetRequiredService<UploadMemory>(),
            sp.GetRequiredService<IBookCatalogue>()));
        // One for every library and connected PC, so the catalogues' paces hold server-wide.
        services.AddSingleton(sp => BookCatalogues(configuration, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new UploadMemory(sp.GetRequiredService<TimeProvider>(), options.UploadMemoryBytes));
        // Every library's reading steps aside for anyone's request: they share the one CPU.
        services.AddSingleton(sp => new ForegroundActivity(sp.GetRequiredService<TimeProvider>()));
        // Model services only on the internet, over https; tests put a stand-in here.
        services.AddSingleton(new ModelClientFactory(ModelServices.ClientFor));
        services.AddSingleton<UserLibraries>();
        services.AddHostedService<IdleLibraries>();
        // Only where the libraries come back from Blob storage after a deploy; on a developer's disk they're already there.
        if (!string.IsNullOrEmpty(options.ReplicaUrl)) services.AddHostedService<LibraryWarmup>();
        services.AddBibliotaphStorage(configuration);

        var credential = new Lazy<ManagedIdentityCredential>(() => new ManagedIdentityCredential(options.ManagedIdentityClientId is { Length: > 0 } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId)
            : ManagedIdentityId.SystemAssigned));
        if (options.CoverContainer is { Length: > 0 } covers)
            services.AddSingleton<ICoverStore>(new BlobCoverStore(new BlobContainerClient(new Uri(covers), credential.Value)));
        else services.AddSingleton<ICoverStore, NoCoverStore>();
        var kept = new List<(BlobContainerClient, string)>();
        if (options.ReplicaUrl is { Length: > 0 } replicaUrl)
        {
            var (replicaContainer, replicaRoot) = BlobUserBlobs.Replica(replicaUrl);
            kept.Add((new BlobContainerClient(replicaContainer, credential.Value), replicaRoot));
        }
        if (options.CoverContainer is { Length: > 0 } coverContainer) kept.Add((new BlobContainerClient(new Uri(coverContainer), credential.Value), ""));
        if (BackupContainer(options) is { } backupContainer)
        {
            var container = new BlobContainerClient(backupContainer, credential.Value);
            services.AddSingleton<IBackupStore>(new BlobBackupStore(container));
            kept.Add((container, ""));
        }
        else services.AddSingleton<IBackupStore>(new DiskBackupStore(paths));
        services.AddSingleton<WebBackups>();
        services.AddHostedService<WebBackups.Weekly>();
        if (kept.Count > 0) services.AddSingleton<IUserBlobs>(new BlobUserBlobs(kept));
        else services.AddSingleton<IUserBlobs, NoUserBlobs>();
        services.AddSingleton<AccountPurge>();
        services.AddHostedService(sp => sp.GetRequiredService<AccountPurge>());
        services.AddSingleton<AccountClosing>();
        services.AddSingleton<DeviceService>();
        services.AddSingleton<PcActivityBoard>();

        var keys = services.AddDataProtection().SetApplicationName("bibliotaph");
        if (options.KeyRingBlob is { Length: > 0 } blob && options.KeyRingKey is { Length: > 0 } key)
            keys.PersistKeysToAzureBlobStorage(new Uri(blob), credential.Value).ProtectKeysWithAzureKeyVault(new Uri(key), credential.Value);
        else keys.PersistKeysToFileSystem(new DirectoryInfo(paths.Keys));

        services.AddBibliotaphAuth(configuration);
        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            // Container Apps' ingress ends TLS and forwards plain HTTP; without this, sign-in would send http:// callbacks.
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
        });
        services.AddProblemDetails();
        return services;
    }

    /// <summary>Where the server's lookups ask, one shared by every library: RPGGeek when Lookups:RpgGeek:Token holds a
    /// token, Open Library, then Google Books when Lookups:GoogleBooks:ApiKey holds a key.</summary>
    static IBookCatalogue BookCatalogues(IConfiguration configuration, TimeProvider clock)
    {
        List<IBookCatalogue> catalogues = [];
        // The order the user prefers their records in: RPGGeek, then Open Library, then Google Books.
        if (configuration["Lookups:RpgGeek:Token"] is { Length: > 0 } token)
            catalogues.Add(new RpgGeekCatalogue(RpgGeekCatalogue.CreateClient(PublicNetwork.Handler()), token, clock));
        catalogues.Add(new OpenLibraryCatalogue(OpenLibraryCatalogue.CreateClient(PublicNetwork.Handler()), clock));
        if (configuration["Lookups:GoogleBooks:ApiKey"] is { Length: > 0 } key)
            catalogues.Add(new GoogleBooksCatalogue(GoogleBooksCatalogue.CreateClient(PublicNetwork.Handler()), key, clock));
        return catalogues.Count == 1 ? catalogues[0] : new BookCatalogues(catalogues);
    }

    /// <summary>Where backups are kept in Blob storage: the container set, or the one named backups beside the covers.</summary>
    static Uri? BackupContainer(ServerOptions options)
    {
        if (options.BackupContainer is { Length: > 0 } set) return new Uri(set);
        if (options.CoverContainer is not { Length: > 0 } covers) return null;
        var account = new Uri(covers);
        return new Uri(account, "/backups");
    }

    public static WebApplication UseBibliotaphServer(this WebApplication app)
    {
        // Anything left in the downloads folder is from before a crash: no book is kept beyond its reading. Here rather
        // than at registration, which the invite command shares while the server runs beside it.
        var downloads = app.Services.GetRequiredService<ServerPaths>().Downloads;
#pragma warning disable RS0030 // The server's own scratch folder; books are never local files here.
        if (Directory.Exists(downloads)) Directory.Delete(downloads, recursive: true);
#pragma warning restore RS0030

        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            // The reader reads a book's byte ranges straight from OneDrive's download links (personal and business).
            headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data: blob:; " +
                "font-src 'self'; connect-src 'self' https://*.sharepoint.com https://*.1drv.com https://*.microsoftpersonalcontent.com; " +
                "worker-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            await next(context);
        });

        app.UseForegroundRequests();
        app.UseAuthentication();
        app.UseClosedAccountGuard();
        app.UseCrossSiteGuard();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapGet("/healthz", () => Results.Text("ok"));
        app.MapAuth(AuthSetup.Auth0(app.Configuration),
            developerSignIn: app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Auth:DeveloperSignIn"));
        app.MapApi();
        app.MapStorage();
        app.MapSite();

        // The Blazor WebAssembly client: its files, and its page for every other path so its router can take over.
        app.MapStaticAssets();
        app.MapFallbackToFile("index.html");
        return app;
    }
}

/// <summary>The invite command line: prints a one-time invitation link.</summary>
sealed record InviteCommand(string? Note, bool Admin)
{
    public static InviteCommand Parse(string[] args)
    {
        string? note = null;
        var admin = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--admin") admin = true;
            else if (args[i] == "--note" && i + 1 < args.Length) note = args[++i];
            else throw new ArgumentException($"Unknown argument {args[i]}. Usage: invite [--admin] [--note <text>]");
        }
        return new InviteCommand(note, admin);
    }

    public async Task<int> RunAsync(IServiceProvider services)
    {
        var (code, invite) = await services.GetRequiredService<AccountService>().CreateInviteAsync(null, Note, Admin);
        var options = services.GetRequiredService<IOptions<ServerOptions>>().Value;
        Console.WriteLine(ApiEndpoints.InviteLink(options, null, code));
        Console.WriteLine($"Expires {invite.Expires:yyyy-MM-dd HH:mm} UTC{(Admin ? "; the account it makes is an admin" : "")}.");
        return 0;
    }
}
