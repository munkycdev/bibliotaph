using System.Net;
using System.Net.Http.Json;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Adds the header the server requires on every call from Bibliotaph's own pages.</summary>
sealed class ClientHeaderHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add(ApiHeaders.Client, "1");
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>The server's API. The session cookie goes with every call because the client is served from the same origin.</summary>
public sealed class ApiClient(HttpClient http)
{
    /// <summary>The signed-in user, or null when nobody is.</summary>
    public async Task<CurrentUser?> MeAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/me", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CurrentUser>(ct);
    }

    public async Task<IReadOnlyList<SignInProvider>> ProvidersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<SignInProvider>>("auth/providers", ct) ?? [];

    public async Task<LibrarySummary> LibrarySummaryAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<LibrarySummary>("api/library/summary", ct) ?? new LibrarySummary(0, 0);

    public async Task<IReadOnlyList<InviteInfo>> InvitesAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<InviteInfo>>("api/invites", ct) ?? [];

    public async Task<CreatedInvite> CreateInviteAsync(CreateInvite request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/invites", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatedInvite>(ct))!;
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("auth/signout", null, ct);
        response.EnsureSuccessStatusCode();
    }
}
