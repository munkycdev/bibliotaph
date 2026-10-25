using Bibliotaph.Server.Accounts;
using Microsoft.AspNetCore.Authentication;
using ProviderInfo = Bibliotaph.Api.Contracts.SignInProvider;

namespace Bibliotaph.Server.Auth;

/// <summary>
/// /auth: the browser navigates here (these are pages, not API calls). Sign-in goes to the provider and back through
/// /auth/complete, which decides which account it is and sets the session cookie.
/// </summary>
public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app, IReadOnlyList<SignInProvider> providers, bool developerSignIn)
    {
        var auth = app.MapGroup("/auth");

        auth.MapGet("/providers", () =>
            providers.Select(p => new ProviderInfo(p.Id, p.Name)).Concat(developerSignIn ? [new ProviderInfo("developer", "a local developer account")] : []));

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
                return Results.LocalRedirect("/");
            });

        auth.MapGet("/signin/{provider}", (string provider, string? invite, string? returnUrl) =>
        {
            if (providers.All(p => p.Id != provider)) return Results.NotFound();
            var properties = new AuthenticationProperties { RedirectUri = "/auth/complete" };
            properties.Items["provider"] = provider;
            if (!string.IsNullOrEmpty(invite)) properties.Items["invite"] = invite;
            properties.Items["returnUrl"] = LocalOrHome(returnUrl);
            return Results.Challenge(properties, [provider]);
        });

        auth.MapGet("/complete", async (HttpContext context, AccountService accounts) =>
        {
            var external = await context.AuthenticateAsync(AuthSetup.ExternalScheme);
            await context.SignOutAsync(AuthSetup.ExternalScheme);
            var items = external.Properties?.Items;
            var provider = providers.FirstOrDefault(p => items is not null && items.TryGetValue("provider", out var id) && id == p.Id);
            if (!external.Succeeded || provider is null || provider.Identify(external.Principal!) is not { } identity)
                return Results.LocalRedirect("/signin?problem=failed");

            var current = await context.AuthenticateAsync(AuthSetup.SessionScheme);
            var outcome = await accounts.SignInAsync(identity, Item(items!, "invite"), current.Principal?.UserId(), context.RequestAborted);
            switch (outcome.Result)
            {
                case SignInResult.SignedIn or SignInResult.Created or SignInResult.Linked:
                    await context.SignInAsync(AuthSetup.SessionScheme, AuthSetup.SessionPrincipal(outcome.User!));
                    return Results.LocalRedirect(LocalOrHome(Item(items!, "returnUrl")));
                case SignInResult.NeedsInvite:
                    return Results.LocalRedirect("/invite-only");
                case SignInResult.InviteNotValid:
                    return Results.LocalRedirect("/invite-only?problem=invite");
                default:
                    return Results.LocalRedirect("/settings?problem=taken");
            }
        });

        auth.MapPost("/signout", async (HttpContext context) =>
        {
            await context.SignOutAsync(AuthSetup.SessionScheme);
            return Results.NoContent();
        });
    }

    static string? Item(IDictionary<string, string?> items, string key) => items.TryGetValue(key, out var value) ? value : null;

    /// <summary>Only a path on this site, so a crafted link can't send someone elsewhere after signing in.</summary>
    internal static string LocalOrHome(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url
            : "/";
}
