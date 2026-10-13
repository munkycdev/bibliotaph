using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// "This file looks like it holds 5 adventures": the file's card, the copy the proposal was read from, and the
/// adventures proposed, in page order.
/// </summary>
public sealed record PartProposalItem(EntryId FileEntryId, string FileTitle, long DocumentId, PartProposalSource Source, IReadOnlyList<ProposedPart> Parts);

/// <summary>What Accept did: the adventures it made and what undoes them, or why it couldn't, in words for the user.</summary>
public sealed record ProposalAccepted(IReadOnlyList<EntryId> Parts, string? Problem = null, Func<Task>? Undo = null);

/// <summary>
/// Proposes the adventures inside compilation files and settles the proposals in Needs review. The rules read a
/// file's bookmarks and the blurbs on their first pages as Hints from names reads its names; a proposal never makes
/// cards by itself. Accept makes a card for each ticked adventure, with the levels its blurb gave as a suggestion;
/// "Not split" says the file is one book. Either answer is remembered for every copy of the file, by content hash,
/// and can be undone.
/// </summary>
public sealed class PartProposalService(PartProposalStore proposals, PartsService parts, EntryStore entries, IndexQueries queries,
    ILogger<PartProposalService>? log = null)
{
    readonly ILogger _log = log ?? NullLogger<PartProposalService>.Instance;

    /// <summary>
    /// Reads the rules' proposal for a document from its bookmarks and page text, as its Hints from names stage runs.
    /// Returns whether its proposal changed.
    /// </summary>
    public async Task<bool> ProposeAsync(long documentId, CancellationToken ct = default)
    {
        if (await entries.GetEntryAsync(documentId, ct) is not { } entry || await entries.GetKindAsync(entry.EntryId, ct) != EntryKind.Whole) return false;
        var outline = await queries.GetOutlineAsync(documentId, ct);
        var pages = await queries.GetAllPageTextsAsync(documentId, ct);
        var proposed = PartRules.Propose([.. outline.Select(o => (o.Title, o.PdfPage, o.Depth))], pages);
        var changed = await proposals.SaveAsync(entry.ContentHash, PartProposalSource.Rules, proposed, ct);
        if (changed && proposed.Count > 0) _log.LogInformation("Document {DocumentId} looks like it holds {Count} adventures", documentId, proposed.Count);
        return changed;
    }

    /// <summary>
    /// Whether a model should be asked which of a document's bookmarks are adventures: it is a copy of a book's own
    /// file with no adventures marked, nothing was proposed from this copy yet, and no copy's proposal was answered.
    /// The rules' proposal comes first, from the file's own words, and a model never replaces it.
    /// </summary>
    public async Task<bool> WantsModelAsync(long documentId, CancellationToken ct = default)
    {
        if (await entries.GetEntryAsync(documentId, ct) is not { } entry || await entries.GetKindAsync(entry.EntryId, ct) != EntryKind.Whole) return false;
        if ((await parts.GetAsync(entry.EntryId, ct)).Count > 0) return false;
        return !await proposals.IsSettledAsync(entry.EntryId, entry.ContentHash, ct);
    }

    /// <summary>Records the adventures a model found in a document, checked against its pages, as a proposal for Needs review.</summary>
    public async Task<bool> ProposeFromModelAsync(long documentId, IReadOnlyList<ProposedPart> found, CancellationToken ct = default)
    {
        if (found.Count == 0 || await entries.GetEntryAsync(documentId, ct) is not { } entry) return false;
        var changed = await proposals.SaveAsync(entry.ContentHash, PartProposalSource.Ai, found, ct);
        if (changed) _log.LogInformation("AI found {Count} adventures in document {DocumentId}", found.Count, documentId);
        return changed;
    }

    /// <summary>The proposals waiting in Needs review, one card per file.</summary>
    public async Task<IReadOnlyList<PartProposalItem>> GetPendingAsync(CancellationToken ct = default)
    {
        var pending = await proposals.GetPendingAsync(ct);
        if (pending.Count == 0) return [];
        var titles = await queries.GetEntryTitlesAsync([.. pending.Select(p => p.EntryId)], ct);
        return [.. pending.Select(p => new PartProposalItem(p.EntryId, titles.GetValueOrDefault(p.EntryId) ?? "Untitled", p.DocumentId, p.Source, p.Parts))];
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default) => (await proposals.GetPendingAsync(ct)).Count;

    /// <summary>
    /// Accept, with the ticked adventures as the user left them (titles and pages edited, perhaps): each gets a card of
    /// its own, with the levels its blurb gave as a suggestion. When one can't, those made so far are taken back and
    /// the card stays, saying why.
    /// </summary>
    public async Task<ProposalAccepted> AcceptAsync(PartProposalItem item, IReadOnlyList<ProposedPart> chosen, CancellationToken ct = default)
    {
        if (chosen.Count == 0) return new ProposalAccepted([], "Tick at least one adventure, or choose Not split.");
        var origin = item.Source == PartProposalSource.Ai ? AssertionOrigin.Ai : AssertionOrigin.Rule;
        var added = new List<EntryId>();
        foreach (var part in chosen.OrderBy(p => p.Pages.First))
        {
            IReadOnlyList<MetadataProposal> suggestions = part.Levels is { } levels && LevelRange.TryParse(levels, out var range)
                ? [new MetadataProposal(MetadataFields.Levels, range.ToString(), origin, part.Quote, [part.Pages.First])]
                : [];
            var made = await parts.AddAsync(item.DocumentId, part.Pages, part.Title, suggestions, ct);
            if (made.EntryId is { } partId)
            {
                added.Add(partId);
                continue;
            }
            foreach (var undone in added) await parts.RemoveAsync(undone, ct);
            return new ProposalAccepted([], $"{part.Title}: {made.Problem}");
        }
        var before = await proposals.AnswerAsync(item.FileEntryId, PartProposalAnswer.Accepted, ct);
        _log.LogInformation("Accepted {Count} adventures in entry {FileId}", added.Count, item.FileEntryId);
        return new ProposalAccepted(added, Undo: async () =>
        {
            foreach (var partId in added) await parts.RemoveAsync(partId, CancellationToken.None);
            await proposals.RestoreAsync(before, CancellationToken.None);
        });
    }

    /// <summary>"Not split": the file is one book, and none of its copies is proposed again. Returns what undoes it.</summary>
    public async Task<Func<Task>> NotSplitAsync(PartProposalItem item, CancellationToken ct = default)
    {
        var before = await proposals.AnswerAsync(item.FileEntryId, PartProposalAnswer.NotSplit, ct);
        return () => proposals.RestoreAsync(before, CancellationToken.None);
    }
}
