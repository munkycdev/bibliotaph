using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A document's assertions and rejections, which <see cref="EffectiveMetadata"/> turns into its metadata.</summary>
public sealed record DocumentMetadata(long DocumentId, IReadOnlyList<MetadataClaim> Claims, IReadOnlyList<(string Field, string Normalized)> Rejections)
{
    public EffectiveMetadata Compute() => EffectiveMetadata.Compute(Claims, Rejections);
}

/// <summary>
/// Metadata assertions in catalog.db. Sources add suggestions; the user's decisions change assertion states and add
/// rejections. Nothing here overwrites a value: the effective metadata is always computed from these rows, so a
/// reindex, a new rule or a new model can only add suggestions, never undo what the user said (A04).
/// </summary>
public sealed class MetadataStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>The origins the Hints from names stage owns: it replaces their suggestions each time it runs.</summary>
    public static IReadOnlyList<AssertionOrigin> HintOrigins { get; } =
        [AssertionOrigin.Embedded, AssertionOrigin.Folder, AssertionOrigin.Filename, AssertionOrigin.Rule];

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<DocumentMetadata> GetAsync(long documentId, CancellationToken ct = default) =>
        (await GetManyAsync([documentId], ct)).GetValueOrDefault(documentId) ?? new DocumentMetadata(documentId, [], []);

    /// <summary>Metadata for <paramref name="documentIds"/>, or for every document with any when it is null.</summary>
    public async Task<IReadOnlyDictionary<long, DocumentMetadata>> GetManyAsync(IReadOnlyCollection<long>? documentIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var assertions = db.Assertions.AsNoTracking();
        var rejections = db.Rejections.AsNoTracking();
        if (documentIds is not null)
        {
            if (documentIds.Count == 0) return new Dictionary<long, DocumentMetadata>();
            assertions = assertions.Where(a => documentIds.Contains(a.DocumentId));
            rejections = rejections.Where(r => documentIds.Contains(r.DocumentId));
        }
        var claims = (await assertions.ToListAsync(ct)).ToLookup(a => a.DocumentId, ToClaim);
        var rejected = (await rejections.Select(r => new { r.DocumentId, r.Field, r.NormalizedValue }).ToListAsync(ct))
            .ToLookup(r => r.DocumentId, r => (r.Field, r.NormalizedValue));
        return claims.Select(g => g.Key).Union(rejected.Select(g => g.Key))
            .ToDictionary(id => id, id => new DocumentMetadata(id, [.. claims[id]], [.. rejected[id]]));
    }

    static MetadataClaim ToClaim(Assertion a) => new(
        a.Id, a.Field, ReadValue(a.ValueJson), a.NormalizedValue, a.Origin, a.State, a.CreatedUtc, a.EvidenceQuote,
        a.EvidencePagesJson is { } pages ? JsonSerializer.Deserialize<int[]>(pages) : null, a.FromSampling, a.DecidedUtc);

    static string ReadValue(string json) => JsonSerializer.Deserialize<string>(json) ?? "";

    /// <summary>
    /// Replaces the document's rule-hint suggestions with <paramref name="proposals"/>. Only provisional hint rows
    /// change: confirmed, rejected and superseded rows, and every other origin's, stay as they are. A suggestion that
    /// is still proposed keeps its row and its age, so rerunning the stage changes nothing the user has seen.
    /// Returns whether anything changed.
    /// </summary>
    public async Task<bool> ReplaceHintsAsync(long documentId, IReadOnlyList<MetadataProposal> proposals, CancellationToken ct = default)
    {
        if (proposals.Any(p => !HintOrigins.Contains(p.Origin)))
            throw new ArgumentException("Only rule-hint origins can be replaced.", nameof(proposals));

        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Assertions.Where(a => a.DocumentId == documentId && HintOrigins.Contains(a.Origin)).ToListAsync(ct);
        var wanted = proposals.DistinctBy(p => (p.Field.Key, p.Normalized, p.Origin)).ToList();
        var now = _clock.GetUtcNow().UtcDateTime;
        var changed = false;

        foreach (var row in rows.Where(r => r.State == AssertionState.Provisional))
        {
            var keep = wanted.FirstOrDefault(p => p.Field.Key == row.Field && p.Normalized == row.NormalizedValue && p.Origin == row.Origin);
            if (keep is null)
            {
                db.Assertions.Remove(row);
                changed = true;
            }
            else if (row.EvidenceQuote != keep.Quote)
            {
                row.EvidenceQuote = keep.Quote;
                changed = true;
            }
        }

        foreach (var proposal in wanted)
        {
            // Already there in some state: provisional (kept above), or decided by the user, whose word stands.
            if (rows.Any(r => r.Field == proposal.Field.Key && r.NormalizedValue == proposal.Normalized && r.Origin == proposal.Origin)) continue;
            db.Assertions.Add(NewAssertion(documentId, proposal, AssertionState.Provisional, now));
            changed = true;
        }

        if (changed) await db.SaveChangesAsync(ct);
        return changed;
    }

    static Assertion NewAssertion(long documentId, MetadataProposal proposal, AssertionState state, DateTime now) => new()
    {
        DocumentId = documentId,
        Field = proposal.Field.Key,
        ValueJson = JsonSerializer.Serialize(proposal.Value),
        NormalizedValue = proposal.Normalized,
        Origin = proposal.Origin,
        EvidenceQuote = proposal.Quote,
        EvidencePagesJson = proposal.Pages is { Count: > 0 } pages ? JsonSerializer.Serialize(pages) : null,
        State = state,
        CreatedUtc = now,
        DecidedUtc = state == AssertionState.Provisional ? null : now,
    };

    /// <summary>
    /// The user's own values for a field, in stored form (term keys, "1-5"): what the inspector's editor saves.
    /// Each value becomes confirmed, by confirming a suggestion that already has it (keeping its evidence) or by adding
    /// the user's own assertion. In a multi-value field, a value that was showing and isn't in the list any more is
    /// rejected. In a single-value field the new value supersedes the old confirmation; an empty list rejects the
    /// current value, so the next suggestion (if any) shows.
    /// </summary>
    public async Task SetValuesAsync(long documentId, MetadataField field, IReadOnlyList<string> values, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (rows, rejections) = await LoadFieldAsync(db, documentId, field, ct);
        var current = Compute(field, rows, rejections);
        var now = _clock.GetUtcNow().UtcDateTime;
        var wanted = values.Select(v => (Value: v, Normalized: MetadataValues.Normalize(field, v)))
            .Where(v => v.Normalized.Length > 0).DistinctBy(v => v.Normalized).ToList();
        if (!field.Multiple && wanted.Count > 1) wanted = wanted[..1];

        if (field.Multiple || wanted.Count == 0)
            foreach (var gone in current.Values.Where(v => wanted.All(w => w.Normalized != v.Normalized)))
                Reject(db, documentId, field, gone.Normalized, rows, rejections, now);

        foreach (var (value, normalized) in wanted)
        {
            if (rejections.FirstOrDefault(r => r.NormalizedValue == normalized) is { } rejection) db.Rejections.Remove(rejection);
            var same = rows.Where(r => r.NormalizedValue == normalized).ToList();
            if (same.Any(r => r.State == AssertionState.Confirmed)) continue;
            var best = same.Where(r => r.State is AssertionState.Provisional or AssertionState.Rejected)
                .OrderByDescending(r => EffectiveMetadata.Priority(r.Origin)).ThenByDescending(r => r.CreatedUtc).FirstOrDefault();
            if (best is not null && best.Origin != AssertionOrigin.User)
            {
                best.State = AssertionState.Confirmed;
                best.DecidedUtc = now;
            }
            else db.Assertions.Add(NewAssertion(documentId, new MetadataProposal(field, value, AssertionOrigin.User), AssertionState.Confirmed, now));
        }

        if (!field.Multiple && wanted.Count == 1)
            foreach (var old in rows.Where(r => r.State == AssertionState.Confirmed && r.NormalizedValue != wanted[0].Normalized))
            {
                old.State = AssertionState.Superseded;
                old.DecidedUtc = now;
            }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Keep this": confirms the values a field shows now, so no later suggestion replaces them.</summary>
    public async Task ConfirmAsync(long documentId, MetadataField field, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (rows, rejections) = await LoadFieldAsync(db, documentId, field, ct);
        var current = Compute(field, rows, rejections);
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var value in current.Values.Where(v => !v.Confirmed))
        {
            var row = rows.First(r => r.Id == value.Source.Id);
            row.State = AssertionState.Confirmed;
            row.DecidedUtc = now;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Not right": rejects one value for this document, from every source now and later.</summary>
    public async Task RejectAsync(long documentId, MetadataField field, string normalized, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (rows, rejections) = await LoadFieldAsync(db, documentId, field, ct);
        Reject(db, documentId, field, normalized, rows, rejections, _clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// "Reset to suggestion": forgets the user's decisions about a field. Their own values are superseded, their
    /// confirmations and rejections of suggestions are undone, and the field shows what the sources suggest again.
    /// </summary>
    public async Task ResetAsync(long documentId, MetadataField field, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (rows, rejections) = await LoadFieldAsync(db, documentId, field, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        db.Rejections.RemoveRange(rejections);
        foreach (var row in rows.Where(r => r.State is AssertionState.Confirmed or AssertionState.Rejected or AssertionState.Superseded))
        {
            if (row.Origin == AssertionOrigin.User)
            {
                if (row.State == AssertionState.Superseded) continue;
                row.State = AssertionState.Superseded;
                row.DecidedUtc = now;
            }
            else
            {
                row.State = AssertionState.Provisional;
                row.DecidedUtc = null;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    static async Task<(List<Assertion> Rows, List<Rejection> Rejections)> LoadFieldAsync(CatalogDbContext db, long documentId, MetadataField field, CancellationToken ct) =>
        (await db.Assertions.Where(a => a.DocumentId == documentId && a.Field == field.Key).ToListAsync(ct),
         await db.Rejections.Where(r => r.DocumentId == documentId && r.Field == field.Key).ToListAsync(ct));

    static EffectiveField Compute(MetadataField field, List<Assertion> rows, List<Rejection> rejections) =>
        EffectiveMetadata.Compute(rows.Select(ToClaim), rejections.Select(r => (r.Field, r.NormalizedValue)))[field];

    static void Reject(CatalogDbContext db, long documentId, MetadataField field, string normalized, List<Assertion> rows, List<Rejection> rejections, DateTime now)
    {
        if (rejections.All(r => r.NormalizedValue != normalized))
        {
            var rejection = new Rejection { DocumentId = documentId, Field = field.Key, NormalizedValue = normalized, CreatedUtc = now };
            db.Rejections.Add(rejection);
            rejections.Add(rejection);
        }
        foreach (var row in rows.Where(r => r.NormalizedValue == normalized && r.State is AssertionState.Provisional or AssertionState.Confirmed))
        {
            row.State = AssertionState.Rejected;
            row.DecidedUtc = now;
        }
    }
}
