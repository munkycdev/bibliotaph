using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// A proposal waiting in Needs review (slice 5 plan, choice 7): the copy it was read from, the file's card, and the
/// adventures it proposes, in page order.
/// </summary>
public sealed record PendingPartProposal(long Id, string ContentHash, long DocumentId, EntryId EntryId, PartProposalSource Source, IReadOnlyList<ProposedPart> Parts);

/// <summary>What a proposal's answer was before the user answered, for Undo.</summary>
public sealed record PartProposalState(string ContentHash, bool Existed, PartProposalAnswer? Answer);

/// <summary>
/// The adventures files seem to hold (slice 5 plan, choices 5 to 7), by the content hash of the copy they were read
/// from. A proposal never makes cards by itself: it is one Needs review card for its file until the user accepts it
/// or says the file is "Not split", and that answer is remembered for every copy of the file, by hash, so the file
/// isn't proposed again whatever reads it later.
/// </summary>
public sealed class PartProposalStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Records what a source proposes for the copy with <paramref name="contentHash"/>, replacing what it proposed
    /// before, or drops its waiting proposal when it now proposes nothing. A copy already answered is left alone, and
    /// so is a waiting proposal from the rules when a model proposes: the rules' come from the file's own bookmarks.
    /// Returns whether anything changed.
    /// </summary>
    public async Task<bool> SaveAsync(string contentHash, PartProposalSource source, IReadOnlyList<ProposedPart> parts, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.PartProposals.SingleOrDefaultAsync(p => p.ContentHash == contentHash, ct);
        if (row is { Answer: not null }) return false;
        if (parts.Count == 0)
        {
            if (row is null || row.Source != source) return false;
            db.PartProposals.Remove(row);
            await db.SaveChangesAsync(ct);
            return true;
        }
        var json = Serialize(parts);
        if (row is null)
        {
            db.PartProposals.Add(new PartProposal { ContentHash = contentHash, Source = source, PartsJson = json, CreatedUtc = _clock.GetUtcNow().UtcDateTime });
        }
        else
        {
            if (row.Source == PartProposalSource.Rules && source != PartProposalSource.Rules) return false;
            if (row.Source == source && row.PartsJson == json) return false;
            row.Source = source;
            row.PartsJson = json;
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Whether the copy with <paramref name="contentHash"/>'s file has had its proposal answered.</summary>
    public async Task<bool> IsAnsweredAsync(string contentHash, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.PartProposals.AnyAsync(p => p.ContentHash == contentHash && p.Answer != null, ct);
    }

    /// <summary>
    /// The proposals waiting for an answer, one per file: the one read from the copy the file opens when it has one.
    /// A file that already has adventures marked, or whose proposal was answered for another of its copies, isn't
    /// asked about.
    /// </summary>
    public async Task<IReadOnlyList<PendingPartProposal>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await (
                from p in db.PartProposals.AsNoTracking()
                where p.Answer == null
                join d in db.Documents on p.ContentHash equals d.ContentHash
                join s in db.EntrySources on d.Id equals s.DocumentId
                where s.FirstPdfPage == null && s.Entry.Kind == EntryKind.Whole && s.Entry.ParentEntryId == null && s.Entry.MergedIntoEntryId == null
                    && !db.Entries.Any(part => part.ParentEntryId == s.EntryId && part.Kind == EntryKind.Part)
                select new { p.Id, p.ContentHash, DocumentId = d.Id, s.EntryId, s.IsCurrent, p.Source, p.PartsJson })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];
        var entryIds = rows.Select(r => r.EntryId).Distinct().ToList();
        var answered = await (
                from s in db.EntrySources
                where entryIds.Contains(s.EntryId) && s.FirstPdfPage == null
                join p in db.PartProposals on s.Document.ContentHash equals p.ContentHash
                where p.Answer != null
                select s.EntryId)
            .Distinct().ToListAsync(ct);
        return [.. rows.Where(r => !answered.Contains(r.EntryId))
            .GroupBy(r => r.EntryId)
            .Select(g => g.OrderByDescending(r => r.IsCurrent).ThenBy(r => r.Id).First())
            .OrderBy(r => r.Id)
            .Select(r => new PendingPartProposal(r.Id, r.ContentHash, r.DocumentId, new EntryId(r.EntryId), r.Source, Deserialize(r.PartsJson)))];
    }

    /// <summary>
    /// Answers the proposal for every copy of the file <paramref name="fileId"/>, by hash, so none of them is proposed
    /// again. Returns what each was before, for Undo.
    /// </summary>
    public async Task<IReadOnlyList<PartProposalState>> AnswerAsync(EntryId fileId, PartProposalAnswer answer, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var hashes = await db.EntrySources.Where(s => s.EntryId == fileId.Value && s.FirstPdfPage == null).Select(s => s.Document.ContentHash).Distinct().ToListAsync(ct);
        var rows = await db.PartProposals.Where(p => hashes.Contains(p.ContentHash)).ToListAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var before = new List<PartProposalState>(hashes.Count);
        foreach (var hash in hashes)
        {
            var row = rows.SingleOrDefault(r => r.ContentHash == hash);
            before.Add(new PartProposalState(hash, row is not null, row?.Answer));
            if (row is null)
            {
                row = new PartProposal { ContentHash = hash, Source = PartProposalSource.Rules, PartsJson = "[]", CreatedUtc = now };
                db.PartProposals.Add(row);
            }
            row.Answer = answer;
            row.DecidedUtc = now;
        }
        await db.SaveChangesAsync(ct);
        return before;
    }

    /// <summary>Undoes <see cref="AnswerAsync"/>: each copy's proposal is as it was, waiting again or gone.</summary>
    public async Task RestoreAsync(IReadOnlyList<PartProposalState> states, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var hashes = states.Select(s => s.ContentHash).ToList();
        var rows = await db.PartProposals.Where(p => hashes.Contains(p.ContentHash)).ToListAsync(ct);
        foreach (var state in states)
        {
            if (rows.SingleOrDefault(r => r.ContentHash == state.ContentHash) is not { } row) continue;
            if (!state.Existed) db.PartProposals.Remove(row);
            else
            {
                row.Answer = state.Answer;
                row.DecidedUtc = state.Answer is null ? null : row.DecidedUtc;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    sealed record StoredPart(string Title, int First, int Last, string? Levels, string? Quote);

    static string Serialize(IReadOnlyList<ProposedPart> parts) =>
        JsonSerializer.Serialize(parts.Select(p => new StoredPart(p.Title, p.Pages.First, p.Pages.Last, p.Levels, p.Quote)), Json);

    static List<ProposedPart> Deserialize(string json) =>
        [.. (JsonSerializer.Deserialize<List<StoredPart>>(json, Json) ?? []).Select(p => new ProposedPart(p.Title, new PartPages(p.First, p.Last), p.Levels, p.Quote))];
}
