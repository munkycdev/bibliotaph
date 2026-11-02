using System.Security.Claims;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// Nornis calling the Bibliotaph API on a game master's behalf, with an Auth0 access token whose audience is the
/// Bibliotaph API. Nothing is linked by Auth0's subject across the two apps: the token is validated here and its subject
/// mapped through this server's own logins, as a sign-in is. It is never the default scheme, so a token reaches only
/// /api/nornis/v1, and only while the user keeps their Nornis connection.
/// </summary>
public static class NornisAuthentication
{
    /// <summary>Not "nornis", which names the storage provider's own OAuth scheme.</summary>
    public const string Scheme = "nornis-token";

    public const string BooksRead = "books:read";
    public const string TicketsWrite = "tickets:write";
    public const string WorldsWrite = "worlds:write";

    /// <summary>A scope the token was granted, one claim each.</summary>
    public const string ScopeClaim = "bib_nornis_scope";

    /// <summary>The user's Nornis storage connection, which the token is honoured through.</summary>
    public const string ConnectionClaim = "bib_nornis_connection";

    /// <summary>Calls per user, so one busy world can't crowd out the rest of the server.</summary>
    public const string Limit = "nornis";

    /// <summary>The policy an endpoint asks for: this scheme, and the scope named.</summary>
    public static string Policy(string scope) => $"nornis:{scope}";

    const string Refusal = "bib_nornis_refusal";

    public static void Add(AuthenticationBuilder authentication, AuthorizationBuilder authorization, Auth0Client auth0, NornisOptions nornis)
    {
        authentication.AddJwtBearer(Scheme, options =>
        {
            options.Authority = $"https://{auth0.Domain}/";
            options.Audience = nornis.Audience;
            // Keep Auth0's own claim names (sub, scope).
            options.MapInboundClaims = false;
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = ConnectAsync,
                OnChallenge = ChallengeAsync,
            };
        });
        foreach (var scope in new[] { BooksRead, TicketsWrite, WorldsWrite })
            authorization.AddPolicy(Policy(scope), policy => policy.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser()
                .RequireClaim(ConnectionClaim).RequireClaim(ScopeClaim, scope));
    }

    /// <summary>Who the token is for here: an open account that still has its Nornis connection, or no one.</summary>
    static async Task ConnectAsync(TokenValidatedContext context)
    {
        var services = context.HttpContext.RequestServices;
        var ct = context.HttpContext.RequestAborted;
        var subject = context.Principal?.FindFirst("sub")?.Value;
        var user = subject is { Length: > 0 }
            ? await services.GetRequiredService<AccountService>().FindBySubjectAsync(Auth0SignIn.Id, subject, Auth0SignIn.FormerSubjects(subject), ct)
            : null;
        var connections = services.GetRequiredService<StorageConnections>();
        var connection = user is null
            ? null
            : (await connections.ListAsync(user.Id, ct)).FirstOrDefault(c => c.Provider == NornisProvider.Key && !c.NeedsReconnect);
        if (user is null || connection is null)
        {
            context.HttpContext.Items[Refusal] = NornisProblems.NotConnected;
            context.Fail("No Bibliotaph account is connected to Nornis for this token.");
            return;
        }

        await connections.TouchAsync(connection.Id, ct);
        var scopes = context.Principal!.FindFirst("scope")?.Value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AuthSetup.UserIdClaim, user.Id),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(AuthSetup.AdminClaim, "false"),
                new Claim(ConnectionClaim, connection.Id),
                .. scopes.Select(scope => new Claim(ScopeClaim, scope)),
            ], Scheme, ClaimTypes.Name, ClaimTypes.Role));
    }

    /// <summary>A refusal Nornis can act on: not_connected tells it to offer "Reconnect Bibliotaph".</summary>
    static async Task ChallengeAsync(JwtBearerChallengeContext context)
    {
        if (context.HttpContext.Items[Refusal] is not string code) return;
        context.HandleResponse();
        await NornisProblems.Refuse(StatusCodes.Status401Unauthorized, code, "Connect Bibliotaph again from Nornis.").ExecuteAsync(context.HttpContext);
    }
}

/// <summary>Refusals with a stable code, so Nornis can act on them without reading the words.</summary>
public static class NornisProblems
{
    public const string NotConnected = "not_connected";
    public const string Opening = "opening";
    public const string KeptFromAi = "kept_from_ai";

    public static IResult Refuse(int status, string code, string detail) =>
        Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
