using Azure.Identity;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Api;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
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
        services.AddSingleton<AccountService>();
        if (string.IsNullOrEmpty(options.ReplicaUrl)) services.AddSingleton<IDatabaseRestore, NoRestore>();
        else services.AddSingleton<IDatabaseRestore, LitestreamRestore>();
        services.AddSingleton(sp => new LibraryHost(sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<UserDriveFactory>(), paths.Downloads));
        services.AddSingleton<UserLibraries>();
        services.AddHostedService<IdleLibraries>();
        services.AddBibliotaphStorage(configuration);

        var keys = services.AddDataProtection().SetApplicationName("bibliotaph");
        if (options.KeyRingBlob is { Length: > 0 } blob && options.KeyRingKey is { Length: > 0 } key)
        {
            var credential = new ManagedIdentityCredential(options.ManagedIdentityClientId is { Length: > 0 } clientId
                ? ManagedIdentityId.FromUserAssignedClientId(clientId)
                : ManagedIdentityId.SystemAssigned);
            keys.PersistKeysToAzureBlobStorage(new Uri(blob), credential).ProtectKeysWithAzureKeyVault(new Uri(key), credential);
        }
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
            headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' data: blob:; " +
                "font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            await next(context);
        });

        app.UseAuthentication();
        app.UseCrossSiteGuard();
        app.UseAuthorization();

        app.MapGet("/healthz", () => Results.Text("ok"));
        app.MapAuth(AuthSetup.Configured(app.Configuration),
            developerSignIn: app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Auth:DeveloperSignIn"));
        app.MapApi();
        app.MapStorage();
        // The address given to sign-in and storage providers; the page itself is static so it reads without the client.
        app.MapGet("/privacy", () => Results.Redirect("/privacy.html"));

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
