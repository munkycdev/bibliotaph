using System.Globalization;
using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A classifier run as the Classify stage records it, finished or failed.</summary>
public sealed record RunRecord(
    EntryId EntryId, string ContentHash, string Provider, string Model, int PromptVersion, int SchemaVersion,
    IReadOnlyList<int> Pages, DateTime StartedUtc, string Outcome);

/// <summary>
/// A finished run over a book as a whole, as the web library shares it with the user's other devices: its id, the copy
/// it read and when it ended.
/// </summary>
public sealed record FinishedRun(string Id, EntryId EntryId, string ContentHash, string Provider, string Model, int PromptVersion, int SchemaVersion,
    IReadOnlyList<int> Pages, DateTime StartedUtc, DateTime FinishedUtc);

/// <summary>How much of the library the current model and prompt have classified, and how much an earlier one did.</summary>
public sealed record ClassificationSummary(int Current, int Earlier);

/// <summary>
/// Classification runs in catalog.db: which model and prompt read which content, and with what outcome. The Classify
/// stage skips content the current model and prompt have already classified, so neither an index.db rebuild nor a
/// restart sends a book to the model again.
/// </summary>
public sealed class ClassificationStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>The outcome of a run whose suggestions were stored.</summary>
    public const string Complete = "complete";

    /// <summary>
    /// The provider of a run another of the user's devices made and this one took from the web library. Such a run
    /// counts as read for its prompt whatever this device's model, so the book isn't sent to a model again, and it is
    /// never shared back.
    /// </summary>
    public const string Shared = "shared";

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Whether <paramref name="model"/> and this prompt version have already classified this content as a whole. The
    /// runs that read an adventure inside it, on its own pages, don't count.
    /// </summary>
    public async Task<bool> HasRunAsync(string contentHash, string model, int promptVersion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClassificationRuns.AnyAsync(r => r.ContentHash == contentHash && r.Model == model && r.PromptVersion == promptVersion
            && r.Outcome == Complete && r.Entry.Kind != EntryKind.Part, ct);
    }

    /// <summary>Whether a run taken from another device has classified this content as a whole with this prompt version.</summary>
    public async Task<bool> HasSharedRunAsync(string contentHash, int promptVersion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClassificationRuns.AnyAsync(r => r.ContentHash == contentHash && r.Provider == Shared && r.PromptVersion == promptVersion
            && r.Outcome == Complete && r.Entry.Kind != EntryKind.Part, ct);
    }

    /// <summary>
    /// The newest finished run over each copy as a whole, of those with <paramref name="contentHashes"/>, or of every
    /// copy; with <paramref name="ownSince"/>, only this device's own runs that finished after it.
    /// </summary>
    public async Task<IReadOnlyList<FinishedRun>> GetLatestAsync(IReadOnlyCollection<string>? contentHashes = null, DateTime? ownSince = null,
        CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var complete = db.ClassificationRuns.AsNoTracking().Where(r => r.Outcome == Complete && r.FinishedUtc != null && r.Entry.Kind != EntryKind.Part);
        // Asked every minute, so the usual answer, nothing new, comes without reading every run.
        if (ownSince is { } after && !await complete.AnyAsync(r => r.Provider != Shared && r.FinishedUtc > after, ct)) return [];
        var rows = new List<ClassificationRun>();
        if (contentHashes is null)
            rows.AddRange(await complete.ToListAsync(ct));
        else
            foreach (var hashes in contentHashes.Distinct().Chunk(500))
                rows.AddRange(await complete.Where(r => hashes.Contains(r.ContentHash)).ToListAsync(ct));
        var latest = rows.GroupBy(r => r.ContentHash, StringComparer.Ordinal).Select(g => g.MaxBy(r => r.FinishedUtc)!);
        if (ownSince is { } since) latest = latest.Where(r => r.Provider != Shared && r.FinishedUtc > since);
        return [.. latest.OrderBy(r => r.FinishedUtc).Select(r => new FinishedRun(r.Id, new EntryId(r.EntryId), r.ContentHash, r.Provider, r.Model,
            r.PromptVersion, r.SchemaVersion, Pages(r.PagesJson), r.StartedUtc, r.FinishedUtc!.Value))];
    }

    static List<int> Pages(string? json)
    {
        try
        {
            return json is null ? [] : JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Whether <paramref name="model"/> and this prompt version have already classified the adventure
    /// <paramref name="partId"/> on its own pages, in any copy of its file.
    /// </summary>
    public async Task<bool> HasRunOnAsync(EntryId partId, string model, int promptVersion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClassificationRuns.AnyAsync(r => r.EntryId == partId.Value && r.Model == model && r.PromptVersion == promptVersion
            && r.Outcome == Complete, ct);
    }

    /// <summary>Records a run and returns its id, which the assertions it produced point at.</summary>
    public Task<string> RecordAsync(RunRecord run, CancellationToken ct = default) =>
        RecordAsync(run, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), _clock.GetUtcNow().UtcDateTime, ct);

    /// <summary>
    /// Records a run another device made, under its own id and end time, so taking it again changes nothing. Returns
    /// false when it is here already.
    /// </summary>
    public async Task<bool> RecordSharedAsync(RunRecord run, string id, DateTime finishedUtc, CancellationToken ct = default)
    {
        await using (var db = await contexts.CreateDbContextAsync(ct))
            if (await db.ClassificationRuns.AnyAsync(r => r.Id == id, ct)) return false;
        await RecordAsync(run with { Provider = Shared }, id, finishedUtc, ct);
        return true;
    }

    async Task<string> RecordAsync(RunRecord run, string id, DateTime finishedUtc, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        db.ClassificationRuns.Add(new ClassificationRun
        {
            Id = id,
            EntryId = run.EntryId.Value,
            ContentHash = run.ContentHash,
            Provider = run.Provider,
            Model = run.Model,
            PromptVersion = run.PromptVersion,
            SchemaVersion = run.SchemaVersion,
            PagesJson = JsonSerializer.Serialize(run.Pages),
            StartedUtc = run.StartedUtc,
            FinishedUtc = finishedUtc,
            Outcome = run.Outcome,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>
    /// Forgets the runs that read these copies, for "Forget its text": the book no longer counts as read by a model,
    /// and "Read it again" sends it to the model again while AI is on. Returns how many went.
    /// </summary>
    public async Task<int> ForgetAsync(IReadOnlyCollection<string> contentHashes, CancellationToken ct = default)
    {
        if (contentHashes.Count == 0) return 0;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClassificationRuns.Where(r => contentHashes.Contains(r.ContentHash)).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// The model of each entry's latest finished run, for the entries a model has read: all of them, or those of
    /// <paramref name="entryIds"/>.
    /// </summary>
    public async Task<Dictionary<EntryId, string>> GetReadByAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var complete = db.ClassificationRuns.AsNoTracking().Where(r => r.Outcome == Complete);
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).ToList();
            complete = complete.Where(r => ids.Contains(r.EntryId));
        }
        var runs = await complete.Select(r => new { r.EntryId, r.Model, r.FinishedUtc }).ToListAsync(ct);
        return runs.GroupBy(r => r.EntryId).ToDictionary(g => new EntryId(g.Key), g => g.MaxBy(r => r.FinishedUtc)!.Model);
    }

    /// <summary>
    /// Content classified by <paramref name="model"/> with this prompt version, and content classified only by
    /// another model or an earlier prompt: the count "Reclassify N documents" offers. Runs are counted by the copy
    /// they read, since Reclassify queues documents; the runs over an adventure inside a file don't count. A run taken
    /// from another device with this prompt version counts as current, as Reclassify leaves it alone.
    /// </summary>
    public async Task<ClassificationSummary> SummarizeAsync(string model, int promptVersion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var runs = await db.ClassificationRuns.AsNoTracking().Where(r => r.Outcome == Complete && r.Entry.Kind != EntryKind.Part)
            .Select(r => new { r.ContentHash, Current = (r.Model == model || r.Provider == Shared) && r.PromptVersion == promptVersion }).ToListAsync(ct);
        var byDocument = runs.GroupBy(r => r.ContentHash).Select(g => g.Any(r => r.Current)).ToList();
        return new ClassificationSummary(byDocument.Count(c => c), byDocument.Count(c => !c));
    }
}
