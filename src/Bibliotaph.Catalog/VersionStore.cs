using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// A "new version?" card waiting in Needs review: <see cref="DocumentId"/> looks like a new version of the book whose
/// card is <see cref="MatchedEntryId"/>, by <see cref="Evidence"/>.
/// </summary>
public sealed record PendingVersion(
    long Id,
    long DocumentId,
    EntryId EntryId,
    long MatchedDocumentId,
    EntryId MatchedEntryId,
    VersionEvidence Evidence,
    int SharedPages,
    int ComparedPages,
    int? PageCount,
    int? MatchedPageCount,
    DateTime CreatedUtc);

/// <summary>
/// An answer to a "new version?" card, with what undoing it needs: the card it answered, and which copy the book's
/// card opened before.
/// </summary>
public sealed record VersionDecision(PendingVersion Version, VersionAnswer Answer, long? PreviousCurrent);

/// <summary>
/// The "new version?" cards (F2 plan, choice 4): Match proposes a file that shares most of its pages with a book, or
/// has its title and publisher, as a new version of it; the user makes it current, keeps it as another copy, or says
/// it is a separate book, which is remembered for that pair of files. Each file gets one card, for its best match.
/// </summary>
public sealed class VersionStore(IDbContextFactory<CatalogDbContext> contexts, EntryStore entries, TimeProvider? clock = null)
{
    /// <summary>A file is proposed when at least this share of the smaller file's fingerprinted pages are in the other.</summary>
    public const double MinimumSharedPages = 0.6;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Proposes the newer of two documents as a new version of the other's book. Nothing is proposed for files already
    /// on one card, or that the user said are different books; and a file keeps the card for its best match: shared
    /// pages over title and publisher, then the larger share. Returns whether the card is new or changed.
    /// </summary>
    public async Task<bool> ProposeAsync(long documentId, long otherDocumentId, VersionEvidence evidence, int sharedPages = 0, int comparedPages = 0,
        CancellationToken ct = default)
    {
        var (newer, older) = documentId > otherDocumentId ? (documentId, otherDocumentId) : (otherDocumentId, documentId);
        var pair = await entries.GetEntriesAsync([newer, older], ct);
        if (!pair.TryGetValue(newer, out var mine) || !pair.TryGetValue(older, out var theirs) || mine.EntryId == theirs.EntryId) return false;
        if (await entries.IsNotSameBookAsync(mine.ContentHash, theirs.ContentHash, ct)) return false;

        await using var db = await contexts.CreateDbContextAsync(ct);
        var proposal = new VersionProposal
        {
            DocumentId = newer,
            MatchedDocumentId = older,
            Evidence = evidence,
            SharedPages = sharedPages,
            ComparedPages = comparedPages,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        };
        if (await db.VersionProposals.SingleOrDefaultAsync(p => p.DocumentId == newer, ct) is { } existing)
        {
            if (existing.MatchedDocumentId != older && Score(existing).CompareTo(Score(proposal)) >= 0) return false;
            if (existing.MatchedDocumentId == older && Score(existing).Equals(Score(proposal))) return false;
            existing.MatchedDocumentId = older;
            existing.Evidence = evidence;
            existing.SharedPages = sharedPages;
            existing.ComparedPages = comparedPages;
        }
        else db.VersionProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        return true;
    }

    static (bool, double, int) Score(VersionProposal p) =>
        (p.Evidence == VersionEvidence.SharedPages, p.ComparedPages == 0 ? 0 : (double)p.SharedPages / p.ComparedPages, p.SharedPages);

    /// <summary>
    /// The cards waiting, oldest first. A card whose files have since joined one card, or that the user said are
    /// different books, isn't one any more.
    /// </summary>
    public async Task<IReadOnlyList<PendingVersion>> GetPendingAsync(CancellationToken ct = default)
    {
        List<VersionProposal> proposals;
        await using (var db = await contexts.CreateDbContextAsync(ct))
            proposals = await db.VersionProposals.AsNoTracking().OrderBy(p => p.CreatedUtc).ThenBy(p => p.Id).ToListAsync(ct);
        if (proposals.Count == 0) return [];
        var documents = await entries.GetEntriesAsync([.. proposals.SelectMany(p => new[] { p.DocumentId, p.MatchedDocumentId })], ct);
        var pages = await PageCountsAsync([.. documents.Keys], ct);
        var pending = new List<PendingVersion>();
        foreach (var p in proposals)
        {
            if (!documents.TryGetValue(p.DocumentId, out var mine) || !documents.TryGetValue(p.MatchedDocumentId, out var theirs)) continue;
            if (mine.EntryId == theirs.EntryId || await entries.IsNotSameBookAsync(mine.ContentHash, theirs.ContentHash, ct)) continue;
            pending.Add(new PendingVersion(p.Id, p.DocumentId, mine.EntryId, p.MatchedDocumentId, theirs.EntryId, p.Evidence, p.SharedPages, p.ComparedPages,
                pages.GetValueOrDefault(p.DocumentId), pages.GetValueOrDefault(p.MatchedDocumentId), p.CreatedUtc));
        }
        return pending;
    }

    async Task<Dictionary<long, int?>> PageCountsAsync(List<long> documentIds, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.PageCount, ct);
    }

    /// <summary>
    /// Answers a card. Make it current and Keep as another copy join the file's card to the book's, as Match joins a
    /// copy, and Make it current then opens it; Separate book remembers the pair. Returns what undoes it, or null when
    /// the card isn't waiting any more.
    /// </summary>
    public async Task<VersionDecision?> AnswerAsync(long proposalId, VersionAnswer answer, CancellationToken ct = default)
    {
        if ((await GetPendingAsync(ct)).FirstOrDefault(p => p.Id == proposalId) is not { } version)
        {
            await DeleteAsync(proposalId, ct);
            return null;
        }
        long? previous = null;
        if (answer == VersionAnswer.SeparateBook)
        {
            var (mine, theirs) = await HashesAsync(version, ct);
            await entries.RememberNotSameBookAsync(mine, theirs, ct);
        }
        else
        {
            previous = (await entries.GetCopiesAsync(version.MatchedEntryId, ct)).FirstOrDefault(c => c.IsCurrent)?.DocumentId;
            if (await entries.JoinAsCopyAsync(version.DocumentId, version.MatchedDocumentId, ct) is null) return null;
            if (answer == VersionAnswer.MakeCurrent) await entries.MakeCurrentAsync(version.MatchedEntryId, version.DocumentId, ct);
        }
        await DeleteAsync(proposalId, ct);
        return new VersionDecision(version, answer, previous);
    }

    /// <summary>Undoes an answer: the file has its own card again, the book opens what it opened, and the card waits again.</summary>
    public async Task UndoAsync(VersionDecision decision, CancellationToken ct = default)
    {
        var version = decision.Version;
        if (decision.Answer == VersionAnswer.SeparateBook)
        {
            var (mine, theirs) = await HashesAsync(version, ct);
            await entries.ForgetNotSameBookAsync(mine, theirs, ct);
        }
        else
        {
            await entries.SplitCopyAsync(version.MatchedEntryId, version.DocumentId, remember: false, ct);
            if (decision.PreviousCurrent is { } previous) await entries.MakeCurrentAsync(version.MatchedEntryId, previous, ct);
        }
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.VersionProposals.AnyAsync(p => p.DocumentId == version.DocumentId, ct)) return;
        db.VersionProposals.Add(new VersionProposal
        {
            DocumentId = version.DocumentId,
            MatchedDocumentId = version.MatchedDocumentId,
            Evidence = version.Evidence,
            SharedPages = version.SharedPages,
            ComparedPages = version.ComparedPages,
            CreatedUtc = version.CreatedUtc,
        });
        await db.SaveChangesAsync(ct);
    }

    async Task<(string, string)> HashesAsync(PendingVersion version, CancellationToken ct)
    {
        var documents = await entries.GetEntriesAsync([version.DocumentId, version.MatchedDocumentId], ct);
        return (documents[version.DocumentId].ContentHash, documents[version.MatchedDocumentId].ContentHash);
    }

    async Task DeleteAsync(long proposalId, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.VersionProposals.Where(p => p.Id == proposalId).ExecuteDeleteAsync(ct);
    }
}
