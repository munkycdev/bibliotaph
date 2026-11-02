using System.Security.Claims;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// /api/nornis/v1: what Nornis calls on a game master's behalf, with the nornis scheme only, each endpoint behind its
/// scope and every call counted against the user's limit.
/// </summary>
public static class NornisApi
{
    public static void MapNornisApi(this IEndpointRouteBuilder app)
    {
        if (NornisOptions.From(app.ServiceProvider.GetRequiredService<IConfiguration>()) is null) return;
        var nornis = app.MapGroup("/api/nornis/v1").RequireRateLimiting(NornisAuthentication.Limit);

        // Sent when a member opens a world, so the idle close and a deploy's restore rarely land on a question.
        nornis.MapPost("/warm", (ClaimsPrincipal principal, UserLibraries libraries) =>
        {
            libraries.BeginOpening(principal.UserId()!);
            return Results.Accepted();
        }).RequireAuthorization(NornisAuthentication.Policy(NornisAuthentication.BooksRead));
    }
}
