using System.Security.Claims;
using System.Text.RegularExpressions;
using Bibliotaph.Server.Accounts;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Bibliotaph.Server.Auth;

/// <summary>The "Auth:&lt;Provider&gt;" configuration section: an OpenID Connect app registration's client.</summary>
public sealed class ProviderClient
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>
/// The OpenID Connect providers people can sign in with. Sign-in asks only for openid, profile and email: access to
/// someone's storage is a separate connection, asked for when they add it.
/// </summary>
public abstract partial class SignInProvider
{
    public static readonly IReadOnlyList<SignInProvider> All = [new MicrosoftProvider(), new GoogleProvider()];

    /// <summary>The scheme name and the key in /auth/signin/{id} and in the logins table.</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    protected abstract string Authority { get; }

    public virtual void Configure(OpenIdConnectOptions options, ProviderClient client)
    {
        options.Authority = Authority;
        options.ClientId = client.ClientId;
        options.ClientSecret = client.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.CallbackPath = $"/auth/callback/{Id}";
        options.SignedOutCallbackPath = $"/auth/signedout/{Id}";
        options.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email" }) options.Scope.Add(scope);
        // Keep the providers' own claim names (sub, oid, tid, email) rather than the WS-Federation URIs.
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SignInScheme = AuthSetup.ExternalScheme;
    }

    /// <summary>Reads who the provider says this is, or null when the token lacks what's needed.</summary>
    public abstract ExternalIdentity? Identify(ClaimsPrincipal principal);

    protected static string? Claim(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;

    sealed class MicrosoftProvider : SignInProvider
    {
        public override string Id => "microsoft";
        public override string Name => "Microsoft";

        // Work, school and personal accounts alike.
        protected override string Authority => "https://login.microsoftonline.com/common/v2.0";

        public override void Configure(OpenIdConnectOptions options, ProviderClient client)
        {
            base.Configure(options, client);
            // The common endpoint's tokens are issued by each user's own tenant, so the issuer can't be one fixed value.
            // It must still be Microsoft's issuer for the tenant the token names.
            options.TokenValidationParameters.IssuerValidator = (issuer, token, _) =>
            {
                var tenant = token is Microsoft.IdentityModel.JsonWebTokens.JsonWebToken jwt && jwt.TryGetPayloadValue<string>("tid", out var tid) ? tid : null;
                if (tenant is not null && TenantIssuer().IsMatch(issuer) && issuer == $"https://login.microsoftonline.com/{tenant}/v2.0") return issuer;
                throw new SecurityTokenInvalidIssuerException($"Issuer {issuer} is not Microsoft's issuer for the token's tenant.");
            };
        }

        // Microsoft recommends oid with tid as the stable ID for a person in a multi-tenant app; sub differs per app.
        public override ExternalIdentity? Identify(ClaimsPrincipal principal) =>
            (Claim(principal, "oid"), Claim(principal, "tid")) is ({ } oid, { } tid)
                ? new ExternalIdentity(Id, $"{oid}@{tid}", Claim(principal, "name"), Claim(principal, "email") ?? Claim(principal, "preferred_username"))
                : null;
    }

    sealed class GoogleProvider : SignInProvider
    {
        public override string Id => "google";
        public override string Name => "Google";
        protected override string Authority => "https://accounts.google.com";

        public override ExternalIdentity? Identify(ClaimsPrincipal principal) =>
            Claim(principal, "sub") is { } sub
                ? new ExternalIdentity(Id, sub, Claim(principal, "name"),
                    Claim(principal, "email_verified") == "true" ? Claim(principal, "email") : null)
                : null;
    }

    [GeneratedRegex("^https://login\\.microsoftonline\\.com/[0-9a-f-]{36}/v2\\.0$")]
    private static partial Regex TenantIssuer();
}
