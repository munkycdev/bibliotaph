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

    public async Task<IReadOnlyList<StorageAccount>> StorageAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<StorageAccount>>("api/storage", ct) ?? [];

    public async Task DisconnectStorageAsync(string id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/storage/{Uri.EscapeDataString(id)}", ct);
        await EnsureAsync(response, ct);
    }

    public async Task<StorageFolderList> StorageFoldersAsync(string id, string path, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/storage/{Uri.EscapeDataString(id)}/folders?path={Uri.EscapeDataString(path)}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<StorageFolderList>(ct))!;
    }

    public async Task<IReadOnlyList<LibraryFolder>> LibraryFoldersAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<LibraryFolder>>("api/library/folders", ct) ?? [];

    public async Task<IReadOnlyList<LibraryFolder>> AddLibraryFolderAsync(AddLibraryFolder request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync("api/library/folders", request, ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<LibraryFolder>>(ct))!;
    }

    public async Task<IReadOnlyList<LibraryFolder>> RemoveLibraryFolderAsync(long id, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/library/folders/{id}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<List<LibraryFolder>>(ct))!;
    }

    public async Task<LibraryStatus?> LibraryStatusAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync("api/library/status", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<LibraryStatus>(ct);
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("api/library/rescan", null, ct);
        await EnsureAsync(response, ct);
    }

    public async Task<LibraryResults> LibraryAsync(LibraryQuery query, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/library{LibraryUrl.QueryString(query, forApi: true)}", ct);
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<LibraryResults>(ct))!;
    }

    public async Task<HomeShelves> HomeAsync(CancellationToken ct = default) =>
        await http.GetFromJsonAsync<HomeShelves>("api/home", ct) ?? new HomeShelves([], [], "");

    /// <summary>A book's details, or null when it isn't in the library (any more).</summary>
    public async Task<BookDetails?> BookAsync(long entryId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/library/books/{entryId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<BookDetails>(ct);
    }

    /// <summary>A refused call becomes an <see cref="ApiProblemException"/> carrying the server's own words, when it gave any.</summary>
    static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? detail = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemText>(ct);
            detail = problem?.Detail ?? problem?.Title;
        }
        throw new ApiProblemException(response.StatusCode, detail ?? "Bibliotaph couldn't do that just now. Try again in a moment.");
    }

    sealed record ProblemText(string? Title, string? Detail);

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        using var response = await http.PostAsync("auth/signout", null, ct);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>A call the server refused, with a message that can be shown as it is.</summary>
public sealed class ApiProblemException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
