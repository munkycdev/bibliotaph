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

    /// <summary>
    /// Adds suggestions from a source that doesn't replace its earlier ones (a classifier run). A value already
    /// suggested by the same origin, in any state, is skipped, so a rejected one isn't proposed again. A term value
    /// whose term is still pending is held (<see cref="AssertionState.AwaitingTerm"/>) until the user decides the
    /// term; one whose term was rejected is dropped. Returns the number added.
    /// </summary>
    public async Task<int> AddSuggestionsAsync(long documentId, IReadOnlyList<MetadataProposal> proposals, CancellationToken ct = default)
    {
        if (proposals.Any(p => HintOrigins.Contains(p.Origin) || p.Origin == AssertionOrigin.User))
            throw new ArgumentException("Rule hints and the user's own values have their own methods.", nameof(proposals));

        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Assertions.Where(a => a.DocumentId == documentId).ToListAsync(ct);
        var vocabularies = proposals.Where(p => p.Field.Kind == FieldKind.Term).Select(p => p.Field.Vocabulary!).Distinct().ToList();
        var states = (await db.VocabularyTerms.Where(t => vocabularies.Contains(t.Vocabulary)).Select(t => new { t.Vocabulary, t.Key, t.State }).ToListAsync(ct))
            .ToDictionary(t => (t.Vocabulary, t.Key), t => t.State);
        var now = _clock.GetUtcNow().UtcDateTime;
        var added = 0;
        foreach (var proposal in proposals.DistinctBy(p => (p.Field.Key, p.Normalized, p.Origin)))
        {
            if (proposal.Normalized.Length == 0) continue;
            if (rows.Any(r => r.Field == proposal.Field.Key && r.NormalizedValue == proposal.Normalized && r.Origin == proposal.Origin)) continue;
            var state = AssertionState.Provisional;
            if (proposal.Field.Kind == FieldKind.Term && states.TryGetValue((proposal.Field.Vocabulary!, proposal.Value), out var term))
            {
                if (term == TermState.Rejected) continue;
                if (term == TermState.Pending) state = AssertionState.AwaitingTerm;
            }
            db.Assertions.Add(NewAssertion(documentId, proposal, state, now));
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// Stores a classifier run's suggestions (slice 2c), replacing the undecided ones earlier runs of the same origin left:
    /// a value suggested again keeps its row and its age, with this run's evidence; one not suggested again goes. What
    /// the user decided stays as it is, a rejected value is never suggested again (A04), and a term value whose term is
    /// pending is held until the user decides the term. Returns whether anything changed.
    /// </summary>
    public async Task<bool> ApplyRunAsync(long documentId, string runId, AssertionOrigin origin, IReadOnlyList<MetadataProposal> proposals, CancellationToken ct = default)
    {
        if (HintOrigins.Contains(origin) || origin == AssertionOrigin.User || proposals.Any(p => p.Origin != origin))
            throw new ArgumentException("A run stores its own origin's suggestions, and never rule hints or the user's values.", nameof(proposals));

        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Assertions.Where(a => a.DocumentId == documentId && a.Origin == origin).ToListAsync(ct);
        var rejected = (await db.Rejections.Where(r => r.DocumentId == documentId).Select(r => new { r.Field, r.NormalizedValue }).ToListAsync(ct))
            .Select(r => (r.Field, r.NormalizedValue)).ToHashSet();
        var vocabularies = proposals.Where(p => p.Field.Kind == FieldKind.Term).Select(p => p.Field.Vocabulary!).Distinct().ToList();
        var terms = (await db.VocabularyTerms.Where(t => vocabularies.Contains(t.Vocabulary)).Select(t => new { t.Vocabulary, t.Key, t.State }).ToListAsync(ct))
            .ToDictionary(t => (t.Vocabulary, t.Key), t => t.State);
        var wanted = proposals.Where(p => p.Normalized.Length > 0).DistinctBy(p => (p.Field.Key, p.Normalized)).ToList();
        var now = _clock.GetUtcNow().UtcDateTime;
        var changed = false;

        foreach (var row in rows.Where(r => r.State is AssertionState.Provisional or AssertionState.AwaitingTerm))
        {
            if (wanted.Any(p => p.Field.Key == row.Field && p.Normalized == row.NormalizedValue)) continue;
            db.Assertions.Remove(row);
            changed = true;
        }

        foreach (var proposal in wanted)
        {
            if (rejected.Contains((proposal.Field.Key, proposal.Normalized))) continue;
            var state = AssertionState.Provisional;
            if (proposal.Field.Kind == FieldKind.Term && terms.TryGetValue((proposal.Field.Vocabulary!, proposal.Value), out var term))
            {
                if (term == TermState.Rejected) continue;
                if (term == TermState.Pending) state = AssertionState.AwaitingTerm;
            }
            var existing = rows.FirstOrDefault(r => r.Field == proposal.Field.Key && r.NormalizedValue == proposal.Normalized);
            if (existing is null)
            {
                db.Assertions.Add(NewAssertion(documentId, proposal, state, now, runId));
                changed = true;
            }
            else if (existing.State is AssertionState.Provisional or AssertionState.AwaitingTerm)
            {
                // The same suggestion again: this run's evidence, but its age stays, so nothing looks new.
                existing.RunId = runId;
                existing.EvidenceQuote = proposal.Quote;
                existing.EvidencePagesJson = Pages(proposal);
                existing.FromSampling = proposal.FromSampling;
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync(ct);
        return changed;
    }

    static string? Pages(MetadataProposal proposal) => proposal.Pages is { Count: > 0 } pages ? JsonSerializer.Serialize(pages) : null;

    static Assertion NewAssertion(long documentId, MetadataProposal proposal, AssertionState state, DateTime now, string? runId = null) => new()
    {
        DocumentId = documentId,
        Field = proposal.Field.Key,
        ValueJson = JsonSerializer.Serialize(proposal.Value),
        NormalizedValue = proposal.Normalized,
        Origin = proposal.Origin,
        EvidenceQuote = proposal.Quote,
        EvidencePagesJson = Pages(proposal),
        FromSampling = proposal.FromSampling,
        RunId = runId,
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
        SetValues(db, documentId, field, values, rows, rejections, _clock.GetUtcNow().UtcDateTime);
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
        Reset(db, rows, rejections, _clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A review card's Reject: rejects <paramref name="rejected"/> (normalised values) and confirms
    /// <paramref name="kept"/> (stored values), so the field is settled on what it showed. With nothing to keep, only
    /// the rejections happen, and the next suggestion, if any, shows.
    /// </summary>
    public async Task RejectAndKeepAsync(long documentId, MetadataField field, IReadOnlyList<string> rejected, IReadOnlyList<string> kept, CancellationToken ct = default)
    {
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var (rows, rejections) = await LoadFieldAsync(db, documentId, field, ct);
            var now = _clock.GetUtcNow().UtcDateTime;
            foreach (var normalized in rejected) Reject(db, documentId, field, normalized, rows, rejections, now);
            await db.SaveChangesAsync(ct);
        }
        if (kept.Count > 0) await SetValuesAsync(documentId, field, kept, ct);
    }

    /// <summary>A field's assertions and rejections as they are now, so a decision about it can be undone exactly.</summary>
    public async Task<FieldSnapshot> SnapshotAsync(long documentId, MetadataField field, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (rows, rejections) = await LoadFieldAsync(db, documentId, field, ct);
        return Snapshot(documentId, field, rows, rejections);
    }

    /// <summary>
    /// Undo: puts a field back as <paramref name="snapshot"/> found it. Assertions added since are removed, the
    /// others get their state back, and so do the rejections. Terms added meanwhile stay in the vocabulary.
    /// </summary>
    public async Task RestoreAsync(FieldSnapshot snapshot, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (rows, rejections) = await LoadFieldAsync(db, snapshot.DocumentId, snapshot.Field, ct);
        Restore(db, snapshot, rows, rejections);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A bulk edit (slice 4e): the same changes to fields of many documents, in one transaction, by the inspector's
    /// per-value rules. Set makes a single-value field's value the user's own, as <see cref="SetValuesAsync"/> does.
    /// Add puts a value in a multi-value field and keeps the values it shows, as "Use this" does, which confirms them;
    /// Remove rejects a value the field shows, from every source now and later, as "Not right" does. Reset is "Reset to
    /// suggestion". A field with no change, and a document a change makes no difference to, are left as they are.
    /// Returns the snapshot, from before, of each field it changed, which <see cref="RestoreAsync(IReadOnlyCollection{FieldSnapshot}, CancellationToken)"/>
    /// puts back.
    /// </summary>
    public async Task<IReadOnlyList<FieldSnapshot>> ApplyBulkAsync(IReadOnlyCollection<long> documentIds, IReadOnlyList<BulkChange> changes,
        CancellationToken ct = default)
    {
        foreach (var change in changes)
        {
            if (change.Action == BulkAction.Reset ? change.Value is not null : string.IsNullOrEmpty(change.Value))
                throw new ArgumentException($"{change.Action} of {change.Field} has the wrong value.", nameof(changes));
            if (change.Action == BulkAction.Set && change.Field.Multiple || change.Action is BulkAction.Add or BulkAction.Remove && !change.Field.Multiple)
                throw new ArgumentException($"{change.Action} doesn't apply to {change.Field}.", nameof(changes));
        }
        var byField = changes.GroupBy(c => c.Field).ToList();
        if (byField.Any(g => g.Count() > 1 && g.Any(c => c.Action is BulkAction.Set or BulkAction.Reset)))
            throw new ArgumentException("A field that is set or reset has no other change.", nameof(changes));
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0 || byField.Count == 0) return [];

        await using var db = await contexts.CreateDbContextAsync(ct);
        var keys = byField.Select(g => g.Key.Key).ToList();
        var (rows, rejected) = await LoadFieldsAsync(db, ids, keys, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var snapshots = new List<FieldSnapshot>();
        foreach (var documentId in ids)
            foreach (var group in byField)
            {
                var field = group.Key;
                var fieldRows = rows[(documentId, field.Key)].ToList();
                var rejections = rejected[(documentId, field.Key)].ToList();
                var before = Snapshot(documentId, field, fieldRows, rejections);
                if (ApplyBulk(db, documentId, field, [.. group], fieldRows, rejections, now)) snapshots.Add(before);
            }
        await db.SaveChangesAsync(ct);
        return snapshots;
    }

    /// <summary>Undo for a bulk edit: puts every field back as its snapshot found it, in one transaction.</summary>
    public async Task RestoreAsync(IReadOnlyCollection<FieldSnapshot> snapshots, CancellationToken ct = default)
    {
        if (snapshots.Count == 0) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (rows, rejected) = await LoadFieldsAsync(db, [.. snapshots.Select(s => s.DocumentId).Distinct()], [.. snapshots.Select(s => s.Field.Key).Distinct()], ct);
        foreach (var snapshot in snapshots)
        {
            var key = (snapshot.DocumentId, snapshot.Field.Key);
            Restore(db, snapshot, [.. rows[key]], [.. rejected[key]]);
        }
        await db.SaveChangesAsync(ct);
    }

    static async Task<(List<Assertion> Rows, List<Rejection> Rejections)> LoadFieldAsync(CatalogDbContext db, long documentId, MetadataField field, CancellationToken ct) =>
        (await db.Assertions.Where(a => a.DocumentId == documentId && a.Field == field.Key).ToListAsync(ct),
         await db.Rejections.Where(r => r.DocumentId == documentId && r.Field == field.Key).ToListAsync(ct));

    /// <summary>Several documents' rows for several fields, by document and field, tracked so they can be changed.</summary>
    static async Task<(ILookup<(long, string), Assertion> Rows, ILookup<(long, string), Rejection> Rejections)> LoadFieldsAsync(CatalogDbContext db,
        List<long> documentIds, List<string> fields, CancellationToken ct) =>
        ((await db.Assertions.Where(a => documentIds.Contains(a.DocumentId) && fields.Contains(a.Field)).ToListAsync(ct)).ToLookup(a => (a.DocumentId, a.Field)),
         (await db.Rejections.Where(r => documentIds.Contains(r.DocumentId) && fields.Contains(r.Field)).ToListAsync(ct)).ToLookup(r => (r.DocumentId, r.Field)));

    static EffectiveField Compute(MetadataField field, List<Assertion> rows, List<Rejection> rejections) =>
        EffectiveMetadata.Compute(rows.Select(ToClaim), rejections.Select(r => (r.Field, r.NormalizedValue)))[field];

    /// <summary>
    /// <see cref="SetValuesAsync"/>'s rules on one field's loaded rows. Returns whether anything changed: typing the
    /// values a field already shows as confirmed changes nothing.
    /// </summary>
    static bool SetValues(CatalogDbContext db, long documentId, MetadataField field, IReadOnlyList<string> values, List<Assertion> rows,
        List<Rejection> rejections, DateTime now)
    {
        var current = Compute(field, rows, rejections);
        var wanted = values.Select(v => (Value: v, Normalized: MetadataValues.Normalize(field, v)))
            .Where(v => v.Normalized.Length > 0).DistinctBy(v => v.Normalized).ToList();
        if (!field.Multiple && wanted.Count > 1) wanted = wanted[..1];
        var changed = false;

        if (field.Multiple || wanted.Count == 0)
            foreach (var gone in current.Values.Where(v => wanted.All(w => w.Normalized != v.Normalized)))
                changed |= Reject(db, documentId, field, gone.Normalized, rows, rejections, now);

        foreach (var (value, normalized) in wanted)
        {
            if (rejections.FirstOrDefault(r => r.NormalizedValue == normalized) is { } rejection)
            {
                db.Rejections.Remove(rejection);
                rejections.Remove(rejection);
                changed = true;
            }
            var same = rows.Where(r => r.NormalizedValue == normalized).ToList();
            if (same.Any(r => r.State == AssertionState.Confirmed)) continue;
            var best = same.Where(r => r.State is AssertionState.Provisional or AssertionState.Rejected)
                .OrderByDescending(r => EffectiveMetadata.Priority(r.Origin)).ThenByDescending(r => r.CreatedUtc).FirstOrDefault();
            if (best is not null && best.Origin != AssertionOrigin.User)
            {
                best.State = AssertionState.Confirmed;
                best.DecidedUtc = now;
            }
            else
            {
                var added = NewAssertion(documentId, new MetadataProposal(field, value, AssertionOrigin.User), AssertionState.Confirmed, now);
                db.Assertions.Add(added);
                rows.Add(added);
            }
            changed = true;
        }

        if (!field.Multiple && wanted.Count == 1)
            foreach (var old in rows.Where(r => r.State == AssertionState.Confirmed && r.NormalizedValue != wanted[0].Normalized))
            {
                old.State = AssertionState.Superseded;
                old.DecidedUtc = now;
                changed = true;
            }
        return changed;
    }

    /// <summary><see cref="ResetAsync"/>'s rules on one field's loaded rows. Returns whether there was anything to forget.</summary>
    static bool Reset(CatalogDbContext db, List<Assertion> rows, List<Rejection> rejections, DateTime now)
    {
        var changed = rejections.Count > 0;
        db.Rejections.RemoveRange(rejections);
        rejections.Clear();
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
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// One field of one document in a bulk edit. When every value to add already shows confirmed, there is nothing to
    /// add; otherwise the field is saved as the inspector saves an edited list: what it shows, less what is removed,
    /// plus what is added. With nothing to add, only the removed values it shows are rejected, and the rest is left alone.
    /// </summary>
    static bool ApplyBulk(CatalogDbContext db, long documentId, MetadataField field, IReadOnlyList<BulkChange> changes, List<Assertion> rows,
        List<Rejection> rejections, DateTime now)
    {
        if (changes is [{ Action: BulkAction.Reset }]) return Reset(db, rows, rejections, now);
        if (changes is [{ Action: BulkAction.Set, Value: { } value }]) return SetValues(db, documentId, field, [value], rows, rejections, now);

        var current = Compute(field, rows, rejections);
        var removed = changes.Where(c => c.Action == BulkAction.Remove).Select(c => MetadataValues.Normalize(field, c.Value!)).ToHashSet(StringComparer.Ordinal);
        var added = changes.Where(c => c.Action == BulkAction.Add).Select(c => c.Value!).Where(v => !removed.Contains(MetadataValues.Normalize(field, v))).ToList();
        if (added.Any(v => !current.Values.Any(c => c.Confirmed && c.Normalized == MetadataValues.Normalize(field, v))))
            return SetValues(db, documentId, field, [.. current.Values.Where(v => !removed.Contains(v.Normalized)).Select(v => v.Value), .. added],
                rows, rejections, now);

        var changed = false;
        foreach (var normalized in removed.Where(r => current.Values.Any(v => v.Normalized == r)))
            changed |= Reject(db, documentId, field, normalized, rows, rejections, now);
        return changed;
    }

    static FieldSnapshot Snapshot(long documentId, MetadataField field, List<Assertion> rows, List<Rejection> rejections) =>
        new(documentId, field,
            [.. rows.Select(r => new FieldSnapshot.Row(r.Id, r.State, r.DecidedUtc))],
            [.. rejections.Select(r => new FieldSnapshot.Rejected(r.NormalizedValue, r.CreatedUtc))]);

    static void Restore(CatalogDbContext db, FieldSnapshot snapshot, List<Assertion> rows, List<Rejection> rejections)
    {
        var before = snapshot.Rows.ToDictionary(r => r.Id);
        foreach (var row in rows)
        {
            if (!before.TryGetValue(row.Id, out var was))
            {
                db.Assertions.Remove(row);
                continue;
            }
            row.State = was.State;
            row.DecidedUtc = was.DecidedUtc;
        }
        db.Rejections.RemoveRange(rejections.Where(r => snapshot.Rejections.All(s => s.Normalized != r.NormalizedValue)));
        foreach (var gone in snapshot.Rejections.Where(s => rejections.All(r => r.NormalizedValue != s.Normalized)))
            db.Rejections.Add(new Rejection { DocumentId = snapshot.DocumentId, Field = snapshot.Field.Key, NormalizedValue = gone.Normalized, CreatedUtc = gone.CreatedUtc });
    }

    /// <summary>"Not right" on one field's loaded rows. Returns whether anything changed.</summary>
    static bool Reject(CatalogDbContext db, long documentId, MetadataField field, string normalized, List<Assertion> rows, List<Rejection> rejections, DateTime now)
    {
        var changed = false;
        if (rejections.All(r => r.NormalizedValue != normalized))
        {
            var rejection = new Rejection { DocumentId = documentId, Field = field.Key, NormalizedValue = normalized, CreatedUtc = now };
            db.Rejections.Add(rejection);
            rejections.Add(rejection);
            changed = true;
        }
        foreach (var row in rows.Where(r => r.NormalizedValue == normalized && r.State is AssertionState.Provisional or AssertionState.Confirmed))
        {
            row.State = AssertionState.Rejected;
            row.DecidedUtc = now;
            changed = true;
        }
        return changed;
    }
}

/// <summary>What a bulk edit does to a field on every selected document (slice 4e plan, choices 3 and 4).</summary>
public enum BulkAction
{
    /// <summary>Makes a single-value field's value the user's own.</summary>
    Set,

    /// <summary>Puts a value in a multi-value field.</summary>
    Add,

    /// <summary>Takes a value out of a multi-value field and rejects it, so no source suggests it again.</summary>
    Remove,

    /// <summary>Back to suggestions: forgets the user's decisions about the field.</summary>
    Reset,
}

/// <summary>One change in a bulk edit. <see cref="Value"/> is in stored form (a term's key, "1-5"); Reset has none.</summary>
public sealed record BulkChange(MetadataField Field, BulkAction Action, string? Value = null);

/// <summary>A field's assertion states and rejections at one moment, which <see cref="MetadataStore.RestoreAsync(FieldSnapshot, CancellationToken)"/> puts back.</summary>
public sealed record FieldSnapshot(long DocumentId, MetadataField Field, IReadOnlyList<FieldSnapshot.Row> Rows, IReadOnlyList<FieldSnapshot.Rejected> Rejections)
{
    public sealed record Row(long Id, AssertionState State, DateTime? DecidedUtc);

    public sealed record Rejected(string Normalized, DateTime CreatedUtc);
}
