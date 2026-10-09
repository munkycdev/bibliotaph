using System.Globalization;
using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A classifier run as the Classify stage records it, finished or failed.</summary>
public sealed record RunRecord(
    long DocumentId, string ContentHash, string Provider, string Model, int PromptVersion, int SchemaVersion,
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
            DocumentId = run.DocumentId,
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
    /// The model of each document's latest finished run, for the documents a model has read: all of them, or those of
    /// <paramref name="documentIds"/>. A document is one version of a file's content, so an edited file needs a new run.
    /// </summary>
    public async Task<Dictionary<long, string>> GetReadByAsync(IReadOnlyCollection<long>? documentIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var complete = db.ClassificationRuns.AsNoTracking().Where(r => r.Outcome == Complete);
        if (documentIds is not null) complete = complete.Where(r => documentIds.Contains(r.DocumentId));
        var runs = await complete.Select(r => new { r.DocumentId, r.Model, r.FinishedUtc }).ToListAsync(ct);
        return runs.GroupBy(r => r.DocumentId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.FinishedUtc)!.Model);
    }

    /// <summary>
    /// Documents classified by <paramref name="model"/> with this prompt version, and documents classified only by
    /// another model or an earlier prompt: the count "Reclassify N documents" offers.
    /// </summary>
    public async Task<ClassificationSummary> SummarizeAsync(string model, int promptVersion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var runs = await db.ClassificationRuns.AsNoTracking().Where(r => r.Outcome == Complete)
            .Select(r => new { r.DocumentId, Current = r.Model == model && r.PromptVersion == promptVersion }).ToListAsync(ct);
        var byDocument = runs.GroupBy(r => r.DocumentId).Select(g => g.Any(r => r.Current)).ToList();
        return new ClassificationSummary(byDocument.Count(c => c), byDocument.Count(c => !c));
    }
}
