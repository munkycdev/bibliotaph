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

/// <summary>How much of the library the current model and prompt have classified, and how much an earlier one did.</summary>
public sealed record ClassificationSummary(int Current, int Earlier);

/// <summary>
/// Classification runs in catalog.db (choice 5): which model and prompt read which content, and with what outcome. The
/// Classify stage skips content the current model and prompt have already classified, so neither an index.db rebuild
/// nor a restart sends a book to the model again.
/// </summary>
public sealed class ClassificationStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>The outcome of a run whose suggestions were stored.</summary>
    public const string Complete = "complete";

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Whether <paramref name="model"/> and this prompt version have already classified this content.</summary>
    public async Task<bool> HasRunAsync(string contentHash, string model, int promptVersion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ClassificationRuns.AnyAsync(r => r.ContentHash == contentHash && r.Model == model && r.PromptVersion == promptVersion && r.Outcome == Complete, ct);
    }

    /// <summary>Records a run and returns its id, which the assertions it produced point at.</summary>
    public async Task<string> RecordAsync(RunRecord run, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
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
            FinishedUtc = _clock.GetUtcNow().UtcDateTime,
            Outcome = run.Outcome,
        });
        await db.SaveChangesAsync(ct);
        return id;
    }

    /// <summary>
    /// Forgets the runs that read these copies, for "Forget its text" (slice 4i plan, choice 6): the book no longer
    /// counts as read by a model, and "Read it again" sends it to the model again while AI is on. Returns how many went.
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
    /// they read, since Reclassify queues documents.
    /// </summary>
    public async Task<ClassificationSummary> SummarizeAsync(string model, int promptVersion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var runs = await db.ClassificationRuns.AsNoTracking().Where(r => r.Outcome == Complete)
            .Select(r => new { r.ContentHash, Current = r.Model == model && r.PromptVersion == promptVersion }).ToListAsync(ct);
        var byDocument = runs.GroupBy(r => r.ContentHash).Select(g => g.Any(r => r.Current)).ToList();
        return new ClassificationSummary(byDocument.Count(c => c), byDocument.Count(c => !c));
    }
}
