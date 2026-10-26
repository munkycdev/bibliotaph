using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>What a shared result is.</summary>
public static class SharedResultKinds
{
    /// <summary>The AI's reading of a book as a whole: the values it suggested, with their evidence.</summary>
    public const string Classification = "classification";

    /// <summary>A finished whole-book scan: the chapters and what the AI found in each.</summary>
    public const string Chapters = "chapters";
}

/// <summary>
/// The web library as the place results are shared: a connected PC's way to it, or the web library's own store of
/// what its devices sent, for the web library itself.
/// </summary>
public interface IResultChannel
{
    /// <summary>Whether the channel is in use now, as when the PC is connected to the web library.</summary>
    bool IsOn { get; }

    /// <summary>The results after <paramref name="after"/> that other devices made, or null when they can't be had now.</summary>
    Task<SharedResultPage?> ReadResultsAsync(long after, CancellationToken ct);

    /// <summary>Sends this device's results. Returns false when they couldn't be sent now.</summary>
    Task<bool> WriteResultsAsync(IReadOnlyList<SharedResult> results, CancellationToken ct);
}

/// <summary>A value the AI suggested, as shared: a term by its key here and its label, which another device resolves to its own key.</summary>
public sealed record SharedClaim(string Field, string Value, string? Term, string? Quote, IReadOnlyList<int> Pages, bool FromSampling);

/// <summary>The AI's reading of a book as a whole, as shared: the model, what it read, and every value it suggested.</summary>
public sealed record SharedClassification(string Model, int SchemaVersion, IReadOnlyList<int> Pages, DateTime StartedUtc, IReadOnlyList<SharedClaim> Claims);

/// <summary>A finished whole-book scan, as shared.</summary>
public sealed record SharedChapters(string Model, DateTime StartedUtc, IReadOnlyList<ScannedChapter> Chapters);

/// <summary>
/// The user's devices sharing what the AI worked out about their files through the web library, so a book is read by
/// the AI once, wherever that is cheapest (on a PC with a graphics card, say), and every device with the same file has
/// the result. Results go by the file's content, its SHA-256, so the same book in OneDrive and on a PC's disk is one
/// book. Each device sends its own results (its classification runs and finished chapter scans) and takes the
/// others': a taken classification becomes a run here (<see cref="ClassificationStore.Shared"/>) with the same
/// suggestions, and a taken scan this file's chapters. The newest result for a file wins; one made with a newer prompt
/// always does. A result is never sent back by a device that took it, and a file waits in index.db for its results
/// until it is in this library. The user's decisions about the suggestions go by library sync, as ever.
/// </summary>
public sealed class SharedResults : IHostedService, IDisposable
{
    /// <summary>How often results are sent and taken.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>How long after the start the first pass runs: after the start's own work.</summary>
    public static readonly TimeSpan First = TimeSpan.FromSeconds(20);

    /// <summary>How long after a result is made or arrives it is passed on.</summary>
    public static readonly TimeSpan Soon = TimeSpan.FromSeconds(3);

    /// <summary>How many results go in one request.</summary>
    const int Batch = 100;

    const string PulledKey = "pulled";
    const string PushedRunsKey = "pushed.classification";
    const string PushedScansKey = "pushed.chapters";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    readonly ClassificationStore _runs;
    readonly ChapterScanStore _scans;
    readonly MetadataStore _metadata;
    readonly VocabularyStore _vocabularies;
    readonly EntryStore _entries;
    readonly LibraryStore _library;
    readonly MetadataProjector _projector;
    readonly ChapterProjection _chapters;
    readonly ChapterQueries _chapterQueries;
    readonly SharedResultCache _cache;
    readonly AiSettings _ai;
    readonly IResultChannel? _channel;
    readonly TimeProvider _clock;
    readonly ILogger _log;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Lock _lock = new();

    /// <summary>The documents looked at since the start: only new ones, and those whose results changed, are looked at again.</summary>
    readonly HashSet<long> _seen = [];

    readonly CancellationTokenSource _stopping = new();
    ITimer? _timer;

    public SharedResults(ClassificationStore runs, ChapterScanStore scans, MetadataStore metadata, VocabularyStore vocabularies, EntryStore entries,
        LibraryStore library, MetadataProjector projector, ChapterProjection chapters, ChapterQueries chapterQueries, SharedResultCache cache, AiSettings ai,
        IResultChannel? channel = null, TimeProvider? clock = null, ILogger<SharedResults>? log = null)
    {
        _runs = runs;
        _scans = scans;
        _metadata = metadata;
        _vocabularies = vocabularies;
        _entries = entries;
        _library = library;
        _projector = projector;
        _chapters = chapters;
        _chapterQueries = chapterQueries;
        _cache = cache;
        _ai = ai;
        _channel = channel;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<SharedResults>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock) _timer = _clock.CreateTimer(_ => Fire(), null, First, Every);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        await _stopping.CancelAsync();
        // A pass under way ends at its next step.
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
    }

    public void Dispose()
    {
        lock (_lock) _timer?.Dispose();
        _stopping.Dispose();
        _gate.Dispose();
    }

    /// <summary>A result was made here, or arrived: passed on shortly rather than at the next minute.</summary>
    public void Nudge()
    {
        lock (_lock) _timer?.Change(Soon, Every);
    }

    void Fire() => _ = Task.Run(async () =>
    {
        try
        {
            await SyncNowAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            // Tried again at the next pass.
            _log.LogWarning(ex, "Sharing results with the web library failed");
        }
    });

    /// <summary>
    /// Sends what this device worked out since the last pass, takes what the others did, and gives this library's
    /// files the results they lack.
    /// </summary>
    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var taken = new HashSet<string>(StringComparer.Ordinal);
            if (_channel is { IsOn: true } channel)
            {
                await PushRunsAsync(channel, ct);
                await PushScansAsync(channel, ct);
                await PullAsync(channel, taken, ct);
            }
            await ApplyAsync(taken, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// For the Classify stage, before it sends a book to the model: gives it the result another device made with this
    /// prompt, when there is one. Returns whether the book has it now.
    /// </summary>
    public async Task<bool> TryApplyAsync(long documentId, string contentHash, CancellationToken ct = default)
    {
        if ((await _cache.GetAsync(SharedResultKinds.Classification, [contentHash], ct)).GetValueOrDefault(contentHash) is not { } cached
            || cached.PromptVersion < ClassifierPrompt.Version) return false;
        var found = await _runs.GetLatestAsync([contentHash], ct: ct);
        var latest = found.Count > 0 ? found[0] : null;
        if (!IsNewer(cached, latest)) return latest?.Id == cached.ResultId;
        return await ApplyClassificationAsync(documentId, cached, ct);
    }

    async Task PushRunsAsync(IResultChannel channel, CancellationToken ct)
    {
        var since = await TimeAsync(PushedRunsKey, ct);
        var runs = await _runs.GetLatestAsync(ownSince: since ?? DateTime.MinValue, ct: ct);
        if (runs.Count == 0) return;
        var labels = await _vocabularies.GetLabelsAsync(ct);
        foreach (var batch in runs.Chunk(Batch))
        {
            var claims = (await _metadata.GetRunClaimsAsync([.. batch.Select(r => r.Id)], ct)).ToLookup(c => c.RunId, StringComparer.Ordinal);
            List<SharedResult> results = [.. batch.Select(run => new SharedResult(run.ContentHash, SharedResultKinds.Classification, run.PromptVersion, run.Id,
                run.FinishedUtc, JsonSerializer.Serialize(new SharedClassification(run.Model, run.SchemaVersion, run.Pages, run.StartedUtc,
                    [.. claims[run.Id].Select(c => new SharedClaim(c.Field, c.Value, TermLabel(c, labels), c.Quote, c.Pages, c.FromSampling))]), Json)))];
            if (!await channel.WriteResultsAsync(results, ct)) return;
            await _cache.SetStateAsync(PushedRunsKey, Stamp(batch[^1].FinishedUtc), ct);
        }
        _log.LogInformation("Shared {Count} AI readings with the web library", runs.Count);
    }

    static string? TermLabel(RunClaim claim, IReadOnlyDictionary<(string Vocabulary, string Key), string> labels) =>
        MetadataFields.Find(claim.Field) is { Kind: FieldKind.Term, Vocabulary: { } vocabulary } ? labels.GetValueOrDefault((vocabulary, claim.Value)) : null;

    async Task PushScansAsync(IResultChannel channel, CancellationToken ct)
    {
        var scans = await _scans.GetFinishedAsync(await TimeAsync(PushedScansKey, ct), ct);
        if (scans.Count == 0) return;
        foreach (var batch in scans.Chunk(Batch))
        {
            List<SharedResult> results = [.. batch.Select(scan => new SharedResult(scan.ContentHash, SharedResultKinds.Chapters, scan.PromptVersion,
                $"{scan.ContentHash}@{Stamp(scan.FinishedUtc!.Value)}", scan.FinishedUtc!.Value,
                JsonSerializer.Serialize(new SharedChapters(scan.Model, scan.StartedUtc, scan.Chapters), Json)))];
            if (!await channel.WriteResultsAsync(results, ct)) return;
            await _cache.SetStateAsync(PushedScansKey, Stamp(batch[^1].FinishedUtc!.Value), ct);
        }
        _log.LogInformation("Shared {Count} whole-book scans with the web library", scans.Count);
    }

    /// <summary>Takes the other devices' results since the last pass: scans straight into the catalog, readings into index.db to wait for their files.</summary>
    async Task PullAsync(IResultChannel channel, HashSet<string> taken, CancellationToken ct)
    {
        var after = long.TryParse(await _cache.GetStateAsync(PulledKey, ct), CultureInfo.InvariantCulture, out var last) ? last : 0;
        var count = 0;
        while (await channel.ReadResultsAsync(after, ct) is { } page)
        {
            var readings = new List<CachedResult>();
            foreach (var result in page.Results)
            {
                if (result.Kind == SharedResultKinds.Classification)
                    readings.Add(new CachedResult(result.ContentHash, result.Kind, result.PromptVersion, result.Id, result.FinishedUtc, result.Content));
                else if (result.Kind == SharedResultKinds.Chapters && Read<SharedChapters>(result.Content) is { } scan
                    && await _scans.SaveSharedAsync(new ChapterScanRecord(result.ContentHash, ClassificationStore.Shared, scan.Model, result.PromptVersion,
                        scan.Chapters, scan.StartedUtc, result.FinishedUtc), ct))
                    taken.Add(result.ContentHash);
            }
            await _cache.PutAsync(readings, ct);
            taken.UnionWith(readings.Select(r => r.ContentHash));
            count += page.Results.Count;
            after = page.Last;
            await _cache.SetStateAsync(PulledKey, after.ToString(CultureInfo.InvariantCulture), ct);
            if (!page.More) break;
        }
        if (count > 0) _log.LogInformation("Took {Count} results from the web library", count);
    }

    /// <summary>Gives the files new since the start, and those whose results were just taken, the results they lack.</summary>
    async Task ApplyAsync(HashSet<string> taken, CancellationToken ct)
    {
        var documents = (await _library.GetDocumentHashesAsync(ct)).Where(d => !_seen.Contains(d.DocumentId) || taken.Contains(d.ContentHash)).ToList();
        if (documents.Count == 0) return;
        var hashes = documents.Select(d => d.ContentHash).ToHashSet(StringComparer.Ordinal);

        var readings = await _cache.GetAsync(SharedResultKinds.Classification, hashes, ct);
        var latest = readings.Count == 0 ? new Dictionary<string, FinishedRun>()
            : (await _runs.GetLatestAsync([.. readings.Keys], ct: ct)).ToDictionary(r => r.ContentHash, StringComparer.Ordinal);
        var applied = 0;
        foreach (var (documentId, hash) in documents)
        {
            ct.ThrowIfCancellationRequested();
            if (readings.TryGetValue(hash, out var reading) && IsNewer(reading, latest.GetValueOrDefault(hash))
                && await ApplyClassificationAsync(documentId, reading, ct)) applied++;
        }

        // A scan just taken replaces the file's chapters; one taken earlier fills a new file's.
        var scans = await _scans.GetManyAsync(hashes, ct);
        var present = await _chapterQueries.GetScannedDocumentsAsync(ct);
        var projected = 0;
        foreach (var (documentId, hash) in documents)
            if (scans.TryGetValue(hash, out var scan) && scan.IsFinished && scan.Provider == ClassificationStore.Shared
                && (taken.Contains(hash) || !present.Contains(documentId)))
            {
                await _chapters.ProjectAsync(documentId, scan.Chapters, ct);
                projected++;
            }

        lock (_lock) _seen.UnionWith(documents.Select(d => d.DocumentId));
        if (applied + projected > 0)
            _log.LogInformation("Gave {Readings} books another device's AI reading and {Scans} their chapters", applied, projected);
    }

    /// <summary>
    /// Whether <paramref name="shared"/> should take the place of the newest reading here: one made with a newer
    /// prompt, or with the same prompt but later, unless it is the same reading.
    /// </summary>
    static bool IsNewer(CachedResult shared, FinishedRun? local) =>
        local is null
        || (local.Id != shared.ResultId
            && (shared.PromptVersion > local.PromptVersion || (shared.PromptVersion == local.PromptVersion && shared.FinishedUtc > local.FinishedUtc)));

    /// <summary>
    /// Records another device's reading of this file as a run here and stores its suggestions as the Classify stage
    /// would, but not for a file in a folder the user keeps from AI. Returns whether it was stored.
    /// </summary>
    async Task<bool> ApplyClassificationAsync(long documentId, CachedResult reading, CancellationToken ct)
    {
        if (_ai.Setup.SkippedRoots is { Count: > 0 } kept && (await _library.GetRootIdsAsync(documentId, ct)).Any(kept.Contains)) return false;
        if (await _entries.GetEntryAsync(documentId, ct) is not { } entry || Read<SharedClassification>(reading.Content) is not { } content) return false;
        var proposals = await ProposalsAsync(content.Claims, ct);
        var run = new RunRecord(entry.EntryId, reading.ContentHash, ClassificationStore.Shared, content.Model, reading.PromptVersion, content.SchemaVersion,
            content.Pages, content.StartedUtc, ClassificationStore.Complete);
        if (!await _runs.RecordSharedAsync(run, reading.ResultId, reading.FinishedUtc, ct)) return false;
        await _metadata.ApplyRunAsync(entry.EntryId, reading.ContentHash, reading.ResultId, AssertionOrigin.Ai, proposals, ct);
        await _projector.ProjectAsync([entry.EntryId], ct);
        return true;
    }

    /// <summary>
    /// The suggestions, as this device names them: a term this device doesn't have is proposed by its label, as a new
    /// term the AI suggested here would be, and one the user rejected here is left out.
    /// </summary>
    async Task<List<MetadataProposal>> ProposalsAsync(IReadOnlyList<SharedClaim> claims, CancellationToken ct)
    {
        var proposals = new List<MetadataProposal>();
        foreach (var claim in claims)
        {
            if (MetadataFields.Find(claim.Field) is not { } field) continue;
            var value = claim.Value;
            if (field is { Kind: FieldKind.Term, Vocabulary: { } vocabulary } && (await _vocabularies.GetAsync(ct)).Find(vocabulary, value) is null)
            {
                var (term, state) = await _vocabularies.ProposeTermAsync(vocabulary, claim.Term ?? value, ct);
                if (state == TermState.Rejected) continue;
                value = term.Key;
            }
            proposals.Add(new MetadataProposal(field, value, AssertionOrigin.Ai, claim.Quote, claim.Pages, claim.FromSampling));
        }
        return proposals;
    }

    /// <summary>A result this version can't read is left alone: a newer version on another device may have made it.</summary>
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

    async Task<DateTime?> TimeAsync(string key, CancellationToken ct) =>
        DateTime.TryParse(await _cache.GetStateAsync(key, ct), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var when) ? when : null;

    static string Stamp(DateTime when) => DateTime.SpecifyKind(when, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
