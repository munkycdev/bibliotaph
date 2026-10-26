using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Connecting storage: an OAuth grant of its own for each provider, apart from signing in, so a user can sign in with
/// Discord and read books from OneDrive and Dropbox, or connect two drives. The grant's refresh token goes to
/// accounts.db, encrypted; the browser never sees a token.
/// </summary>
public static class StorageSetup
{
    public static IServiceCollection AddBibliotaphStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient();
        services.AddSingleton<StorageConnections>();
        services.AddSingleton<DriveTokens>();
        services.AddSingleton<UserDriveFactory>();
        services.AddSingleton<DriveNotifications>();
        services.AddHostedService(sp => sp.GetRequiredService<DriveNotifications>());
        services.AddHostedService<DriveWatch>();

        foreach (var provider in StorageProviders.Configured(configuration)) AddProvider(services, provider, provider.App(configuration)!);
        return services;
    }

    static void AddProvider(IServiceCollection services, StorageProvider provider, StorageApp app) =>
        services.AddAuthentication().AddOAuth(provider.Id, provider.Name, options =>
        {
            options.AuthorizationEndpoint = provider.AuthorizationEndpoint;
            options.TokenEndpoint = provider.TokenEndpoint;
            options.ClientId = app.ClientId!;
            options.ClientSecret = app.ClientSecret!;
            options.CallbackPath = $"/storage/callback/{provider.Id}";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.SignInScheme = AuthSetup.ExternalScheme;
            options.Scope.Clear();
            foreach (var scope in provider.Scopes) options.Scope.Add(scope);
            foreach (var (name, value) in provider.AuthorizationParameters) options.AdditionalAuthorizationParameters[name] = value;
            options.Events.OnCreatingTicket = context => SaveConnectionAsync(context, provider);
            // Nothing is signed in: the connection was saved, and the session cookie stays as it was.
            options.Events.OnTicketReceived = context =>
            {
                context.HandleResponse();
                context.Response.Redirect(AuthEndpoints.LocalOrHome(context.ReturnUri));
                return Task.CompletedTask;
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect($"/settings?problem=storage&provider={provider.Id}");
                return Task.CompletedTask;
            };
        });

    static async Task SaveConnectionAsync(OAuthCreatingTicketContext context, StorageProvider provider)
    {
        var services = context.HttpContext.RequestServices;
        // The callback is handled before the request's own user is read, so the session is asked for directly.
        var session = await context.HttpContext.AuthenticateAsync(AuthSetup.SessionScheme);
        var userId = session.Principal?.UserId();
        if (userId is null || context.AccessToken is null || context.RefreshToken is null)
        {
            context.Fail("Connecting storage needs a signed-in user and a refresh token.");
            return;
        }

        var ct = context.HttpContext.RequestAborted;
        var identity = await provider.IdentifyAsync(services.GetRequiredService<UserDriveFactory>().Http(provider), context.AccessToken, ct);
        var connection = await services.GetRequiredService<StorageConnections>().SaveAsync(userId, provider.Id, identity.AccountId,
            identity.DisplayName, identity.AccountKind, context.RefreshToken, string.Join(' ', provider.Scopes), ct);
        services.GetRequiredService<DriveTokens>().Forget(connection.Id);
        await services.GetRequiredService<UserLibraries>().DrivesChangedAsync(userId);
        services.GetRequiredService<DriveNotifications>().Subscribe(connection.Id);
    }

    public static void MapStorage(this IEndpointRouteBuilder app)
    {
        var storage = app.MapGroup("/storage");

        storage.MapGet("/connect/{provider}", (string provider, HttpContext context, string? returnUrl) =>
            StorageProviders.Find(provider) is { } known && known.App(context.RequestServices.GetRequiredService<IConfiguration>()) is not null
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = AuthEndpoints.LocalOrHome(returnUrl ?? "/settings") }, [known.Id])
                : Results.LocalRedirect("/settings?problem=storage"))
            .RequireAuthorization();

        // Graph posts here: first to check the address (it echoes a token back), then whenever a drive changes. It
        // carries no cookie, so it's anonymous; each notification proves itself with its connection's secret.
        storage.MapPost($"/notifications/{OneDriveProvider.Key}", async (HttpContext context, string? validationToken, DriveNotifications notifications) =>
        {
            if (validationToken is not null) return Results.Text(validationToken, "text/plain");
            try
            {
                await notifications.ReceiveAsync(context.Request.Body, context.RequestAborted);
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest();
            }
            return Results.Accepted();
        }).AllowAnonymous();
    }

    /// <summary>Where Graph posts change notifications, or null when the server isn't reachable from the internet.</summary>
    public static string? NotificationUrl(IOptions<ServerOptions> options) =>
        options.Value.PublicOrigin is { } origin && origin.StartsWith("https://", StringComparison.Ordinal) && !origin.Contains("localhost", StringComparison.Ordinal)
            ? $"{origin.TrimEnd('/')}/storage/notifications/{OneDriveProvider.Key}"
            : null;
}
