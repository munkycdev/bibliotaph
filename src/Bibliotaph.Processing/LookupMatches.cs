using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>A "Same book?" card for Needs review: the record, the book's card title, and where its file is.</summary>
public sealed record LookupMatchItem(PendingLookup Match, string BookTitle, string? Path);

/// <summary>
/// Needs review's "Same book?" cards: a catalogue's record for the ISBN a book prints, whose title isn't printed in the
/// book. Same book fills its fields from the record at once, as the catalogue's suggestions, which rank above the AI's
/// and below the user's own values; Not this book is remembered, so the record is never used for the book.
/// </summary>
public sealed class LookupMatches(LookupRecordStore records, MetadataProjector projector, IndexQueries queries, LibraryStore library)
{
    public async Task<int> CountPendingAsync(CancellationToken ct = default) => (await records.GetPendingAsync(ct)).Count;

    /// <summary>The cards waiting, oldest first.</summary>
    public async Task<IReadOnlyList<LookupMatchItem>> GetPendingAsync(CancellationToken ct = default)
    {
        var pending = await records.GetPendingAsync(ct);
        if (pending.Count == 0) return [];
        var titles = await queries.GetEntryTitlesAsync([.. pending.Select(p => p.EntryId).Distinct()], ct);
        var items = new List<LookupMatchItem>(pending.Count);
        foreach (var match in pending)
        {
            var path = (await library.GetLocationsAsync(match.DocumentId, ct)).FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
            items.Add(new LookupMatchItem(match, titles.GetValueOrDefault(match.EntryId) ?? System.IO.Path.GetFileNameWithoutExtension(path) ?? "Untitled", path));
        }
        return items;
    }

    /// <summary>Answers a card. Returns what undoes it, or null when it isn't waiting any more.</summary>
    public async Task<LookupDecision?> AnswerAsync(PendingLookup match, LookupAnswer answer, CancellationToken ct = default)
    {
        var decision = await records.AnswerAsync(match, answer, ct);
        if (decision is not null) await ApplyAsync(decision.Match, ct);
        return decision;
    }

    public async Task UndoAsync(LookupDecision decision, CancellationToken ct = default)
    {
        await records.UndoAsync(decision, ct);
        await ApplyAsync(decision.Match, ct);
    }

    /// <summary>The book's suggestions from its files' records as they are now; projecting it also tells Needs review its count changed.</summary>
    async Task ApplyAsync(PendingLookup match, CancellationToken ct)
    {
        var grew = false;
        foreach (var hash in match.Hashes) grew |= (await records.ApplyAsync(match.EntryId, hash, ct)).VocabularyChanged;
        if (grew) await projector.ProjectVocabularyAsync(ct);
        await projector.ProjectAsync([match.EntryId], ct);
    }
}
