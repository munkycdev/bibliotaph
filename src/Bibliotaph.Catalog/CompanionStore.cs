using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A companion's link to a book, as stored.</summary>
public sealed record CompanionLinkInfo(EntryId Companion, EntryId Book, CompanionState State, SourceBookEvidence? Evidence, DateTime DecidedUtc)
{
    /// <summary>Whether the book's values reach the companion.</summary>
    public bool InEffect => State is CompanionState.Suggested or CompanionState.Confirmed;
}

/// <summary>A link a rules pass found: the book, whether it is strong enough to stand without asking, and why.</summary>
public sealed record FoundSourceBook(EntryId Companion, EntryId Book, bool Strong, SourceBookEvidence Evidence);

/// <summary>Every link the cards had before a change, so Undo can put them back exactly.</summary>
public sealed record CompanionUndo(IReadOnlyList<EntryId> Companions, IReadOnlyList<CompanionLinkInfo> Before);

/// <summary>What linking cards to a book did: the cards linked, those that can't go with it (and why), and its Undo.</summary>
public sealed record CompanionChange(IReadOnlyList<EntryId> Changed, IReadOnlyList<EntryId> Refused, CompanionUndo Undo);

/// <summary>
/// Which book each companion card goes with (<see cref="CompanionLink"/>). A companion is a file's own card or a pack;
/// the book is a card with files, one owned elsewhere, or an adventure inside a file, but never a companion itself, so
/// a book's values reach its companions in one step. The user's links and refusals are never touched by the rules,
/// which only add, update and take back their own suggestions.
/// </summary>
public sealed class CompanionStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    static CompanionLinkInfo Info(CompanionLink l) =>
        new(new EntryId(l.EntryId), new EntryId(l.BookEntryId), l.State, SourceBookEvidence.FromJson(l.Evidence), l.DecidedUtc);

    static bool Live(CompanionState state) => state is CompanionState.Suggested or CompanionState.Confirmed;

    /// <summary>The link in effect for each of <paramref name="companions"/> that has one, or for every companion when it is null.</summary>
    public async Task<IReadOnlyDictionary<EntryId, CompanionLinkInfo>> GetLiveAsync(IReadOnlyCollection<EntryId>? companions = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = db.CompanionLinks.AsNoTracking().Where(l => l.State == CompanionState.Suggested || l.State == CompanionState.Confirmed);
        if (companions is not null)
        {
            var ids = companions.Select(c => c.Value).Distinct().ToList();
            rows = rows.Where(l => ids.Contains(l.EntryId));
        }
        return (await rows.ToListAsync(ct)).ToDictionary(l => new EntryId(l.EntryId), Info);
    }

    /// <summary>The link in effect for <paramref name="companion"/>, if it has one.</summary>
    public async Task<CompanionLinkInfo?> GetLiveAsync(EntryId companion, CancellationToken ct = default) =>
        (await GetLiveAsync([companion], ct)).GetValueOrDefault(companion);

    /// <summary>Every row for <paramref name="companion"/>: its link, proposals and refusals.</summary>
    public async Task<IReadOnlyList<CompanionLinkInfo>> GetAllAsync(EntryId companion, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.CompanionLinks.AsNoTracking().Where(l => l.EntryId == companion.Value).ToListAsync(ct)).Select(Info)];
    }

    /// <summary>The cards that go with <paramref name="book"/>, by links in effect, oldest link first.</summary>
    public async Task<IReadOnlyList<CompanionLinkInfo>> GetCompanionsAsync(EntryId book, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.CompanionLinks.AsNoTracking()
                .Where(l => l.BookEntryId == book.Value && (l.State == CompanionState.Suggested || l.State == CompanionState.Confirmed))
                .OrderBy(l => l.Id).ToListAsync(ct))
            .Select(Info)];
    }

    /// <summary>The companions of any of <paramref name="books"/>, whose values follow theirs.</summary>
    public async Task<IReadOnlyList<EntryId>> GetCompanionIdsAsync(IReadOnlyCollection<EntryId> books, CancellationToken ct = default)
    {
        if (books.Count == 0) return [];
        var ids = books.Select(b => b.Value).Distinct().ToList();
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.CompanionLinks.AsNoTracking()
                .Where(l => ids.Contains(l.BookEntryId) && (l.State == CompanionState.Suggested || l.State == CompanionState.Confirmed))
                .Select(l => l.EntryId).Distinct().ToListAsync(ct))
            .Select(id => new EntryId(id))];
    }

    /// <summary>The proposals waiting for an answer, each companion's candidates best first.</summary>
    public async Task<IReadOnlyList<CompanionLinkInfo>> GetProposedAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.CompanionLinks.AsNoTracking().Where(l => l.State == CompanionState.Proposed).ToListAsync(ct))
            .Select(Info).OrderBy(l => l.Companion.Value).ThenByDescending(l => l.Evidence?.Score ?? 0)];
    }

    /// <summary>
    /// Why <paramref name="companion"/> can't go with <paramref name="book"/>, or null when it can. A companion is a
    /// whole file's card or a pack, not inside a pack; a book is a whole file, an adventure or one owned elsewhere,
    /// not a companion itself; and neither has joined another card.
    /// </summary>
    static string? Refusal(Entry companion, Entry book, bool companionHasCompanions, bool bookIsCompanion)
    {
        if (companion.Id == book.Id) return "A card can't go with itself.";
        if (companion.MergedIntoEntryId is not null || book.MergedIntoEntryId is not null) return "That card has joined another.";
        if (companion.Kind is not (EntryKind.Whole or EntryKind.Pack) || companion.ParentEntryId is not null)
            return "Only a file's card or a pack can go with a book.";
        if (book.Kind == EntryKind.Pack || (book.Kind == EntryKind.Whole && book.ParentEntryId is not null)) return "A pack can't be the book.";
        if (companionHasCompanions) return "Other cards go with this one, so it can't go with a book itself.";
        if (bookIsCompanion) return "That card goes with a book itself.";
        return null;
    }

    /// <summary>
    /// The user's "Goes with": each of <paramref name="companions"/> goes with <paramref name="book"/>, confirmed, in
    /// place of any link it had; a refusal of that pair is forgotten. Cards that can't go with it are left as they are
    /// and returned as refused.
    /// </summary>
    public async Task<CompanionChange> LinkAsync(IReadOnlyCollection<EntryId> companions, EntryId book, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = companions.Select(c => c.Value).Distinct().ToList();
        var target = await db.Entries.SingleOrDefaultAsync(e => e.Id == book.Value, ct);
        if (target is null) return new CompanionChange([], [.. companions], new CompanionUndo([], []));
        var cards = await db.Entries.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var withCompanions = (await db.CompanionLinks.Where(l => ids.Contains(l.BookEntryId) && (l.State == CompanionState.Suggested || l.State == CompanionState.Confirmed))
            .Select(l => l.BookEntryId).Distinct().ToListAsync(ct)).ToHashSet();
        var bookIsCompanion = await db.CompanionLinks.AnyAsync(l => l.EntryId == book.Value
            && (l.State == CompanionState.Suggested || l.State == CompanionState.Confirmed) && !ids.Contains(l.BookEntryId), ct);

        List<long> linking = [];
        List<EntryId> refused = [];
        foreach (var id in ids)
        {
            if (cards.TryGetValue(id, out var card) && Refusal(card, target, withCompanions.Contains(id), bookIsCompanion) is null) linking.Add(id);
            else refused.Add(new EntryId(id));
        }
        var undo = await SnapshotAsync(db, linking, ct);
        var rows = await db.CompanionLinks.Where(l => linking.Contains(l.EntryId)).ToListAsync(ct);
        var now = Now;
        foreach (var id in linking)
        {
            foreach (var other in rows.Where(r => r.EntryId == id && r.BookEntryId != book.Value && r.State != CompanionState.Rejected))
                db.CompanionLinks.Remove(other);
            // Saved first: the index allows one link in effect per companion at any moment.
            await db.SaveChangesAsync(ct);
            if (rows.FirstOrDefault(r => r.EntryId == id && r.BookEntryId == book.Value) is { } same)
            {
                same.State = CompanionState.Confirmed;
                same.DecidedUtc = now;
            }
            else db.CompanionLinks.Add(new CompanionLink { EntryId = id, BookEntryId = book.Value, State = CompanionState.Confirmed, DecidedUtc = now });
        }
        await db.SaveChangesAsync(ct);
        return new CompanionChange([.. linking.Select(id => new EntryId(id))], refused, undo);
    }

    /// <summary>
    /// Takes away the link in effect from each of <paramref name="companions"/>. With <paramref name="refuse"/> ("Not
    /// from this book"), the pair is remembered as refused so no rule proposes it again, and any proposals for the card
    /// are refused with it; without, a link the user made is simply removed and the rules may suggest one again.
    /// </summary>
    public async Task<CompanionChange> UnlinkAsync(IReadOnlyCollection<EntryId> companions, bool refuse, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = companions.Select(c => c.Value).Distinct().ToList();
        var rows = await db.CompanionLinks.Where(l => ids.Contains(l.EntryId) && l.State != CompanionState.Rejected).ToListAsync(ct);
        var changed = rows.Select(r => r.EntryId).Distinct().ToList();
        var undo = await SnapshotAsync(db, changed, ct);
        var now = Now;
        foreach (var row in rows)
        {
            if (refuse)
            {
                row.State = CompanionState.Rejected;
                row.DecidedUtc = now;
            }
            else if (row.State != CompanionState.Proposed) db.CompanionLinks.Remove(row);
        }
        await db.SaveChangesAsync(ct);
        return new CompanionChange([.. changed.Select(id => new EntryId(id))], [], undo);
    }

    /// <summary>"Keep": the suggested link of each of <paramref name="companions"/> becomes the user's own.</summary>
    public async Task<CompanionChange> ConfirmAsync(IReadOnlyCollection<EntryId> companions, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = companions.Select(c => c.Value).Distinct().ToList();
        var rows = await db.CompanionLinks.Where(l => ids.Contains(l.EntryId) && l.State == CompanionState.Suggested).ToListAsync(ct);
        var undo = await SnapshotAsync(db, [.. rows.Select(r => r.EntryId)], ct);
        foreach (var row in rows)
        {
            row.State = CompanionState.Confirmed;
            row.DecidedUtc = Now;
        }
        await db.SaveChangesAsync(ct);
        return new CompanionChange([.. rows.Select(r => new EntryId(r.EntryId))], [], undo);
    }

    static async Task<CompanionUndo> SnapshotAsync(CatalogDbContext db, IReadOnlyCollection<long> companions, CancellationToken ct)
    {
        var before = await db.CompanionLinks.AsNoTracking().Where(l => companions.Contains(l.EntryId)).ToListAsync(ct);
        return new CompanionUndo([.. companions.Select(id => new EntryId(id))], [.. before.Select(Info)]);
    }

    /// <summary>Puts every row of the cards a change touched back as it was. Returns the cards.</summary>
    public async Task<IReadOnlyList<EntryId>> RestoreAsync(CompanionUndo undo, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = undo.Companions.Select(c => c.Value).ToList();
        var known = (await db.Entries.Where(e => ids.Contains(e.Id) || undo.Before.Select(b => b.Book.Value).Contains(e.Id))
            .Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        db.CompanionLinks.RemoveRange(await db.CompanionLinks.Where(l => ids.Contains(l.EntryId)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        foreach (var row in undo.Before.Where(b => known.Contains(b.Companion.Value) && known.Contains(b.Book.Value)))
            db.CompanionLinks.Add(new CompanionLink
            {
                EntryId = row.Companion.Value,
                BookEntryId = row.Book.Value,
                State = row.State,
                Evidence = row.Evidence?.ToJson(),
                DecidedUtc = row.DecidedUtc,
            });
        await db.SaveChangesAsync(ct);
        return undo.Companions;
    }

    /// <summary>
    /// Answers a proposal: the companion goes with <paramref name="book"/> (one of its candidates or another), whose
    /// link the rules then leave alone; or, when it is null, with none of its candidates, which are refused.
    /// </summary>
    public async Task<CompanionChange> AnswerAsync(EntryId companion, EntryId? book, CancellationToken ct = default) =>
        book is { } chosen ? await LinkAsync([companion], chosen, ct) : await UnlinkAsync([companion], refuse: true, ct);

    /// <summary>
    /// What a rules pass found, for the companions in <paramref name="considered"/>: each one's suggestion (a strong
    /// signal, one book) or proposals (one or more books to ask about) replace those the rules made before. A companion
    /// the user linked, and a pair the user refused, are left alone; a suggestion that stays keeps its row and age.
    /// Returns the companions whose link in effect changed, for their values to follow.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> ReplaceFoundAsync(IReadOnlyCollection<EntryId> considered, IReadOnlyList<FoundSourceBook> found, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var ids = considered.Select(c => c.Value).Distinct().ToList();
        var rows = new List<CompanionLink>();
        foreach (var chunk in ids.Chunk(500))
            rows.AddRange(await db.CompanionLinks.Where(l => chunk.Contains(l.EntryId)).ToListAsync(ct));
        var byCompanion = rows.ToLookup(r => r.EntryId);
        var foundBy = found.ToLookup(f => f.Companion.Value);
        var now = Now;
        List<EntryId> changed = [];
        foreach (var id in ids)
        {
            var mine = byCompanion[id].ToList();
            if (mine.Any(r => r.State == CompanionState.Confirmed)) continue;
            var refused = mine.Where(r => r.State == CompanionState.Rejected).Select(r => r.BookEntryId).ToHashSet();
            var wanted = foundBy[id].Where(f => !refused.Contains(f.Book.Value)).GroupBy(f => f.Book.Value).Select(g => g.First()).ToList();
            // Strong evidence links only when it names one book; otherwise every candidate is asked about.
            var strong = wanted.Count == 1 && wanted[0].Strong;
            var liveBefore = mine.FirstOrDefault(r => r.State == CompanionState.Suggested)?.BookEntryId;
            foreach (var stale in mine.Where(r => r.State is CompanionState.Suggested or CompanionState.Proposed && wanted.All(w => w.Book.Value != r.BookEntryId)))
                db.CompanionLinks.Remove(stale);
            await db.SaveChangesAsync(ct);
            foreach (var want in wanted)
            {
                var state = strong ? CompanionState.Suggested : CompanionState.Proposed;
                var evidence = want.Evidence.ToJson();
                if (mine.FirstOrDefault(r => r.BookEntryId == want.Book.Value) is { } row)
                {
                    if (row.State == state && row.Evidence == evidence) continue;
                    if (row.State != state) row.DecidedUtc = now;
                    (row.State, row.Evidence) = (state, evidence);
                }
                else db.CompanionLinks.Add(new CompanionLink { EntryId = id, BookEntryId = want.Book.Value, State = state, Evidence = evidence, DecidedUtc = now });
            }
            await db.SaveChangesAsync(ct);
            var liveAfter = strong ? wanted[0].Book.Value : (long?)null;
            if (liveBefore != liveAfter) changed.Add(new EntryId(id));
        }
        return changed;
    }

    /// <summary>
    /// Makes this catalog agree with another device about one companion: the book the user linked it to there (null
    /// for none), and the books refused there. Returns whether anything changed.
    /// </summary>
    public async Task<bool> ApplySyncedAsync(EntryId companion, EntryId? book, IReadOnlyCollection<EntryId> refused, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.CompanionLinks.Where(l => l.EntryId == companion.Value).ToListAsync(ct);
        var refusedIds = refused.Select(r => r.Value).ToHashSet();
        var changed = false;
        var now = Now;
        foreach (var row in rows.Where(r => r.State == CompanionState.Rejected && !refusedIds.Contains(r.BookEntryId)))
        {
            db.CompanionLinks.Remove(row);
            changed = true;
        }
        foreach (var id in refusedIds)
        {
            if (rows.FirstOrDefault(r => r.BookEntryId == id) is { } row)
            {
                if (row.State == CompanionState.Rejected) continue;
                row.State = CompanionState.Rejected;
                row.DecidedUtc = now;
            }
            else db.CompanionLinks.Add(new CompanionLink { EntryId = companion.Value, BookEntryId = id, State = CompanionState.Rejected, DecidedUtc = now });
            changed = true;
        }
        if (book is null)
        {
            foreach (var row in rows.Where(r => r.State == CompanionState.Confirmed))
            {
                db.CompanionLinks.Remove(row);
                changed = true;
            }
            await db.SaveChangesAsync(ct);
            return changed;
        }
        if (rows.Any(r => r.BookEntryId == book.Value.Value && r.State == CompanionState.Confirmed))
        {
            await db.SaveChangesAsync(ct);
            return changed;
        }
        foreach (var row in rows.Where(r => Live(r.State) && r.BookEntryId != book.Value.Value)) db.CompanionLinks.Remove(row);
        await db.SaveChangesAsync(ct);
        if (rows.FirstOrDefault(r => r.BookEntryId == book.Value.Value) is { } existing && db.Entry(existing).State != EntityState.Deleted)
        {
            existing.State = CompanionState.Confirmed;
            existing.DecidedUtc = now;
        }
        else db.CompanionLinks.Add(new CompanionLink { EntryId = companion.Value, BookEntryId = book.Value.Value, State = CompanionState.Confirmed, DecidedUtc = now });
        await db.SaveChangesAsync(ct);
        return true;
    }
}
