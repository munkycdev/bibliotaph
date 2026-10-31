using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
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

/// <summary>
/// The calls every area of the server's API is made of. The session cookie goes with every call because the client is
/// served from the same origin.
/// </summary>
public sealed class ApiHttp(HttpClient http)
{
    /// <summary>A call whose answer is <typeparamref name="T"/>; a body that is <see cref="HttpContent"/> is sent as it is, anything else as JSON.</summary>
    public async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    public async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var response = await SendRawAsync(method, url, body, ct);
    }

    /// <summary>A GET whose answer is null when the server says <paramref name="absent"/>: gone, or nobody signed in.</summary>
    public async Task<T?> FindAsync<T>(string url, HttpStatusCode absent, CancellationToken ct) where T : class
    {
        using var response = await http.GetAsync(url, ct);
        if (response.StatusCode == absent) return null;
        await EnsureAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    /// <summary>A GET read as plain JSON, with <paramref name="empty"/> for an empty answer.</summary>
    public async Task<T> GetAsync<T>(string url, T empty, CancellationToken ct) => await http.GetFromJsonAsync<T>(url, ct) ?? empty;

    /// <summary>A POST whose answer is newline-delimited JSON, yielding each line as it arrives. Cancelling <paramref name="ct"/> ends the call.</summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(string url, object body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body, body.GetType()) };
        // Read as it comes, rather than once the whole answer is in.
        request.SetBrowserResponseStreamingEnabled(true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var lines = new StreamReader(stream);
        while (await lines.ReadLineAsync(ct) is { } line)
            if (line.Length > 0 && JsonSerializer.Deserialize<T>(line, JsonSerializerOptions.Web) is { } update) yield return update;
    }

    /// <summary>A file the user picked, sent as it is.</summary>
    public static StreamContent Content(Stream file, string contentType)
    {
        var content = new StreamContent(file);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType.Length > 0 ? contentType : "application/octet-stream");
        return content;
    }

    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = body switch { null => null, HttpContent content => content, _ => JsonContent.Create(body, body.GetType()) },
        };
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
}

/// <summary>A call the server refused, with a message that can be shown as it is.</summary>
public sealed class ApiProblemException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
