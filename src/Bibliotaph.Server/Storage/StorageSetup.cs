using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// Connecting storage: an OAuth grant of its own, apart from signing in, so a user can sign in with Google and read
/// books from OneDrive, or connect two drives. The grant's refresh token goes to accounts.db, encrypted; the browser
/// never sees a token.
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

        if (!Configured(configuration)) return services;
        var client = configuration.GetSection("Auth:microsoft").Get<ProviderClient>()!;
        services.AddAuthentication().AddOAuth(OneDriveProvider.Id, OneDriveProvider.Name, options =>
        {
            options.AuthorizationEndpoint = OneDriveProvider.AuthorizationEndpoint;
            options.TokenEndpoint = OneDriveProvider.TokenEndpoint;
            options.ClientId = client.ClientId!;
            options.ClientSecret = client.ClientSecret!;
            options.CallbackPath = $"/storage/callback/{OneDriveProvider.Id}";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.SignInScheme = AuthSetup.ExternalScheme;
            options.Scope.Clear();
            foreach (var scope in OneDriveProvider.Scopes) options.Scope.Add(scope);
            options.Events.OnCreatingTicket = SaveConnectionAsync;
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
                context.Response.Redirect("/settings?problem=storage");
                return Task.CompletedTask;
            };
        });
        return services;
    }

    /// <summary>OneDrive is connected through the Microsoft sign-in's app registration, so it needs that to be set up.</summary>
    public static bool Configured(IConfiguration configuration) =>
        configuration.GetSection("Auth:microsoft").Get<ProviderClient>() is { ClientId.Length: > 0, ClientSecret.Length: > 0 };

    static async Task SaveConnectionAsync(OAuthCreatingTicketContext context)
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

        var drive = await services.GetRequiredService<UserDriveFactory>().ForToken(context.AccessToken).MyDriveAsync(context.HttpContext.RequestAborted);
        var connection = await services.GetRequiredService<StorageConnections>().SaveAsync(userId, OneDriveProvider.Id, drive.Id,
            drive.OwnerName is { Length: > 0 } owner ? $"OneDrive · {owner}" : "OneDrive", drive.DriveType, context.RefreshToken,
            string.Join(' ', OneDriveProvider.Scopes), context.HttpContext.RequestAborted);
        services.GetRequiredService<DriveTokens>().Forget(connection.Id);
        await services.GetRequiredService<UserLibraries>().DrivesChangedAsync(userId);
        services.GetRequiredService<DriveNotifications>().Subscribe(connection.Id);
    }

    public static void MapStorage(this IEndpointRouteBuilder app)
    {
        var storage = app.MapGroup("/storage");

        storage.MapGet($"/connect/{OneDriveProvider.Id}", (HttpContext context, string? returnUrl) =>
            StorageSetup.Configured(context.RequestServices.GetRequiredService<IConfiguration>())
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = AuthEndpoints.LocalOrHome(returnUrl ?? "/settings") }, [OneDriveProvider.Id])
                : Results.LocalRedirect("/settings?problem=storage"))
            .RequireAuthorization();

        // Graph posts here: first to check the address (it echoes a token back), then whenever a drive changes. It
        // carries no cookie, so it's anonymous; each notification proves itself with its connection's secret.
        storage.MapPost($"/notifications/{OneDriveProvider.Id}", async (HttpContext context, string? validationToken, DriveNotifications notifications) =>
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
            ? $"{origin.TrimEnd('/')}/storage/notifications/{OneDriveProvider.Id}"
            : null;
}
