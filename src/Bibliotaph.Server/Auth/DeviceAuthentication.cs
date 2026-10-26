using System.Security.Claims;
using System.Text.Encodings.Web;
using Bibliotaph.Server.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Auth;

/// <summary>
/// A connected Windows PC's key, sent as a bearer token. It isn't the default scheme: only endpoints that ask for
/// <see cref="AuthSetup.DevicePolicy"/> accept it, so a PC's key reaches what the PC needs and nothing else.
/// </summary>
public sealed class DeviceAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    DeviceService devices, AccountService accounts)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var key = header["Bearer ".Length..].Trim();
        if (await devices.AuthenticateAsync(key, Context.RequestAborted) is not { } device
            || await accounts.FindUserAsync(device.UserId, Context.RequestAborted) is not { } user)
            return AuthenticateResult.Fail("Not a connected PC's key.");
        var identity = new ClaimsIdentity(
            [
                new Claim(AuthSetup.UserIdClaim, user.Id),
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(AuthSetup.AdminClaim, "false"),
                new Claim(AuthSetup.DeviceClaim, device.Id),
            ], AuthSetup.DeviceScheme, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSetup.DeviceScheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
