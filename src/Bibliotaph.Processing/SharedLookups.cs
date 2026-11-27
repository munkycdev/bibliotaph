using System.Globalization;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>A record a lookup got, as shared, with whether its title is printed in the book (or, for a book with no file, whether its publisher agreed).</summary>
public sealed record SharedLookupRecord(BookRecord Record, bool Printed);

/// <summary>
/// What a lookup asked and got, as shared: the catalogues asked (<see cref="AskedOf"/>, "Open Library or RPGGeek"), every
/// record it got in the order it got them, what it searched for (a file's <see cref="Search"/>), and for a book with no
/// file the title it was asked by (<see cref="Title"/>). The user's answers about the records aren't in it: they go by
/// library sync, and each device's own decide what the book uses.
/// </summary>
public sealed record SharedLookup(string AskedOf, IReadOnlyList<SharedLookupRecord> Records, LookupSearch? Search = null, string? Title = null)
{
    /// <summary>Whether <see cref="Printed"/> was said of <paramref name="record"/>.</summary>
    public bool Printed(BookRecord record) => Records.Any(r => r.Printed && r.Record.Source == record.Source && r.Record.Key == record.Key);

    /// <summary>
    /// Whether it asked every catalogue in <paramref name="catalogues"/> ("Open Library or Google Books or RPGGeek"),
    /// so asking them here would be asking the same again.
    /// </summary>
    public bool AskedEvery(string catalogues) => !BookLookups.AskedOfFewer(BookLookups.AskedOf(AskedOf), catalogues);

    /// <summary>
    /// Whether a file's lookup here would ask nothing it didn't: the same catalogues or fewer, no ISBN, product code or
    /// title it didn't search for, and no record the user gave that it didn't read. Lines and games aren't compared:
    /// they come from the books beside it, which another PC may keep in other folders.
    /// </summary>
    public bool Covers(string catalogues, IReadOnlyCollection<string> isbns, IReadOnlyCollection<string> codes, IReadOnlyCollection<string> titles,
        IEnumerable<(string Source, string Key)> kept)
    {
        if (Search is not { } search || !AskedEvery(catalogues)) return false;
        var searched = search.Titles.Select(BookRecords.Simplify).ToHashSet(StringComparer.Ordinal);
        return isbns.All(search.Isbns.Contains) && codes.All(search.Codes.Contains) && titles.All(t => searched.Contains(BookRecords.Simplify(t)))
            && kept.All(k => Records.Any(r => r.Record.Source == k.Source && r.Record.Key == k.Key));
    }
}

/// <summary>
/// Lookups shared through the web library as the AI's readings are (<see cref="SharedResults"/>), since looking a whole
/// library up takes hours at RPGGeek's pace: a device about to look a book up online first takes what another of the
/// user's devices found for the same file, when that lookup asked everything this one would
/// (<see cref="SharedLookup.Covers"/>). Each lookup a device makes online is sent once, so a taken one never goes back.
/// A book the user asks to be looked up again goes online afresh (<see cref="JobRecord.Afresh"/>), and what it finds
/// then goes to the other devices. Books are kept by their file's content hash, or, for a book with no file, by the name
/// its records have on every PC (<see cref="Catalog.LookupRecordStore.KeyOf"/>).
/// </summary>
public sealed class SharedLookups(SharedResultCache cache, IResultChannel? channel = null, TimeProvider? clock = null, ILogger<SharedLookups>? log = null,
    IndexQueries? queries = null, LookupRecordStore? records = null, SettingsStore? settings = null)
{
    /// <summary>Which version of Look up's lookups made before lookups were shared have been kept to send, once.</summary>
    const string BackfilledKey = "lookup.backfilled";

    /// <summary>The lookups made here waiting to be sent, each under this prefix and its key, with when it was made and what it got.</summary>
    const string WaitingPrefix = "lookup.waiting:";

    /// <summary>When this device last looked each book up itself, under this prefix and its key: an older one from elsewhere doesn't replace it.</summary>
    const string OwnPrefix = "lookup.own:";

    /// <summary>How many lookups go in one request.</summary>
    const int Batch = 100;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<SharedLookups>.Instance;

    /// <summary>The version of Look up a shared lookup must have been made by.</summary>
    static int Version => Pipeline.Version(Stage.LookUp);

    /// <summary>
    /// Keeps a lookup made here, to send at the next pass while the web library is connected. One made before it was
    /// connected isn't kept to send: connecting adds catalogues, so the book is asked again then.
    /// </summary>
    public async Task RecordAsync(string key, SharedLookup lookup, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await cache.SetStateAsync(OwnPrefix + key, Stamp(now), ct);
        if (channel is { IsOn: true }) await cache.SetStateAsync(WaitingPrefix + key, JsonSerializer.Serialize(new Waiting(now, lookup), Json), ct);
    }

    /// <summary>
    /// What another device's lookup got for <paramref name="key"/>, made by this version of Look up and after this
    /// device's own last lookup of it, or null.
    /// </summary>
    public async Task<SharedLookup?> FindAsync(string key, CancellationToken ct = default)
    {
        if ((await cache.GetAsync(SharedResultKinds.Lookup, [key], ct)).GetValueOrDefault(key) is not { } cached || cached.PromptVersion != Version) return null;
        if (Parse(await cache.GetStateAsync(OwnPrefix + key, ct)) is { } own && own >= cached.FinishedUtc) return null;
        return Read<SharedLookup>(cached.Content);
    }

    /// <summary>Sends the lookups made here since the last pass, and, the first time, those made before lookups were shared.</summary>
    public async Task PushAsync(CancellationToken ct = default)
    {
        if (channel is not { IsOn: true }) return;
        await BackfillAsync(ct);
        var waiting = await cache.GetStatesAsync(WaitingPrefix, ct);
        if (waiting.Count == 0) return;
        var sent = 0;
        foreach (var batch in waiting.Chunk(Batch))
        {
            ct.ThrowIfCancellationRequested();
            var results = new List<SharedResult>();
            foreach (var (stateKey, value) in batch)
            {
                if (Read<Waiting>(value) is not { } made)
                {
                    await cache.DeleteStateAsync(stateKey, ct);
                    continue;
                }
                var key = stateKey[WaitingPrefix.Length..];
                results.Add(new SharedResult(key, SharedResultKinds.Lookup, Version, $"{key}@{Stamp(made.FinishedUtc)}", made.FinishedUtc,
                    JsonSerializer.Serialize(made.Lookup, Json)));
            }
            if (results.Count > 0 && !await channel.WriteResultsAsync(results, ct)) return;
            foreach (var (stateKey, _) in batch) await cache.DeleteStateAsync(stateKey, ct);
            sent += results.Count;
        }
        if (sent > 0) _log.LogInformation("Shared {Count} lookups with the web library", sent);
    }

    /// <summary>
    /// Keeps the lookups this device made before lookups were shared, once for each version of Look up: each file's
    /// last, from what it searched for and the records it kept, and each book owned elsewhere's, from the title and
    /// catalogues it was asked with. One made since then is already kept.
    /// </summary>
    async Task BackfillAsync(CancellationToken ct)
    {
        if (queries is null || records is null) return;
        var version = Version.ToString(CultureInfo.InvariantCulture);
        if (await cache.GetStateAsync(BackfilledKey, ct) == version) return;
        var own = await cache.GetStatesAsync(OwnPrefix, ct);
        var made = new List<(string Key, SharedLookup Lookup)>();
        var searches = (await queries.GetLookupSearchesAsync(ct)).Where(s => !own.ContainsKey(OwnPrefix + s.ContentHash))
            .Select(s => (s.ContentHash, Search: LookupSearch.Read(s.Searched))).Where(s => s.Search is not null).ToList();
        var saved = await records.GetSavedAsync([.. searches.Select(s => s.ContentHash)], ct);
        foreach (var (hash, search) in searches)
            made.Add((hash, new SharedLookup(search!.AskedOf, [.. saved.GetValueOrDefault(hash, []).Select(r => new SharedLookupRecord(r.Record, r.Printed))],
                search with { Found = [], Waiting = [], Refused = [] })));
        if (settings is not null && Asked(await settings.GetAsync(ElsewhereLookups.AskedKey, ct)) is { Count: > 0 } asked)
        {
            var fileless = asked.Where(a => !own.ContainsKey(OwnPrefix + a.Key)).ToList();
            var theirs = await records.GetSavedAsync([.. fileless.Select(a => a.Key)], ct);
            foreach (var (key, stamp) in fileless)
                if (stamp.Split('|', 2) is [var catalogues, var title])
                    made.Add((key, new SharedLookup(catalogues, [.. theirs.GetValueOrDefault(key, []).Select(r => new SharedLookupRecord(r.Record, r.Printed))],
                        Title: title)));
        }
        foreach (var (key, lookup) in made) await RecordAsync(key, lookup, ct);
        await cache.SetStateAsync(BackfilledKey, version, ct);
        if (made.Count > 0) _log.LogInformation("Kept {Count} earlier lookups to share with the web library", made.Count);
    }

    static Dictionary<string, string>? Asked(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    sealed record Waiting(DateTime FinishedUtc, SharedLookup Lookup);

    /// <summary>A lookup this version can't read is left alone, as other results are.</summary>
    static T? Read<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Stamp(DateTime when) => DateTime.SpecifyKind(when, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    static DateTime? Parse(string? when) =>
        DateTime.TryParse(when, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
