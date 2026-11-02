using System.Security.Claims;
using Bibliotaph.Api.Contracts;
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
        // Made now, so it's there to apply each library's inbox as the library opens.
        app.ServiceProvider.GetRequiredService<NornisWorlds>();

        // Sent when a member opens a world, so the idle close and a deploy's restore rarely land on a question.
        nornis.MapPost("/warm", (ClaimsPrincipal principal, UserLibraries libraries) =>
        {
            libraries.BeginOpening(principal.UserId()!);
            return Results.Accepted();
        }).RequireAuthorization(NornisAuthentication.Policy(NornisAuthentication.BooksRead));

        // Nornis's outbox retries anything but a 2xx, so a batch is answered only once it's all written, or not at all.
        nornis.MapPost("/worlds/events", async (NornisWorldEvent[]? events, ClaimsPrincipal principal, NornisWorlds worlds, CancellationToken ct) =>
        {
            if (events is not { Length: > 0 and <= NornisWorldEvent.MostInBatch } || events.Any(e => e is null || !e.IsComplete))
                return NornisProblems.Refuse(StatusCodes.Status400BadRequest, "malformed",
                    $"Send 1 to {NornisWorldEvent.MostInBatch} events, each with an eventId, a known kind, a worldId, a worldName of 1 to {NornisWorldEvent.LongestName} characters and occurredAt.");
            await worlds.ReceiveAsync(principal.UserId()!, events, ct);
            return Results.Accepted();
        }).RequireAuthorization(NornisAuthentication.Policy(NornisAuthentication.WorldsWrite));
    }
}
