using System.Globalization;
using System.Security.Claims;
using Bibliotaph.Server.Accounts;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Bibliotaph.Server.Auth;

/// <summary>The "Auth:auth0" configuration section: the Auth0 tenant and its Regular Web Application.</summary>
public sealed class Auth0Client
{
    /// <summary>The tenant's domain, such as bibliotaph.us.auth0.com, or a custom domain set up in Auth0.</summary>
    public string? Domain { get; set; }

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool IsSet => Domain is { Length: > 0 } && ClientId is { Length: > 0 } && ClientSecret is { Length: > 0 };
}

/// <summary>
/// Signing in through Auth0, the one identity provider: Google, Microsoft, Apple or an email address are connections
/// switched on in its dashboard, so adding one needs no change here. Sign-in asks only for openid, profile and email:
/// access to someone's storage is a separate connection, asked for when they add it.
/// </summary>
public static class Auth0SignIn
{
    /// <summary>The scheme name, the key in /auth/signin/{id} and the provider in the logins table.</summary>
    public const string Id = "auth0";

    public static void Configure(OpenIdConnectOptions options, Auth0Client client)
    {
        options.Authority = $"https://{client.Domain}/";
        options.ClientId = client.ClientId;
        options.ClientSecret = client.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.CallbackPath = $"/auth/callback/{Id}";
        options.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email" }) options.Scope.Add(scope);
        // Keep Auth0's own claim names (sub, email) rather than the WS-Federation URIs.
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SignInScheme = AuthSetup.ExternalScheme;
    }

    /// <summary>
    /// Reads who Auth0 says this is, or null when the token lacks what's needed. Auth0's sub names the connection and
    /// the person in it ("google-oauth2|1234"), and stays the same for that person and connection.
    /// </summary>
    public static ExternalIdentity? Identify(ClaimsPrincipal principal) =>
        Claim(principal, "sub") is { } sub
            ? new ExternalIdentity(Id, sub, Claim(principal, "name"), Claim(principal, "email_verified") == "true" ? Claim(principal, "email") : null)
            : null;

    /// <summary>Where signing out ends the Auth0 session too, so the next sign-in asks again rather than going straight back in.</summary>
    public static string LogoutUrl(Auth0Client client, string returnTo) =>
        $"https://{client.Domain}/v2/logout?client_id={Uri.EscapeDataString(client.ClientId!)}&returnTo={Uri.EscapeDataString(returnTo)}";

    /// <summary>How a sign-in is named in Settings: the connection it came through, which Auth0 puts before the | in sub.</summary>
    public static string Describe(string provider, string subject)
    {
        if (provider != Id) return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(provider);
        var parts = subject.Split('|');
        // A custom social connection, such as Discord's, puts its own name second: "oauth2|discord|1234".
        var connection = parts is ["oauth2", { Length: > 0 } named, _, ..] ? named : parts[0];
        return connection switch
        {
            "google-oauth2" => "Google",
            "windowslive" => "Microsoft",
            "waad" => "Microsoft work or school",
            "apple" => "Apple",
            "auth0" or "email" => "Email",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(connection),
        };
    }

    static string? Claim(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;
}
