using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A folder rule as Needs review and Settings show it, with the library folder's own path for naming it.</summary>
public sealed record FolderRuleInfo(long Id, FolderKey Folder, string RootPath, string Field, string Value, FolderRuleState State, int Books, DateTime CreatedUtc);

/// <summary>A folder rule's state before a change, so the change can be undone.</summary>
public sealed record FolderRuleWas(long Id, FolderRuleState State, DateTime? DecidedUtc);

/// <summary>
/// Easier corrections in catalog.db (scenario review, row 8): the books the user said were cataloged wrong and left
/// unfinished, which wait in Needs review, and the folder rules proposed from the user's repeated corrections.
/// </summary>
public sealed class CorrectionStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Remembers that the user left this book's correction unfinished. False when it was already waiting.</summary>
    public async Task<bool> RequestAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.CorrectionRequests.AnyAsync(r => r.EntryId == entryId.Value, ct)) return false;
        db.CorrectionRequests.Add(new CorrectionRequest { EntryId = entryId.Value, CreatedUtc = _clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The book's correction is finished, or dismissed. Returns when it was asked for, for Undo, or null when nothing waited.</summary>
    public async Task<DateTime?> ClearAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.CorrectionRequests.FirstOrDefaultAsync(r => r.EntryId == entryId.Value, ct) is not { } row) return null;
        db.CorrectionRequests.Remove(row);
        await db.SaveChangesAsync(ct);
        return row.CreatedUtc;
    }

    /// <summary>Undo for <see cref="ClearAsync"/>: the card waits again, with its first date.</summary>
    public async Task RestoreAsync(EntryId entryId, DateTime createdUtc, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.CorrectionRequests.AnyAsync(r => r.EntryId == entryId.Value, ct)) return;
        if (!await db.Entries.AnyAsync(e => e.Id == entryId.Value, ct)) return;
        db.CorrectionRequests.Add(new CorrectionRequest { EntryId = entryId.Value, CreatedUtc = createdUtc });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The books whose correction waits, oldest first. A card that joined another as a copy isn't listed.</summary>
    public async Task<IReadOnlyList<(EntryId EntryId, DateTime CreatedUtc)>> GetRequestsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.CorrectionRequests.AsNoTracking()
            .Join(db.Entries.Where(e => e.MergedIntoEntryId == null), r => r.EntryId, e => e.Id, (r, _) => r)
            .OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id)
            .Select(r => new { r.EntryId, r.CreatedUtc })
            .ToListAsync(ct);
        return [.. rows.Select(r => (new EntryId(r.EntryId), r.CreatedUtc))];
    }

    /// <summary>Every folder rule, or those in one state, by folder.</summary>
    public async Task<IReadOnlyList<FolderRuleInfo>> GetRulesAsync(FolderRuleState? state = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rules = db.FolderRules.AsNoTracking();
        if (state is { } wanted) rules = rules.Where(r => r.State == wanted);
        var rows = await rules
            .Join(db.SourceRoots.Where(s => s.Availability != SourceRootAvailability.RemovedByUser), r => r.SourceRootId, s => s.Id, (r, s) => new { Rule = r, s.Path })
            .ToListAsync(ct);
        return [.. rows
            .Select(r => new FolderRuleInfo(r.Rule.Id, new FolderKey(r.Rule.SourceRootId, r.Rule.Folder), r.Path, r.Rule.Field, r.Rule.Value, r.Rule.State,
                r.Rule.Books, r.Rule.CreatedUtc))
            .OrderBy(r => r.RootPath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Folder.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Field, StringComparer.Ordinal).ThenBy(r => r.Id)];
    }

    /// <summary>
    /// Proposes a folder rule. False when the folder already has one for this field and value, in any state: a rule
    /// is proposed once, and a declined one stays declined.
    /// </summary>
    public async Task<bool> ProposeAsync(FolderKey folder, string field, string value, int books, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var existing = await db.FolderRules.AsNoTracking()
            .Where(r => r.SourceRootId == folder.RootId && r.Field == field && r.Value == value)
            .Select(r => r.Folder).ToListAsync(ct);
        if (existing.Any(f => new FolderKey(folder.RootId, f) == folder)) return false;
        db.FolderRules.Add(new FolderRule
        {
            SourceRootId = folder.RootId,
            Folder = folder.Folder,
            Field = field,
            Value = value,
            State = FolderRuleState.Proposed,
            Books = books,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Switches a rule on or off. A field with one value takes one rule per folder, so switching one on switches off
    /// the folder's other rules for that field when <paramref name="oneValue"/>. Returns every rule it changed as it was,
    /// for <see cref="RestoreAsync(IReadOnlyCollection{FolderRuleWas}, CancellationToken)"/>; empty when the rule has gone.
    /// </summary>
    public async Task<IReadOnlyList<FolderRuleWas>> SetStateAsync(long ruleId, FolderRuleState state, bool oneValue, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.FolderRules.FirstOrDefaultAsync(r => r.Id == ruleId, ct) is not { } rule) return [];
        var changed = new List<FolderRule> { rule };
        if (state == FolderRuleState.On && oneValue)
        {
            var key = new FolderKey(rule.SourceRootId, rule.Folder);
            var others = await db.FolderRules
                .Where(r => r.Id != ruleId && r.SourceRootId == rule.SourceRootId && r.Field == rule.Field && r.State == FolderRuleState.On).ToListAsync(ct);
            changed.AddRange(others.Where(r => new FolderKey(r.SourceRootId, r.Folder) == key));
        }
        var was = changed.Select(r => new FolderRuleWas(r.Id, r.State, r.DecidedUtc)).ToList();
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var row in changed)
        {
            row.State = row == rule ? state : FolderRuleState.Off;
            row.DecidedUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return was;
    }

    /// <summary>Undo: puts rules back as <see cref="SetStateAsync"/> found them.</summary>
    public async Task RestoreAsync(IReadOnlyCollection<FolderRuleWas> rules, CancellationToken ct = default)
    {
        if (rules.Count == 0) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = rules.Select(r => r.Id).ToList();
        var rows = await db.FolderRules.Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        foreach (var row in rows)
        {
            var was = rules.First(r => r.Id == row.Id);
            row.State = was.State;
            row.DecidedUtc = was.DecidedUtc;
        }
        await db.SaveChangesAsync(ct);
    }
}
