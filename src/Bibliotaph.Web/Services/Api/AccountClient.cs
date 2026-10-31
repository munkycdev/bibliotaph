using System.Net;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>The signed-in user's account, sign-in, invitations, the accounts an admin sees and the PCs connected to the library.</summary>
public sealed class AccountClient(ApiHttp http)
{
    /// <summary>The signed-in user, or null when nobody is.</summary>
    public Task<CurrentUser?> MeAsync(CancellationToken ct = default) =>
        http.FindAsync<CurrentUser>("api/me", HttpStatusCode.Unauthorized, ct);

    /// <summary>Closes the signed-in account, which the server deletes with everything it keeps for it.</summary>
    public Task<SignedOut> CloseAccountAsync(CancellationToken ct = default) =>
        http.SendAsync<SignedOut>(HttpMethod.Delete, "api/me", null, ct);

    public async Task<IReadOnlyList<SignInProvider>> ProvidersAsync(CancellationToken ct = default) =>
        await http.GetAsync<List<SignInProvider>>("auth/providers", [], ct);

    public async Task<IReadOnlyList<InviteInfo>> InvitesAsync(CancellationToken ct = default) =>
        await http.GetAsync<List<InviteInfo>>("api/invites", [], ct);

    /// <summary>Every account, for an admin.</summary>
    public Task<List<AccountInfo>> AccountsAsync(CancellationToken ct = default) => http.SendAsync<List<AccountInfo>>(HttpMethod.Get, "api/accounts", null, ct);

    /// <summary>An admin deletes someone's account, as Close account would.</summary>
    public Task<ActionResult> DeleteAccountAsync(string id, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Delete, $"api/accounts/{Uri.EscapeDataString(id)}", null, ct);

    public Task<CreatedInvite> CreateInviteAsync(CreateInvite request, CancellationToken ct = default) =>
        http.SendAsync<CreatedInvite>(HttpMethod.Post, "api/invites", request, ct);

    /// <summary>An open invitation's link again; <see cref="ApiProblemException"/> once it's used or expired.</summary>
    public Task<CreatedInvite> InviteLinkAsync(string id, CancellationToken ct = default) =>
        http.SendAsync<CreatedInvite>(HttpMethod.Get, $"api/invites/{Uri.EscapeDataString(id)}/link", null, ct);

    public Task DeleteInviteAsync(string id, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Delete, $"api/invites/{Uri.EscapeDataString(id)}", null, ct);

    /// <summary>The Windows PCs connected to the library.</summary>
    public Task<List<ConnectedDevice>> DevicesAsync(CancellationToken ct = default) => http.SendAsync<List<ConnectedDevice>>(HttpMethod.Get, "api/devices", null, ct);

    public Task DisconnectDeviceAsync(string id, CancellationToken ct = default) => http.SendAsync(HttpMethod.Delete, $"api/devices/{Uri.EscapeDataString(id)}", null, ct);

    /// <summary>A PC's request to connect, or null when it has expired or was already answered.</summary>
    public Task<DeviceConnectRequest?> DeviceRequestAsync(string code, CancellationToken ct = default) =>
        http.FindAsync<DeviceConnectRequest>($"api/devices/connect/{Uri.EscapeDataString(code)}", HttpStatusCode.NotFound, ct);

    public Task ApproveDeviceAsync(string code, CancellationToken ct = default) => http.SendAsync(HttpMethod.Post, $"api/devices/connect/{Uri.EscapeDataString(code)}", null, ct);

    public Task DeclineDeviceAsync(string code, CancellationToken ct = default) => http.SendAsync(HttpMethod.Delete, $"api/devices/connect/{Uri.EscapeDataString(code)}", null, ct);

    public Task<SignedOut> SignOutAsync(CancellationToken ct = default) =>
        http.SendAsync<SignedOut>(HttpMethod.Post, "auth/signout", null, ct);
}
