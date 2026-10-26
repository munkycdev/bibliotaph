using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Auth;

/// <summary>
/// /auth: the browser navigates here (these are pages, not API calls). Sign-in goes to Auth0 and back through
/// /auth/complete, which decides which account it is and sets the session cookie.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app, Auth0Client? auth0, bool developerSignIn)
    {
        var auth = app.MapGroup("/auth");

        auth.MapGet("/providers", () =>
            (auth0 is null ? Array.Empty<SignInProvider>() : [new SignInProvider(Auth0SignIn.Id, "Continue")])
                .Concat(developerSignIn ? [new SignInProvider("developer", "Continue as a local developer")] : []));

        // Development only, and only when Auth:DeveloperSignIn is true: signs in as one local admin with no provider at
        // all, so the client can be worked on before any app registration exists.
        if (developerSignIn)
            auth.MapGet("/signin/developer", async (HttpContext context, AccountService accounts) =>
            {
                var identity = new ExternalIdentity("developer", "local", "Developer", null);
                var outcome = await accounts.SignInAsync(identity, null, null, context.RequestAborted);
                if (outcome.User is null)
                {
                    var (code, _) = await accounts.CreateInviteAsync(null, "developer sign-in", admin: true, context.RequestAborted);
                    outcome = await accounts.SignInAsync(identity, code, null, context.RequestAborted);
                }
                await context.SignInAsync(AuthSetup.SessionScheme, AuthSetup.SessionPrincipal(outcome.User!));
                return Results.LocalRedirect("/home");
            });

        if (auth0 is not null)
            auth.MapGet("/signin/" + Auth0SignIn.Id, async (string? invite, string? returnUrl, HttpContext context) =>
            {
                var properties = new OpenIdConnectChallengeProperties { RedirectUri = "/auth/complete" };
                // Someone signed in is linking another sign-in; without this, Auth0 would hand back the one they're using.
                if ((await context.AuthenticateAsync(AuthSetup.SessionScheme)).Succeeded) properties.Prompt = "login";
                if (!string.IsNullOrEmpty(invite)) properties.Items["invite"] = invite;
                properties.Items["returnUrl"] = LocalOrHome(returnUrl);
                return Results.Challenge(properties, [Auth0SignIn.Id]);
            });

        auth.MapGet("/complete", async (HttpContext context, AccountService accounts) =>
        {
            var external = await context.AuthenticateAsync(AuthSetup.ExternalScheme);
            await context.SignOutAsync(AuthSetup.ExternalScheme);
            var items = external.Properties?.Items;
            if (!external.Succeeded || items is null || Auth0SignIn.Identify(external.Principal!) is not { } identity)
                return Results.LocalRedirect("/signin?problem=failed");

            var current = await context.AuthenticateAsync(AuthSetup.SessionScheme);
            var outcome = await accounts.SignInAsync(identity, Item(items, "invite"), current.Principal?.UserId(), context.RequestAborted);
            switch (outcome.Result)
            {
                case SignInResult.SignedIn or SignInResult.Created or SignInResult.Linked:
                    await context.SignInAsync(AuthSetup.SessionScheme, AuthSetup.SessionPrincipal(outcome.User!));
                    return Results.LocalRedirect(LocalOrHome(Item(items, "returnUrl")));
                case SignInResult.NeedsInvite:
                    return Results.LocalRedirect("/invite-only");
                case SignInResult.InviteNotValid:
                    return Results.LocalRedirect("/invite-only?problem=invite");
                default:
                    return Results.LocalRedirect("/settings?problem=taken");
            }
        });

        // The page goes on to Auth0's logout, which ends its session and comes back here.
        auth.MapPost("/signout", async (HttpContext context, IOptions<ServerOptions> options) =>
        {
            await context.SignOutAsync(AuthSetup.SessionScheme);
            return AfterSignOut(auth0, options.Value, context.Request);
        });
    }

    /// <summary>Where the page goes once the session has ended: Auth0's logout, which ends its session too, then home.</summary>
    internal static SignedOut AfterSignOut(Auth0Client? auth0, ServerOptions options, HttpRequest request) =>
        new(auth0 is null ? "/" : Auth0SignIn.LogoutUrl(auth0, PublicOrigin(options, request) + "/"));

    /// <summary>This site's address as people reach it, which behind the container's ingress isn't the request's own.</summary>
    internal static string PublicOrigin(ServerOptions options, HttpRequest? request) =>
        (options.PublicOrigin ?? (request is null ? "https://localhost" : $"{request.Scheme}://{request.Host}")).TrimEnd('/');

    static string? Item(IDictionary<string, string?> items, string key) => items.TryGetValue(key, out var value) ? value : null;

    /// <summary>Only a path on this site, so a crafted link can't send someone elsewhere after signing in.</summary>
    internal static string LocalOrHome(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/home";
}
