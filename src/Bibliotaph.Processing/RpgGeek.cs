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
/// so its records come first. Its XML API is searched by name only, and finds only names holding every word searched
/// for, so it is searched with the book's titles, a record title from another catalogue first, and then with the line
/// the book belongs to ("Gull Rock Saga"), since a file's name often leaves the line out or runs its words together; an
/// item is taken only when one of its versions lists an ISBN or product code the book prints, so a search as broad as
/// a line's can't name the wrong book. A book printing neither is searched for by its titles alone and takes only the
/// one item named as it is (<see cref="AskByTitleAsync"/>), which Look up keeps only when the book prints its whole name. A
/// book the user gives the item's page of is read by the item's id (<see cref="ReadAsync"/>). BoardGameGeek asks for about five
/// seconds between requests (<see cref="Pace"/>, unless a test gives another) and for answers to be kept, so they're
/// kept for a month: a line's search and the items it finds are asked for once for every book in it.
/// </summary>
public sealed class RpgGeekCatalogue(HttpClient http, string token, TimeProvider? clock = null, TimeSpan? pace = null) : IBookCatalogue
{
    public static readonly Uri Address = new("https://boardgamegeek.com/");

    /// <summary>The least time between two requests, as BoardGameGeek's documentation asks.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(5);

    /// <summary>How long an answer is kept before it's asked for again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    /// <summary>Titles searched for one book: another catalogue's record title or its card's, and what its file gives.</summary>
    const int MostSearches = 3;

    /// <summary>Lines searched for one book: the folders it's in, the lines the books beside it were found in, its cover's.</summary>
    const int MostLineSearches = 3;

    /// <summary>Items read in one request, the most the API takes.</summary>
    const int MostItems = 20;

    /// <summary>
    /// Requests for items one book may make, however many searches find some: a line's search finds dozens. With every
    /// search, a book asks at most nine times, 45 seconds at the pace, and much less once its line's answers are kept.
    /// </summary>
    const int MostReads = 3;

    const int MostKept = 20_000;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly RequestPace _pace = new(pace ?? Pace, clock);
    readonly ConcurrentDictionary<string, (DateTimeOffset Asked, string Xml)> _kept = new(StringComparer.Ordinal);

    /// <summary>The name its records are given, and their suggestions are quoted by.</summary>
    public const string Source = "RPGGeek";

    public string Name => Source;

    public bool SearchesByTitle => true;

    /// <summary>A book printing no ISBN or code is searched for by its titles, and taken only by its whole name.</summary>
    public bool FindsByTitleAlone => true;

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
        IReadOnlyList<string>? lines = null, CancellationToken ct = default)
    {
        if (isbns.Count == 0 && codes.Count == 0) return await AskByTitleAsync(titles, ct);
        var searches = BookRecords.Searches(titles, MostSearches).Concat(BookRecords.Searches(lines ?? [], MostLineSearches));
        var reads = 0;
        foreach (var search in BookRecords.Searches(searches, MostSearches + MostLineSearches))
        {
            var query = Uri.EscapeDataString(search).Replace("%20", "+", StringComparison.Ordinal);
            // Read in the same batches whichever book asks, so a line's items, once read, are kept for the next.
            foreach (var batch in Candidates(Parse(await GetAsync($"xmlapi2/search?query={query}&type=rpgitem", ct)), search).Chunk(MostItems))
            {
                if (reads++ == MostReads) return new CatalogueAnswer([], Name);
                if (Read(Parse(await GetAsync($"xmlapi2/thing?id={string.Join(',', batch)}&type=rpgitem&versions=1", ct)), isbns, codes) is { } record)
                    return new CatalogueAnswer([record], Name);
            }
        }
        return new CatalogueAnswer([], Name);
    }

    /// <summary>
    /// For a book printing no ISBN or product code, the one item a search names exactly as one of its titles, or else
    /// the one whose name after its line's ("Line: Game Master's Screen") is the title, as a record found by title alone
    /// (its <see cref="BookRecord.Isbn"/> empty): two items of the name, as lines' screens often are, and it's neither.
    /// Never by a line: a line names many books. Look up then uses it only when the book prints the whole name and its
    /// designers or publisher too.
    /// </summary>
    async Task<CatalogueAnswer> AskByTitleAsync(IReadOnlyList<string> titles, CancellationToken ct)
    {
        foreach (var search in BookRecords.Searches(titles, MostSearches))
        {
            var query = Uri.EscapeDataString(search).Replace("%20", "+", StringComparison.Ordinal);
            if (Named(Parse(await GetAsync($"xmlapi2/search?query={query}&type=rpgitem", ct)), search) is not { } id) continue;
            var item = Parse(await GetAsync($"xmlapi2/thing?id={id}&type=rpgitem&versions=1", ct)).Root?.Elements("item")
                .FirstOrDefault(i => (string?)i.Attribute("id") == id);
            if (item is not null && Record(item, null, "") is { } record) return new CatalogueAnswer([record], Name);
        }
        return new CatalogueAnswer([], Name);
    }

    /// <summary>
    /// The record of the item with this id, whatever the book prints, as a record found by its title alone is (its
    /// <see cref="BookRecord.Isbn"/> empty): for a book the user gives the item's page of. Null for another catalogue's
    /// record, or when RPGGeek has no such item.
    /// </summary>
    public async Task<BookRecord?> ReadAsync(string source, string key, CancellationToken ct = default)
    {
        if (source != Source || key.Length is 0 or > MostDigits || !key.All(char.IsAsciiDigit)) return null;
        // Asked for as a search's items are, so one already read for the book's line isn't asked for again.
        var item = Parse(await GetAsync($"xmlapi2/thing?id={key}&type=rpgitem&versions=1", ct)).Root?.Elements("item")
            .FirstOrDefault(i => (string?)i.Attribute("id") == key);
        return item is null ? null : Record(item, null, "");
    }

    /// <summary>The longest id read from an address: RPGGeek's have six digits or so.</summary>
    const int MostDigits = 12;

    /// <summary>What an empty box is answered with.</summary>
    public const string NoLink = "Paste the address of the book's page on RPGGeek.";

    /// <summary>What an address anywhere else is answered with.</summary>
    public const string NotRpgGeek = "That isn't an RPGGeek address. A book's page there looks like https://rpggeek.com/rpgitem/12345/its-name.";

    /// <summary>What the page of a game, a series, a publisher or a search is answered with.</summary>
    public const string NotABook = "That RPGGeek page isn't a book's. A book's page has /rpgitem/ in its address, like https://rpggeek.com/rpgitem/12345/its-name.";

    /// <summary>What an address RPGGeek has no item at is answered with.</summary>
    public const string NoSuchBook = "RPGGeek has no book at that address.";

    /// <summary>
    /// The id of the item whose page <paramref name="link"/> is ("https://rpggeek.com/rpgitem/12345/its-name", with or
    /// without the https, on boardgamegeek.com too, or the number alone), or why it names none: a game's, series' or
    /// publisher's page isn't a book's.
    /// </summary>
    public static (string? Item, string? Problem) ItemIn(string? link)
    {
        var text = link?.Trim() ?? "";
        if (text.Length == 0) return (null, NoLink);
        if (text.All(char.IsAsciiDigit)) return Id(text) is { } number ? (number, null) : (null, NotRpgGeek);
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")) return (null, NotRpgGeek);
        var host = address.Host.StartsWith("www.", StringComparison.Ordinal) ? address.Host[4..] : address.Host;
        if (host is not ("rpggeek.com" or "boardgamegeek.com" or "videogamegeek.com")) return (null, NotRpgGeek);
        return address.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [var kind, var digits, ..]
            && kind.Equals("rpgitem", StringComparison.OrdinalIgnoreCase) && Id(digits) is { } id
            ? (id, null) : (null, NotABook);
    }

    /// <summary>An item's id as RPGGeek writes it, without zeros in front, or null for what isn't one.</summary>
    static string? Id(string digits) =>
        digits.All(char.IsAsciiDigit) && digits.TrimStart('0') is { Length: > 0 and <= MostDigits } id ? id : null;

    /// <summary>An item's page, for the details to link to.</summary>
    public static string PageOf(string key) => $"https://rpggeek.com/rpgitem/{key}";

    /// <summary>The id of the one item named as <paramref name="title"/> is, by its whole name or the part after a colon.</summary>
    static string? Named(XDocument search, string title)
    {
        var wanted = BookRecords.Simplify(title);
        var items = search.Root?.Elements("item")
            .Select(item => (Id: (string?)item.Attribute("id"), Names: item.Elements("name").Select(n => (string?)n.Attribute("value") ?? "").ToList()))
            .Where(i => i.Id is { Length: > 0 } && i.Id.All(char.IsAsciiDigit))
            .ToList() ?? [];
        string? One(Func<string, bool> named) => items.Where(i => i.Names.Any(named)).Select(i => i.Id).Distinct().ToList() is [var only] ? only : null;
        return One(n => BookRecords.Simplify(n) == wanted)
            ?? One(n => n.Contains(':', StringComparison.Ordinal) && BookRecords.Simplify(n[(n.LastIndexOf(':') + 1)..]) == wanted);
    }

    /// <summary>
    /// The items a search found that may be the book, closest first: named exactly as the title, then starting with it,
    /// then holding it, then holding all its words in another order ("Bestiary: Creatures" for "Creatures Bestiary"),
    /// by any of their names; as many as <see cref="MostReads"/> requests read.
    /// </summary>
    internal static List<string> Candidates(XDocument search, string title)
    {
        var wanted = BookRecords.Simplify(title);
        var words = wanted.Split(' ');
        int Closeness(string name) =>
            name == wanted ? 4
            : name.StartsWith(wanted, StringComparison.Ordinal) ? 3
            : name.Contains(wanted, StringComparison.Ordinal) ? 2
            : words.All(name.Split(' ').Contains) ? 1
            : 0;
        return [.. search.Root?.Elements("item")
            .Select(item => (Id: (string?)item.Attribute("id"), Names: item.Elements("name").Select(n => BookRecords.Simplify((string?)n.Attribute("value") ?? "")).ToList()))
            .Where(i => i.Id is { Length: > 0 } && i.Id.All(char.IsAsciiDigit))
            .Select(i => (i.Id, Closeness: i.Names.Count == 0 ? 0 : i.Names.Max(Closeness)))
            .Where(i => i.Closeness > 0)
            .OrderByDescending(i => i.Closeness)
            .Select(i => i.Id!)
            .Distinct()
            .Take(MostItems * MostReads) ?? []];
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
                if (Record(item, version, found) is { } record) return record;
            }
        }
        return null;
    }

    /// <summary>
    /// An item's record, found by <paramref name="found"/> (an ISBN, a product code, or empty for its title), with the
    /// version's publisher and year where <paramref name="version"/> gives them, else the item's.
    /// </summary>
    static BookRecord? Record(XElement item, XElement? version, string found)
    {
        if (PrimaryName(item) is not { } title || (string?)item.Attribute("id") is not { Length: > 0 } id) return null;
        var publishers = version is null ? [] : Links(version, "rpgpublisher");
        return new BookRecord(Source, id, found, title, Links(item, "rpgdesigner"),
            publishers.Count > 0 ? publishers : Links(item, "rpgpublisher"), (version is null ? null : Year(version)) ?? Year(item),
            [.. Filed.SelectMany(f => Links(item, f.Link).Select(v => new CatalogueValue(f.Field.Key, v)))]);
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
