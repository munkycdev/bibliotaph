using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// A "Is this its file?" card waiting in Needs review: <see cref="DocumentId"/>, on card <see cref="FileEntryId"/>, has
/// the title and publisher of <see cref="EntryId"/>, a book owned elsewhere.
/// </summary>
public sealed record PendingElsewhere(long Id, EntryId EntryId, long DocumentId, EntryId FileEntryId, DateTime CreatedUtc);

/// <summary>An answer to a "Is this its file?" card, with what undoing it needs.</summary>
public sealed record ElsewhereDecision(PendingElsewhere Match, ElsewhereAnswer Answer);

/// <summary>
/// Books owned elsewhere meeting their files (F5 plan, choice 6). Match proposes a new file whose title and publisher
/// match a book owned elsewhere; "Same book" joins the file to it, and "Separate book" is remembered for the pair.
/// </summary>
public sealed class ElsewhereStore(IDbContextFactory<CatalogDbContext> contexts, EntryStore entries, TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Proposes <paramref name="documentId"/> as the file of <paramref name="entryId"/>, a book owned elsewhere.
    /// Returns false when the pair was asked about already, or the entry isn't owned elsewhere any more.
    /// </summary>
    public async Task<bool> ProposeAsync(EntryId entryId, long documentId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.Entries.AnyAsync(e => e.Id == entryId.Value && e.Kind == EntryKind.Elsewhere && e.MergedIntoEntryId == null, ct)) return false;
        if (await db.ElsewhereMatches.AnyAsync(m => m.EntryId == entryId.Value && m.DocumentId == documentId, ct)) return false;
        db.ElsewhereMatches.Add(new ElsewhereMatch { EntryId = entryId.Value, DocumentId = documentId, CreatedUtc = _clock.GetUtcNow().UtcDateTime });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The cards waiting, oldest first. A card whose book has since got a file, or whose file has gone or joined
    /// another card's book, isn't one any more.
    /// </summary>
    public async Task<IReadOnlyList<PendingElsewhere>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.ElsewhereMatches.AsNoTracking()
            .Where(m => m.Answer == null)
            .Join(db.Entries.Where(e => e.Kind == EntryKind.Elsewhere && e.MergedIntoEntryId == null), m => m.EntryId, e => e.Id, (m, _) => m)
            .OrderBy(m => m.CreatedUtc).ThenBy(m => m.Id)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];
        var files = await entries.GetEntriesAsync([.. rows.Select(r => r.DocumentId)], ct);
        return [.. rows.Where(r => files.ContainsKey(r.DocumentId))
            .Select(r => new PendingElsewhere(r.Id, new EntryId(r.EntryId), r.DocumentId, files[r.DocumentId].EntryId, r.CreatedUtc))];
    }

    /// <summary>
    /// Answers a card. Same book joins the file to the book (<see cref="EntryStore.JoinElsewhereAsync"/>); Separate
    /// book remembers the pair. Returns what undoes it, or null when the card isn't waiting any more.
    /// </summary>
    public async Task<ElsewhereDecision?> AnswerAsync(long matchId, ElsewhereAnswer answer, CancellationToken ct = default)
    {
        if ((await GetPendingAsync(ct)).FirstOrDefault(p => p.Id == matchId) is not { } match) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.ElsewhereMatches.SingleAsync(m => m.Id == matchId, ct);
        if (answer == ElsewhereAnswer.SameBook)
        {
            if (await entries.JoinElsewhereAsync(match.EntryId, match.DocumentId, ct) is null) return null;
            db.ElsewhereMatches.Remove(row);
        }
        else row.Answer = ElsewhereAnswer.SeparateBook;
        await db.SaveChangesAsync(ct);
        return new ElsewhereDecision(match, answer);
    }

    /// <summary>Undoes an answer: the file has its own card again, the book is owned elsewhere again, and the card waits again.</summary>
    public async Task UndoAsync(ElsewhereDecision decision, CancellationToken ct = default)
    {
        var match = decision.Match;
        if (decision.Answer == ElsewhereAnswer.SameBook && await entries.UndoJoinElsewhereAsync(match.EntryId, match.DocumentId, ct) is null) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.ElsewhereMatches.SingleOrDefaultAsync(m => m.EntryId == match.EntryId.Value && m.DocumentId == match.DocumentId, ct) is { } row)
            row.Answer = null;
        else
            db.ElsewhereMatches.Add(new ElsewhereMatch { EntryId = match.EntryId.Value, DocumentId = match.DocumentId, CreatedUtc = match.CreatedUtc });
        await db.SaveChangesAsync(ct);
    }
}
