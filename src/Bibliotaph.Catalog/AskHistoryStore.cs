using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// A question asked of a book and its answer, as kept: the question as typed and as rewritten to stand on its own, the
/// answer as shown and its checked pieces (JSON, read by Processing), the PDF pages it cites, and the model.
/// </summary>
public sealed record AskTurnInfo(long Id, DateTime AskedUtc, string Question, string? Rewritten, string Answer, string Parts,
    IReadOnlyList<int> CitedPages, string Model, bool NotAnswered);

/// <summary>
/// Ask this book's conversations: one per book, kept on its entry in catalog.db, so a rebuilt index keeps them. Only
/// the last <see cref="MaxTurns"/> turns of a book are kept.
/// </summary>
public sealed class AskHistoryStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>A book's conversation keeps this many turns; older ones go as new ones come.</summary>
    public const int MaxTurns = 50;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The entry's conversation, oldest first.</summary>
    public async Task<IReadOnlyList<AskTurnInfo>> GetAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var turns = await db.AskTurns.AsNoTracking().Where(t => t.EntryId == entryId.Value).OrderBy(t => t.Id).ToListAsync(ct);
        return [.. turns.Select(t => new AskTurnInfo(t.Id, t.AskedUtc, t.Question, t.Rewritten, t.Answer, t.Parts,
            JsonSerializer.Deserialize<int[]>(t.CitedPages) ?? [], t.Model, t.NotAnswered))];
    }

    /// <summary>Adds a turn to the entry's conversation, dropping the oldest beyond <see cref="MaxTurns"/>, and returns it as kept.</summary>
    public async Task<AskTurnInfo> AddAsync(EntryId entryId, string question, string? rewritten, string answer, string parts, IReadOnlyList<int> citedPages,
        string model, bool notAnswered, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var turn = new AskTurn
        {
            EntryId = entryId.Value,
            AskedUtc = _clock.GetUtcNow().UtcDateTime,
            Question = question,
            Rewritten = rewritten,
            Answer = answer,
            Parts = parts,
            CitedPages = JsonSerializer.Serialize(citedPages),
            Model = model,
            NotAnswered = notAnswered,
        };
        db.AskTurns.Add(turn);
        await db.SaveChangesAsync(ct);
        var older = await db.AskTurns.Where(t => t.EntryId == entryId.Value).OrderByDescending(t => t.Id).Skip(MaxTurns).Select(t => t.Id).ToListAsync(ct);
        if (older.Count > 0) await db.AskTurns.Where(t => older.Contains(t.Id)).ExecuteDeleteAsync(ct);
        return new AskTurnInfo(turn.Id, turn.AskedUtc, question, rewritten, answer, parts, citedPages, model, notAnswered);
    }

    /// <summary>Starts the entry's conversation again, or forgets it with its text. Returns how many turns went.</summary>
    public async Task<int> ClearAsync(EntryId entryId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.AskTurns.Where(t => t.EntryId == entryId.Value).ExecuteDeleteAsync(ct);
    }
}
