using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing;

/// <summary>
/// AdventureLookup (adventurelookup.com), a catalogue of published adventures, mostly D&amp;D and Pathfinder, kept by
/// its users: the levels they're for, their edition, setting and environments, their monsters and items, and what comes
/// with them. It knows no ISBNs, so it's searched by
/// title, a record title from another catalogue first, and asked about every book, whatever the others found; a record
/// it gives is the book's only once Look up finds its title printed in the book and its authors, publisher or page
/// count agree (<see cref="BookRecords.Agrees"/>). No key is needed and only titles are sent. It asks for no pace, so
/// this keeps to one request a second across every library, and keeps answers for a month.
/// </summary>
public sealed class AdventureLookupCatalogue(HttpClient http, TimeProvider? clock = null, TimeSpan? pace = null) : IBookCatalogue
{
    public static readonly Uri Address = new("https://adventurelookup.com/");

    /// <summary>The least time between two requests.</summary>
    public static readonly TimeSpan Pace = TimeSpan.FromSeconds(1);

    /// <summary>How long an answer is kept before it's asked for again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    /// <summary>Titles searched for one book.</summary>
    const int MostSearches = 2;

    const int MostKept = 20_000;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly RequestPace _pace = new(pace ?? Pace, clock);
    readonly ConcurrentDictionary<string, (DateTimeOffset Asked, string Json)> _kept = new(StringComparer.Ordinal);

    /// <summary>The name its records are given, and their suggestions are quoted by.</summary>
    public const string Source = "AdventureLookup";

    public string Name => Source;

    public bool SearchesByTitle => true;

    public bool FindsByTitleAlone => true;

    /// <summary>The usual client: who is asking, as for Open Library.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.BaseAddress = Address;
        client.Timeout = TimeSpan.FromSeconds(30);
        var version = typeof(AdventureLookupCatalogue).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Bibliotaph", version));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://bibliotaph.online)"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>Nothing to search for without a title.</summary>
    public Task<BookRecord?> FindAsync(IReadOnlyList<string> isbns, CancellationToken ct = default) => Task.FromResult<BookRecord?>(null);

    public async Task<CatalogueAnswer> AskAsync(IReadOnlyList<string> isbns, IReadOnlyList<string> titles, IReadOnlyList<string> codes,
        CancellationToken ct = default)
    {
        foreach (var title in titles.Where(t => BookRecords.Simplify(t).Length > 0).Take(MostSearches))
        {
            var json = await GetAsync($"api/adventures?q={Uri.EscapeDataString(title.Trim())}", ct);
            if (Read(Parse(json), title) is { } record) return new CatalogueAnswer([record], Name);
        }
        return new CatalogueAnswer([], Name);
    }

    /// <summary>
    /// The record of the adventure a search found that's named as <paramref name="title"/> is, ignoring case and
    /// punctuation, or else the one with the longest name the title holds whole ("Curse of the Crimson Throne" in
    /// "Curse of the Crimson Throne Player's Guide"), since a file's title often adds an edition or a version.
    /// </summary>
    internal static BookRecord? Read(JsonDocument search, string title)
    {
        if (search.RootElement.ValueKind != JsonValueKind.Object
            || !search.RootElement.TryGetProperty("adventures", out var adventures) || adventures.ValueKind != JsonValueKind.Array)
            return null;
        var wanted = BookRecords.Simplify(title);
        var named = adventures.EnumerateArray()
            .Select(a => (Adventure: a, Name: Text(a, "title")))
            .Where(a => a.Name is not null && Id(a.Adventure) is not null)
            .Select(a => (a.Adventure, a.Name, Simple: BookRecords.Simplify(a.Name!)))
            .Where(a => a.Simple.Length > 0)
            .ToList();
        var best = named.FirstOrDefault(a => a.Simple == wanted);
        if (best.Name is null)
            best = named.Where(a => $" {wanted} ".Contains($" {a.Simple} ", StringComparison.Ordinal))
                .OrderByDescending(a => a.Simple.Length).FirstOrDefault();
        return best.Name is null ? null : Record(best.Adventure, best.Name);
    }

    static BookRecord Record(JsonElement adventure, string title)
    {
        var values = new List<CatalogueValue>();
        void Add(MetadataField field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) values.Add(new CatalogueValue(field.Key, value.Trim()));
        }

        Add(MetadataFields.System, Edition(Text(adventure, "edition")));
        foreach (var environment in Texts(adventure, "environments"))
        {
            Add(MetadataFields.Environments, Environment(environment));
            if (environment.Equals("Feywild", StringComparison.OrdinalIgnoreCase)) Add(MetadataFields.Themes, "Fey");
        }
        if (Text(adventure, "setting") is { } setting && !NoSetting.Contains(setting)) Add(MetadataFields.Settings, setting);
        Add(MetadataFields.Levels, Levels(adventure));
        Add(MetadataFields.Series, Text(adventure, "part_of"));
        foreach (var monster in Texts(adventure, "boss_monsters").Concat(Texts(adventure, "common_monsters")).Distinct(StringComparer.OrdinalIgnoreCase))
            Add(MetadataFields.Monsters, monster);
        foreach (var item in Texts(adventure, "items")) Add(MetadataFields.Items, item);
        foreach (var (flag, includes) in Included)
            if (adventure.TryGetProperty(flag, out var value) && value.ValueKind == JsonValueKind.True) Add(MetadataFields.Includes, includes);
        var publisher = Text(adventure, "publisher") is { } named && !NoPublisher.Contains(named) ? named : null;
        return new BookRecord(Source, Id(adventure)!, "", title, Texts(adventure, "authors"), publisher is null ? [] : [publisher],
            Number(adventure, "publication_year")?.ToString(CultureInfo.InvariantCulture), values,
            Number(adventure, "num_pages") is > 0 and var pages ? pages : null);
    }

    /// <summary>
    /// Its edition in the vocabulary's words where AdventureLookup's own don't match a term as they are: a bare
    /// "Pathfinder" is the first edition, since the second is "Pathfinder 2e". "OSR" is a family of games, not one.
    /// </summary>
    static string? Edition(string? edition) => edition switch
    {
        null => null,
        "OSR" => null,
        "5th Edition" => "D&D 5e",
        "4th Edition" => "D&D 4e",
        "3.5 Edition" => "D&D 3.5e",
        "3rd Edition" => "D&D 3e",
        "Pathfinder" => "Pathfinder 1st edition",
        "Pathfinder 2e" => "Pathfinder 2nd edition",
        _ => edition,
    };

    /// <summary>
    /// Its environments as the vocabulary names them: AdventureLookup's list is fixed and coarser in places, so a
    /// dungeon, a cave and a sewer are all underground, and a town is urban. "Other" says nothing.
    /// </summary>
    static string? Environment(string environment) => environment switch
    {
        "Other" => null,
        "Dungeon" or "Cave" or "Underdark" or "Sewer" => "Underground",
        "City/Urban" or "Town/Village" => "Urban",
        "Stronghold (Castle/Keep/Tower)" => "Stronghold",
        "Temple/Shrine" => "Temple",
        "House/Mansion" => "Mansion",
        "Ruins/Tomb/Cemetary" or "Ruins/Tomb/Cemetery" => "Ruins",
        "Swamp/Moor" => "Swamp",
        "Desert/Pyramid" => "Desert",
        "Island" => "Coastal",
        "Ship" or "Underwater" => "At sea",
        "Air/Sky Ship" => "Sky",
        "Extraplanar" or "Feywild" => "Planar",
        _ => environment,
    };

    /// <summary>
    /// What its flags say comes with it, as the vocabulary names them. A flag that's false or missing says nothing,
    /// since most records leave them unset rather than saying no.
    /// </summary>
    static readonly (string Flag, string Includes)[] Included =
    [
        ("has_pregenerated_characters", "Pre-generated characters"), ("has_handouts", "Handouts"), ("has_tactical_maps", "Battle maps"),
        ("soloable", "Playable solo"),
    ];

    /// <summary>Settings that say the adventure has none in particular.</summary>
    static readonly HashSet<string> NoSetting = new(["Setting Neutral", "Other"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A store, and a word for anyone at all: neither is who published it.</summary>
    static readonly HashSet<string> NoPublisher = new(["DMs Guild", "DMsGuild", "Indie Dev.", "Indie Dev"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The levels it's for, "1-3" or "5", from its lowest and highest starting levels, or else from its named range:
    /// a tier or a 4th edition band, never "Low" or "High", which say too little.
    /// </summary>
    static string? Levels(JsonElement adventure)
    {
        var low = Number(adventure, "min_starting_level");
        var high = Number(adventure, "max_starting_level");
        if (low is > 0 && high is { } top && top >= low) return top == low ? $"{low}" : $"{low}-{top}";
        if (low is > 0 && high is null) return $"{low}";
        return Text(adventure, "starting_level_range") switch
        {
            "Tier 1" or "Tier 1 5E 1-4" => "1-4",
            "Tier 2" or "Tier 2 5E 5-10" => "5-10",
            "Tier 3" or "Tier 3 5E 11-16" => "11-16",
            "Tier 4" or "Tier 4 5E 17-20" => "17-20",
            "Heroic 4E 1-10" => "1-10",
            "Paragon 4E 11-20" => "11-20",
            "Epic 4E 21-30" => "21-30",
            "1-20 (any level)" => "1-20",
            _ => null,
        };
    }

    static string? Id(JsonElement adventure) =>
        adventure.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) ? n.ToString(CultureInfo.InvariantCulture) : null;

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text ? text : null;

    static List<string> Texts(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!.Trim()).Where(v => v.Length > 0).Distinct()]
            : [];

    static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;

    static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new CatalogueUnavailableException("AdventureLookup's answer couldn't be read.", ex);
        }
    }

    /// <summary>An answer, kept for a month, so a library looked up again doesn't ask again.</summary>
    async Task<string> GetAsync(string path, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_kept.TryGetValue(path, out var kept) && now - kept.Asked < KeepFor) return kept.Json;

        await _pace.WaitAsync(ct);
        string json;
        try
        {
            using var response = await http.GetAsync(new Uri(Address, path), ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                throw new CatalogueUnavailableException("AdventureLookup is busy. Books will be looked up when it answers again.");
            if (!response.IsSuccessStatusCode)
                throw new CatalogueUnavailableException($"AdventureLookup turned the request down ({(int)response.StatusCode}).");
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new CatalogueUnavailableException("AdventureLookup can't be reached. Books will be looked up when it can.", ex);
        }
        if (_kept.Count >= MostKept)
            foreach (var old in _kept.Where(k => now - k.Value.Asked >= KeepFor).Select(k => k.Key).ToList()) _kept.TryRemove(old, out _);
        if (_kept.Count < MostKept) _kept[path] = (now, json);
        return json;
    }
}
