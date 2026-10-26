using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Accounts;
using Bibliotaph.Server.Auth;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/devices: connecting a Windows PC to the library. The PC asks without signing in, and the user approves it on
/// the web while signed in; after that the PC calls with its own key. The user's session manages the list.
/// </summary>
public static class DevicesApi
{
    public static void MapDevicesApi(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices");

        // Anyone may ask, so each address may only ask a few times a minute; the request does nothing until approved.
        devices.MapPost("/connect", (ConnectDevice request, HttpContext context, DeviceService service, IOptions<ServerOptions> options) =>
            service.Start(request.Name) is var (code, poll, expires)
                ? Results.Ok(new DeviceConnectStarted(code, poll,
                    $"{AuthEndpoints.PublicOrigin(options.Value, context.Request)}/connect?code={Uri.EscapeDataString(code)}", expires))
                : Results.Problem("Too many PCs are asking to connect just now. Try again in a few minutes.", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous().RequireRateLimiting(AuthSetup.ConnectLimit);

        devices.MapPost("/connect/poll", (DeviceConnectPoll request, DeviceService service) =>
        {
            var poll = service.Poll(request.PollToken);
            return new DeviceConnectState(State(poll.State), poll.Key, poll.DeviceId);
        }).AllowAnonymous();

        // The PC asks who it is, so Settings in the app can say whose library it's connected to, and learn when it's been disconnected.
        devices.MapGet("/me", async (ClaimsPrincipal principal, DeviceService service, CancellationToken ct) =>
            await service.FindAsync(principal.UserId()!, principal.DeviceId()!, ct) is { } device
                ? Results.Ok(new DeviceIdentity(device.Id, device.Name, principal.Identity!.Name!))
                : Results.Unauthorized()).RequireAuthorization(AuthSetup.DevicePolicy);

        // Disconnecting in the app removes the PC here too, so it doesn't linger in the web's list.
        devices.MapDelete("/me", async (ClaimsPrincipal principal, DeviceService service, CancellationToken ct) =>
        {
            await service.RemoveAsync(principal.UserId()!, principal.DeviceId()!, ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthSetup.DevicePolicy);

        var signedIn = devices.MapGroup("").RequireAuthorization();

        signedIn.MapGet("/connect/{code}", (string code, DeviceService service) =>
            service.Find(code) is { } request ? Results.Ok(new DeviceConnectRequest(request.Code, request.Name, request.Expires)) : Results.NotFound());

        signedIn.MapPost("/connect/{code}", async (string code, ClaimsPrincipal principal, DeviceService service, CancellationToken ct) =>
            await service.ApproveAsync(code, principal.UserId()!, ct) ? Results.NoContent() : Gone());

        signedIn.MapDelete("/connect/{code}", (string code, DeviceService service) => service.Decline(code) ? Results.NoContent() : Gone());

        signedIn.MapGet("/", async (ClaimsPrincipal principal, DeviceService service, CancellationToken ct) =>
            (await service.ListAsync(principal.UserId()!, ct)).Select(d =>
                new ConnectedDevice(d.Id, d.Name, Utc(d.Created), d.LastUsed is { } used ? Utc(used) : null)));

        signedIn.MapDelete("/{id}", async (string id, ClaimsPrincipal principal, DeviceService service, CancellationToken ct) =>
            await service.RemoveAsync(principal.UserId()!, id, ct) ? Results.NoContent() : Results.NotFound());
    }

    static IResult Gone() =>
        Results.Problem("That request has expired or was already answered. Ask again from the PC.", statusCode: StatusCodes.Status404NotFound);

    static string State(ConnectState state) => state switch
    {
        ConnectState.Waiting => DeviceConnectState.Waiting,
        ConnectState.Connected => DeviceConnectState.Connected,
        ConnectState.Declined => DeviceConnectState.Declined,
        _ => DeviceConnectState.Expired,
    };

    static DateTimeOffset Utc(DateTime stored) => new(DateTime.SpecifyKind(stored, DateTimeKind.Utc));
}
