using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// Google Books (books.googleapis.com), asked only by the web server, which holds its API key: it knows many more
/// small-press books than Open Library. Only the ISBNs are sent, one a request, since its search takes one ISBN at a
/// time. The key goes in a header, never the address, so it can't turn up in a logged URL.
/// </summary>
public sealed class GoogleBooksCatalogue(HttpClient http, string apiKey, TimeProvider? clock = null) : IBookCatalogue
{
    public static readonly Uri Address = new("https://www.googleapis.com/");

    /// <summary>The least time between two requests: well inside the API's default quota per minute.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);

    readonly RequestPace _pace = new(Pace, clock);

    public string Name => "Google Books";

    /// <summary>The usual client: who is asking, as for Open Library.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = Address;
        client.Timeout = TimeSpan.FromSeconds(30);
        var version = typeof(GoogleBooksCatalogue).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Bibliotaph", version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://bibliotaph.online)"));
        return client;
    }

    public async Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default)
    {
        foreach (var isbn in isbns)
            if (await FindOneAsync(isbn, ct) is { } record) return record;
        return null;
    }

    async Task<BookRecord?> FindOneAsync(string isbn, CancellationToken ct)
    {
        await _pace.WaitAsync(ct);
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Address,
                $"books/v1/volumes?q=isbn:{isbn}&maxResults=5&printType=books&fields=items(id,volumeInfo(title,authors,publisher,publishedDate,industryIdentifiers))"));
            request.Headers.Add("X-Goog-Api-Key", apiKey);
            using var response = await http.SendAsync(request, ct);
            json = await response.Content.ReadAsStringAsync(ct);
            // A day's quota used up comes back as 429, or as 403 with a limit as its reason; any other 403 is the key.
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                || (response.StatusCode == HttpStatusCode.Forbidden && json.Contains("LimitExceeded", StringComparison.OrdinalIgnoreCase)))
                throw new CatalogueUnavailableException("Google Books is busy. Books will be looked up when it answers again.");
            if (!response.IsSuccessStatusCode)
                throw new CatalogueUnavailableException($"Google Books turned the request down ({(int)response.StatusCode}). Its API key may need checking.");
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new CatalogueUnavailableException("Google Books can't be reached. Books will be looked up when it can.", ex);
        }
        return Read(json, isbn);
    }

    /// <summary>
    /// The record in an answer from the volumes search whose own ISBNs include <paramref name="isbn"/>: the search is
    /// for that ISBN, but a volume is taken only when it lists it, so a loose match never names a book.
    /// </summary>
    internal static BookRecord? Read(string json, string isbn)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CatalogueUnavailableException("Google Books' answer couldn't be read.", ex);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("volumeInfo", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                if (Text(info, "title") is not { } title || !Lists(info, isbn)) continue;
                var key = Text(item, "id") ?? isbn;
                var publishers = Text(info, "publisher") is { } publisher ? new[] { publisher } : [];
                return new BookRecord("Google Books", key, isbn, title, Strings(info, "authors"), publishers, Text(info, "publishedDate"));
            }
            return null;
        }
    }

    static bool Lists(JsonElement info, string isbn) =>
        info.TryGetProperty("industryIdentifiers", out var ids) && ids.ValueKind == JsonValueKind.Array
        && ids.EnumerateArray().Any(i => i.ValueKind == JsonValueKind.Object && Text(i, "identifier") is { } id && Isbn.Normalize(id) == isbn);

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    static List<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()?.Trim()).OfType<string>().Where(s => s.Length > 0).Distinct()]
            : [];
}
