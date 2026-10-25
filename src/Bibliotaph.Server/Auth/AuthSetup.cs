using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Bibliotaph.Server.Auth;

/// <summary>
/// Sign-in as a backend for the frontend: the server runs the OpenID Connect flow and gives the browser one HTTP-only
/// session cookie. No token ever reaches the page, and every call is same-origin, so no Authorization header crosses
/// origins.
/// </summary>
public static class AuthSetup
{
    public const string SessionScheme = "session";

    /// <summary>Holds a provider's answer for the moment between its callback and /auth/complete.</summary>
    public const string ExternalScheme = "external";

    public const string AdminPolicy = "admin";

    public const string UserIdClaim = "bib_uid";
    public const string AdminClaim = "bib_admin";

    public static IServiceCollection AddBibliotaphAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = services.AddAuthentication(SessionScheme)
            .AddCookie(SessionScheme, options =>
            {
                options.Cookie.Name = "__Host-bibliotaph";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                // The web client shows its own sign-in page; an API call without a session just gets a status code.
                options.Events.OnRedirectToLogin = context => Status(context, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = context => Status(context, StatusCodes.Status403Forbidden);
            })
            .AddCookie(ExternalScheme, options =>
            {
                options.Cookie.Name = "__Host-bibliotaph-external";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            });

        if (Auth0(configuration) is { } client) auth.AddOpenIdConnect(Auth0SignIn.Id, "Auth0", options => Auth0SignIn.Configure(options, client));

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(AdminClaim, "true"));
        return services;
    }

    /// <summary>Auth0's settings (Auth:auth0:Domain, ClientId and ClientSecret), or null when sign-in isn't set up.</summary>
    public static Auth0Client? Auth0(IConfiguration configuration) =>
        configuration.GetSection($"Auth:{Auth0SignIn.Id}").Get<Auth0Client>() is { IsSet: true } client ? client : null;

    static Task Status(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    public static string? UserId(this ClaimsPrincipal principal) =>
        principal.FindFirst(UserIdClaim)?.Value is { } id && UserIds.IsValid(id) ? id : null;

    public static ClaimsPrincipal SessionPrincipal(User user) =>
        new(new ClaimsIdentity(
            [
                new Claim(UserIdClaim, user.Id),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(AdminClaim, user.IsAdmin ? "true" : "false"),
            ], SessionScheme, ClaimTypes.Name, ClaimTypes.Role));

    /// <summary>
    /// Refuses a state-changing call that lacks the <see cref="ApiHeaders.Client"/> header. The session cookie is
    /// SameSite=Lax, which already stops most cross-site requests; the header covers the rest, since another site can't
    /// send it without a CORS preflight, and the server allows no cross-origin calls.
    /// </summary>
    public static IApplicationBuilder UseCrossSiteGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            var guarded = request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/auth/signout");
            if (guarded && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)
                && !request.Headers.ContainsKey(ApiHeaders.Client))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await next(context);
        });
}
