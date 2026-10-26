using System.Text.Json;
using System.Text.Json.Serialization;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A whole-book scan of one copy of a file, as far as it has got.</summary>
/// <param name="ContentHash">The copy it read; every copy with the same content shares it.</param>
/// <param name="Provider">The model server it asked, as classification runs record it.</param>
/// <param name="Model">The model that read it.</param>
/// <param name="PromptVersion">The prompt it was read with.</param>
/// <param name="Chapters">The chapters in page order, with what was found in those read so far.</param>
/// <param name="StartedUtc">When the first chapter was sent.</param>
/// <param name="FinishedUtc">When the last chapter was read; null while chapters wait.</param>
public sealed record ChapterScanRecord(string ContentHash, string Provider, string Model, int PromptVersion, IReadOnlyList<ScannedChapter> Chapters,
    DateTime StartedUtc, DateTime? FinishedUtc)
{
    public bool IsFinished => FinishedUtc is not null;
}

/// <summary>
/// Whole-book scans in catalog.db, by the content hash of the copy they read, so what the AI found in each chapter
/// outlives an index.db rebuild and the same file is never read twice. They describe the file rather than anything the
/// user decided, so they aren't part of library sync; a device connected to the web library shares its finished scans
/// there instead, by content, and takes the others' (<see cref="ClassificationStore.Shared"/>).
/// </summary>
public sealed class ChapterScanStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The scan of the copy with <paramref name="contentHash"/>, finished or not, or null when none has started.</summary>
    public async Task<ChapterScanRecord?> GetAsync(string contentHash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.ChapterScans.AsNoTracking().SingleOrDefaultAsync(s => s.ContentHash == contentHash, ct);
        return row is null ? null : ToRecord(row);
    }

    /// <summary>The scans of these copies that have anything in them, by content hash.</summary>
    public async Task<IReadOnlyDictionary<string, ChapterScanRecord>> GetManyAsync(IReadOnlyCollection<string> contentHashes, CancellationToken ct = default)
    {
        var found = new Dictionary<string, ChapterScanRecord>(StringComparer.Ordinal);
        if (contentHashes.Count == 0) return found;
        await using var db = await contexts.CreateDbContextAsync(ct);
        foreach (var hashes in contentHashes.Distinct().Chunk(500))
            foreach (var row in await db.ChapterScans.AsNoTracking().Where(s => hashes.Contains(s.ContentHash)).ToListAsync(ct))
                found[row.ContentHash] = ToRecord(row);
        return found;
    }

    /// <summary>Every scan, finished or not: what index.db is filled from when it is built again.</summary>
    public async Task<IReadOnlyList<ChapterScanRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.ChapterScans.AsNoTracking().ToListAsync(ct)).Select(ToRecord)];
    }

    /// <summary>
    /// Saves how far the scan of a copy has got, replacing what was saved before; with <paramref name="finished"/>
    /// it is marked done and isn't read again while the file stays the same.
    /// </summary>
    public async Task SaveAsync(string contentHash, string provider, string model, int promptVersion, IReadOnlyList<ScannedChapter> chapters, DateTime startedUtc,
        bool finished, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.ChapterScans.SingleOrDefaultAsync(s => s.ContentHash == contentHash, ct);
        if (row is null)
        {
            row = new ChapterScan { ContentHash = contentHash, Provider = provider, Model = model, ChaptersJson = "[]" };
            db.ChapterScans.Add(row);
        }
        row.Provider = provider;
        row.Model = model;
        row.PromptVersion = promptVersion;
        row.ChaptersJson = JsonSerializer.Serialize(chapters, Json);
        row.StartedUtc = startedUtc;
        row.FinishedUtc = finished ? _clock.GetUtcNow().UtcDateTime : null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>This device's own finished scans that finished after <paramref name="since"/>, oldest first, to share with its other devices.</summary>
    public async Task<IReadOnlyList<ChapterScanRecord>> GetFinishedAsync(DateTime? since, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var finished = db.ChapterScans.AsNoTracking().Where(s => s.FinishedUtc != null && s.Provider != ClassificationStore.Shared);
        if (since is { } after) finished = finished.Where(s => s.FinishedUtc > after);
        return [.. (await finished.OrderBy(s => s.FinishedUtc).ToListAsync(ct)).Select(ToRecord)];
    }

    /// <summary>
    /// Keeps a finished scan another device made, as it was made, in place of an unfinished or older one here. Returns
    /// false when this device already has it or a newer one.
    /// </summary>
    public async Task<bool> SaveSharedAsync(ChapterScanRecord scan, CancellationToken ct = default)
    {
        if (scan.FinishedUtc is not { } finished) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.ChapterScans.SingleOrDefaultAsync(s => s.ContentHash == scan.ContentHash, ct);
        if (row is { FinishedUtc: { } held } && (row.PromptVersion > scan.PromptVersion || (row.PromptVersion == scan.PromptVersion && held >= finished))) return false;
        if (row is null)
        {
            row = new ChapterScan { ContentHash = scan.ContentHash, Provider = ClassificationStore.Shared, Model = scan.Model, ChaptersJson = "[]" };
            db.ChapterScans.Add(row);
        }
        row.Provider = ClassificationStore.Shared;
        row.Model = scan.Model;
        row.PromptVersion = scan.PromptVersion;
        row.ChaptersJson = JsonSerializer.Serialize(scan.Chapters, Json);
        row.StartedUtc = scan.StartedUtc;
        row.FinishedUtc = finished;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Forgets the scans of these copies, for "Forget its text" and "Scan again": they are read from the start the next
    /// time. Returns how many went.
    /// </summary>
    public async Task<int> ForgetAsync(IReadOnlyCollection<string> contentHashes, CancellationToken ct = default)
    {
        if (contentHashes.Count == 0) return 0;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.ChapterScans.Where(s => contentHashes.Contains(s.ContentHash)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Forgets the scan of one document's content, for "Scan again". Returns whether there was one.</summary>
    public async Task<bool> ForgetDocumentAsync(long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var hash = await db.Documents.Where(d => d.Id == documentId).Select(d => d.ContentHash).SingleOrDefaultAsync(ct);
        return hash is not null && await db.ChapterScans.Where(s => s.ContentHash == hash).ExecuteDeleteAsync(ct) > 0;
    }

    static ChapterScanRecord ToRecord(ChapterScan row) => new(row.ContentHash, row.Provider, row.Model, row.PromptVersion, Read(row.ChaptersJson),
        row.StartedUtc, row.FinishedUtc);

    /// <summary>A scan whose chapters can't be read back is treated as not started, so it is simply read again.</summary>
    static List<ScannedChapter> Read(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ScannedChapter>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
