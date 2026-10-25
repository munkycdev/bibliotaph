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

    static string Book(long entryId) => $"api/library/books/{entryId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>A value typed for a field, a list comma-separated: the details as they are now, or what's wrong with it.</summary>
    public Task<EditResult> SetFieldAsync(long entryId, string key, string text, CancellationToken ct = default) =>
        SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/fields/{Uri.EscapeDataString(key)}", new FieldEdit(text), ct);

    /// <summary>Keep this, Use this, Not right or Reset to suggestion on a field's evidence; <paramref name="text"/> is the value meant.</summary>
    public Task<EditResult> FieldActionAsync(long entryId, string key, string action, string text = "", CancellationToken ct = default) =>
        SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/fields/{Uri.EscapeDataString(key)}/{action}", new FieldEdit(text), ct);

    public async Task<string> ResolutionAsync(string key, string text, CancellationToken ct = default) =>
        (await SendAsync<Resolution>(HttpMethod.Get, $"api/library/fields/{Uri.EscapeDataString(key)}/resolution?text={Uri.EscapeDataString(text)}", null, ct)).Text;

    public Task EndCorrectionAsync(long entryId, bool decided, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"{Book(entryId)}/correction", new CorrectionEnd(decided), ct);

    public Task SetFavoriteAsync(long entryId, bool favorite, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"{Book(entryId)}/favorite", new SetFavorite(favorite), ct);

    public Task<BookNotes> NotesAsync(long entryId, CancellationToken ct = default) => SendAsync<BookNotes>(HttpMethod.Get, $"{Book(entryId)}/notes", null, ct);

    /// <summary>The book's own note, saved; the Notes tab as it is now.</summary>
    public Task<BookNotes> SetNoteAsync(long entryId, string text, CancellationToken ct = default) =>
        SendAsync<BookNotes>(HttpMethod.Put, $"{Book(entryId)}/notes", new FieldEdit(text), ct);

    public Task<BookDetails> ReprocessAsync(long entryId, CancellationToken ct = default) =>
        SendAsync<BookDetails>(HttpMethod.Post, $"{Book(entryId)}/reprocess", null, ct);

    public Task<EditResult> AddPartAsync(long entryId, PartEdit part, CancellationToken ct = default) =>
        SendAsync<EditResult>(HttpMethod.Post, $"{Book(entryId)}/parts", part, ct);

    public Task<ActionResult> SetPartPagesAsync(long partId, PartEdit pages, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Put, $"{Book(partId)}/pages", pages, ct);

    public Task<ActionResult> RemovePartAsync(long partId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Delete, $"{Book(partId)}/part", null, ct);

    /// <summary>A pack's cover: one of its files, or null for the mosaic.</summary>
    public Task<BookDetails> UsePackCoverAsync(long packId, long? member, CancellationToken ct = default) =>
        SendAsync<BookDetails>(HttpMethod.Put, $"{Book(packId)}/cover", new PackCover(member), ct);

    public Task<ActionResult> SplitPackAsync(long packId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Book(packId)}/split", null, ct);

    public Task<ActionResult> MakeCurrentAsync(long entryId, long documentId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/copies/{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/current", null, ct);

    public Task<ActionResult> NotSameBookAsync(long entryId, long documentId, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"{Book(entryId)}/copies/{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/not-same", null, ct);

    /// <summary>An Undo, or Change them, by the token the server gave for it.</summary>
    public Task<ActionResult> RunHeldAsync(string token, CancellationToken ct = default) =>
        SendAsync<ActionResult>(HttpMethod.Post, $"api/library/held/{Uri.EscapeDataString(token)}", null, ct);

    async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, ct);
    }

    async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body, body.GetType()) };
        var response = await http.SendAsync(request, ct);
        try
        {
            await EnsureAsync(response, ct);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
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
