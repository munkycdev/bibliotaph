using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Xml;
using System.Xml.Linq;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// RPGGeek, BoardGameGeek's catalogue of tabletop RPG products, asked only by the web server, which holds the token of
/// the app registered with BoardGameGeek. It knows RPGs, small-press ones included, better than any library catalogue,
/// so its records come first. Its XML API is searched by name only, so it is searched with the book's title, a record
/// title from another catalogue first; an item is taken only when one of its versions lists an ISBN or product code
/// the book prints, so a book is never named by a title alone. BoardGameGeek asks for about five seconds between requests (<see cref="Pace"/>,
/// unless a test gives another) and for answers to be kept, so they're kept for a month.
/// </summary>
public sealed class RpgGeekCatalogue(HttpClient http, string token, TimeProvider? clock = null, TimeSpan? pace = null) : IBookCatalogue
{
    public static readonly Uri Address = new("https://boardgamegeek.com/");

    /// <summary>The least time between two requests, as BoardGameGeek's documentation asks.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(5);

    /// <summary>How long an answer is kept before it's asked for again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    /// <summary>Titles searched for one book: two searches and two item reads is twenty seconds at the pace.</summary>
    const int MostSearches = 2;

    /// <summary>Items read in one request, the most the API takes.</summary>
    const int MostItems = 20;

    const int MostKept = 20_000;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly RequestPace _pace = new(pace ?? Pace, clock);
    readonly ConcurrentDictionary<string, (DateTimeOffset Asked, string Xml)> _kept = new(StringComparer.Ordinal);

    public string Name => "RPGGeek";

    public bool SearchesByTitle => true;

    /// <summary>The usual client: who is asking, as for Open Library.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = Address;
        client.Timeout = TimeSpan.FromSeconds(30);
        var version = typeof(RpgGeekCatalogue).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Bibliotaph", version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://bibliotaph.online)"));
        return client;
    }

    /// <summary>Nothing to search for without a title.</summary>
    public Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default) => Task.FromResult<BookRecord?>(null);

    public async Task<CatalogueAnswer> AskAsync(IReadOnlyList<string> isbns, IReadOnlyList<string> titles, IReadOnlyList<string> codes,
        CancellationToken ct = default)
    {
        foreach (var title in titles.Where(t => BookRecords.Simplify(t).Length > 0).Take(MostSearches))
        {
            var query = Uri.EscapeDataString(title.Trim()).Replace("%20", "+", StringComparison.Ordinal);
            var ids = Candidates(Parse(await GetAsync($"xmlapi2/search?query={query}&type=rpgitem", ct)), title);
            if (ids.Count == 0) continue;
            if (Read(Parse(await GetAsync($"xmlapi2/thing?id={string.Join(',', ids)}&type=rpgitem&versions=1", ct)), isbns, codes) is { } record)
                return new CatalogueAnswer([record], Name);
        }
        return new CatalogueAnswer([], Name);
    }

    /// <summary>
    /// The items a search found that may be the book, closest first: named exactly as the title, then starting with it,
    /// then holding it, by any of their names.
    /// </summary>
    internal static List<string> Candidates(XDocument search, string title)
    {
        var wanted = BookRecords.Simplify(title);
        return [.. search.Root?.Elements("item")
            .Select(item => (Id: (string?)item.Attribute("id"), Names: item.Elements("name").Select(n => BookRecords.Simplify((string?)n.Attribute("value") ?? "")).ToList()))
            .Where(i => i.Id is { Length: > 0 } && i.Id.All(char.IsAsciiDigit))
            .Select(i => (i.Id, Closeness: i.Names.Count == 0 ? 0 : i.Names.Max(n => n == wanted ? 3 : n.StartsWith(wanted, StringComparison.Ordinal) ? 2 : n.Contains(wanted, StringComparison.Ordinal) ? 1 : 0)))
            .Where(i => i.Closeness > 0)
            .OrderByDescending(i => i.Closeness)
            .Select(i => i.Id!)
            .Distinct()
            .Take(MostItems) ?? []];
    }

    /// <summary>What an item's links file it under, and who drew it and the mechanics it uses, by the field each fills; producers and the rest aren't used.</summary>
    static readonly (string Link, MetadataField Field)[] Filed =
    [
        ("rpg", MetadataFields.System), ("rpgcategory", MetadataFields.Types), ("rpggenre", MetadataFields.Themes),
        ("rpgsetting", MetadataFields.Settings), ("rpgseries", MetadataFields.Series), ("rpgartist", MetadataFields.Artists),
        ("rpgmechanic", MetadataFields.Mechanics),
    ];

    /// <summary>
    /// The record of the first item one of whose versions lists one of <paramref name="isbns"/>, or else one of the
    /// product codes the book prints (<paramref name="codes"/>, as <see cref="ProductCode"/> keeps them): its name,
    /// designers, the version's publisher (or the item's) and year (or the item's), the systems, categories, genres,
    /// settings and series it is filed under, and its artists and mechanics. An ISBN is the surer match, so every item
    /// is looked through for one before a code is taken.
    /// </summary>
    internal static BookRecord? Read(XDocument things, IReadOnlyList<string> isbns, IReadOnlyList<string>? codes = null)
    {
        string? ByIsbn(XElement version) => new[] { Value(version, "isbn13"), Value(version, "isbn10") }.OfType<string>()
            .Select(Isbn.Normalize).FirstOrDefault(i => i is not null && isbns.Contains(i));
        string? ByCode(XElement version) =>
            Value(version, "productcode") is { } code && ProductCode.Normalize(code) is { } normal && codes!.Contains(normal) ? normal : null;
        return Read(things, ByIsbn) ?? (codes is { Count: > 0 } ? Read(things, ByCode) : null);
    }

    /// <summary>The record of the first item with a version <paramref name="matched"/> gives the identifier it was found by.</summary>
    static BookRecord? Read(XDocument things, Func<XElement, string?> matched)
    {
        foreach (var item in things.Root?.Elements("item") ?? [])
        {
            foreach (var version in item.Element("versions")?.Elements("item") ?? [])
            {
                if (matched(version) is not { } found) continue;
                if (PrimaryName(item) is not { } title) continue;
                var publishers = Links(version, "rpgpublisher");
                return new BookRecord("RPGGeek", (string?)item.Attribute("id") ?? found, found, title, Links(item, "rpgdesigner"),
                    publishers.Count > 0 ? publishers : Links(item, "rpgpublisher"), Year(version) ?? Year(item),
                    [.. Filed.SelectMany(f => Links(item, f.Link).Select(v => new CatalogueValue(f.Field.Key, v)))]);
            }
        }
        return null;
    }

    static string? PrimaryName(XElement item) =>
        (item.Elements("name").FirstOrDefault(n => (string?)n.Attribute("type") == "primary") ?? item.Elements("name").FirstOrDefault())
            ?.Attribute("value")?.Value.Trim() is { Length: > 0 } name ? name : null;

    static string? Value(XElement element, string name) =>
        (string?)element.Element(name)?.Attribute("value") is { } value && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    /// <summary>A year RPGGeek knows: it writes 0 for none.</summary>
    static string? Year(XElement element) => Value(element, "yearpublished") is { } year && year != "0" ? year : null;

    /// <summary>The names an element links to of one type, without RPGGeek's "(Uncredited)".</summary>
    static List<string> Links(XElement element, string type) =>
        [.. element.Elements("link").Where(l => (string?)l.Attribute("type") == type).Select(l => ((string?)l.Attribute("value"))?.Trim())
            .OfType<string>().Where(v => v.Length > 0 && !v.StartsWith('(')).Distinct()];

    static XDocument Parse(string xml)
    {
        try
        {
            return XDocument.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new CatalogueUnavailableException("RPGGeek's answer couldn't be read.", ex);
        }
    }

    /// <summary>An answer, kept for a month: RPGGeek asks that answers be cached rather than asked for again.</summary>
    async Task<string> GetAsync(string path, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_kept.TryGetValue(path, out var kept) && now - kept.Asked < KeepFor) return kept.Xml;

        await _pace.WaitAsync(ct);
        string xml;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Address, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct);
            // It throttles with 429, 500 or 503, and queues some requests with 202.
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Accepted || (int)response.StatusCode >= 500)
                throw new CatalogueUnavailableException("RPGGeek is busy. Books will be looked up when it answers again.");
            if (!response.IsSuccessStatusCode)
                throw new CatalogueUnavailableException($"RPGGeek turned the request down ({(int)response.StatusCode}). Its token may need checking.");
            xml = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new CatalogueUnavailableException("RPGGeek can't be reached. Books will be looked up when it can.", ex);
        }
        if (_kept.Count >= MostKept)
            foreach (var old in _kept.Where(k => now - k.Value.Asked >= KeepFor).Select(k => k.Key).ToList()) _kept.TryRemove(old, out _);
        if (_kept.Count < MostKept) _kept[path] = (now, xml);
        return xml;
    }
}
