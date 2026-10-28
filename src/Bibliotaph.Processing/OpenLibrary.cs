using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>An online catalogue of books that can be asked about an ISBN.</summary>
public interface IBookCatalogue
{
    /// <summary>What the app calls it: "Open Library".</summary>
    string Name { get; }

    /// <summary>
    /// The record of the first of <paramref name="isbns"/> (13 digits each) the catalogue knows, or null when it knows
    /// none of them.
    /// </summary>
    /// <exception cref="CatalogueUnavailableException">The catalogue can't be asked right now.</exception>
    Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default);

    /// <summary>
    /// Finds out again where it asks, for a catalogue that asks somewhere else in turn, as a PC asks its web library:
    /// <see cref="Name"/> may change, raising <see cref="NameChanged"/>. Never throws for a catalogue out of reach.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>Raised when <see cref="Name"/> changes, from any thread.</summary>
    event EventHandler? NameChanged
    {
        add { }
        remove { }
    }
}

/// <summary>The catalogue can't be reached, or is too busy to answer: nothing to do with the book asked about.</summary>
public sealed class CatalogueUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps requests to one service at least <paramref name="pace"/> apart however many callers share it, as the web
/// server's libraries do: each request takes the next free slot and waits for it.
/// </summary>
public sealed class RequestPace(TimeSpan pace, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Lock _lock = new();
    DateTimeOffset _next = DateTimeOffset.MinValue;

    public async Task WaitAsync(CancellationToken ct)
    {
        DateTimeOffset slot;
        lock (_lock)
        {
            slot = _next > _clock.GetUtcNow() ? _next : _clock.GetUtcNow();
            _next = slot + pace;
        }
        var wait = slot - _clock.GetUtcNow();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, ct);
    }
}

/// <summary>
/// Several catalogues asked in turn, the first record found winning: Open Library first, as it needs no key, then any
/// the server has a key for. A book none of them knows waits to be asked again when one of them couldn't answer.
/// </summary>
public sealed class BookCatalogues(IReadOnlyList<IBookCatalogue> catalogues) : IBookCatalogue
{
    public string Name { get; } = string.Join(" or ", catalogues.Select(c => c.Name));

    public async Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default)
    {
        CatalogueUnavailableException? unavailable = null;
        foreach (var catalogue in catalogues)
        {
            try
            {
                if (await catalogue.FindAsync(isbns, ct) is { } record) return record;
            }
            catch (CatalogueUnavailableException ex)
            {
                unavailable ??= ex;
            }
        }
        return unavailable is null ? null : throw unavailable;
    }
}

/// <summary>
/// Open Library (openlibrary.org), the Internet Archive's free catalogue: no key, and its data is public domain. Only
/// the ISBNs are sent. Its rules ask for at most one request a second from an app without a contact in its user agent,
/// and three with one; this keeps to one a second however many libraries share it, as the web server's do, and asks
/// for all of a book's ISBNs at once.
/// </summary>
public sealed class OpenLibraryCatalogue(HttpClient http, TimeProvider? clock = null) : IBookCatalogue
{
    public static readonly Uri Address = new("https://openlibrary.org/");

    /// <summary>The least time between two requests.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);

    readonly RequestPace _pace = new(Pace, clock);

    public string Name => "Open Library";

    /// <summary>The usual client: who is asking, with where to find out more, as Open Library asks.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = Address;
        client.Timeout = TimeSpan.FromSeconds(30);
        var version = typeof(OpenLibraryCatalogue).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Bibliotaph", version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://bibliotaph.online)"));
        return client;
    }

    public async Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default)
    {
        if (isbns.Count == 0) return null;
        var keys = string.Join(',', isbns.Select(i => "ISBN:" + i));
        await _pace.WaitAsync(ct);

        string json;
        try
        {
            using var response = await http.GetAsync(new Uri(Address, $"api/books?bibkeys={Uri.EscapeDataString(keys)}&format=json&jscmd=data"), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                throw new CatalogueUnavailableException("Open Library is busy. Books will be looked up when it answers again.");
            if (!response.IsSuccessStatusCode)
                throw new CatalogueUnavailableException($"Open Library turned the request down ({(int)response.StatusCode}).");
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new CatalogueUnavailableException("Open Library can't be reached. Books will be looked up when it can.", ex);
        }
        return Read(json, isbns);
    }

    /// <summary>
    /// The record for the first of <paramref name="isbns"/> in an answer from the books API (<c>jscmd=data</c>): an
    /// object with a property for each ISBN it knows, as "ISBN:9781234567897".
    /// </summary>
    internal static BookRecord? Read(string json, IReadOnlyList<string> isbns)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CatalogueUnavailableException("Open Library's answer couldn't be read.", ex);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var isbn in isbns)
            {
                if (!document.RootElement.TryGetProperty("ISBN:" + isbn, out var book) || book.ValueKind != JsonValueKind.Object) continue;
                if (Text(book, "title") is not { } title) continue;
                var key = Text(book, "key") is { } path ? path[(path.LastIndexOf('/') + 1)..] : isbn;
                return new BookRecord("Open Library", key, isbn, title, Names(book, "authors"), Names(book, "publishers"), Text(book, "publish_date"));
            }
            return null;
        }
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

    static List<string> Names(JsonElement element, string name) =>
        element.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => Text(i, "name")).OfType<string>().Distinct()]
            : [];
}
