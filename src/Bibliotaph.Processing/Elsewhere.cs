using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What the "Add a book I own elsewhere" dialog collects (F5 plan, choice 1): a title, and optionally a game system,
/// types, a publisher and where it is owned. Each is typed as the inspector takes it; multi-value fields are lists.
/// </summary>
public sealed record ElsewhereDraft(string Title, string? System = null, IReadOnlyList<string>? Types = null, string? Publisher = null,
    IReadOnlyList<string>? AlsoOwn = null);

/// <summary>
/// A "Is this its file?" card for Needs review: the book owned elsewhere and where it is owned, and the file that may be
/// its, with its card's title and where it is.
/// </summary>
public sealed record ElsewhereItem(PendingElsewhere Match, string BookTitle, string? AlsoOwn, string FileTitle, string? Path);

/// <summary>
/// Books the user owns elsewhere, in print or on a VTT (F5 plan, choices 1 to 6): adding one, removing it again, and
/// the Needs review cards that offer a new file as its. Their metadata are the user's own values, set through
/// <see cref="MetadataService"/> as the inspector sets them, so bulk edit, search and Undo treat them as any card's.
/// </summary>
public sealed class ElsewhereService(EntryStore entries, ElsewhereStore matches, MetadataService metadata, VocabularyStore vocabularies,
    MetadataProjector projector, LibraryStore library, IndexQueries queries, LibraryQueries cards)
{
    /// <summary>The terms the dialog offers: game systems, types and places to own a book.</summary>
    public Task<Vocabulary> GetVocabularyAsync(CancellationToken ct = default) => vocabularies.GetAsync(ct);

    /// <summary>
    /// Makes the card. Every value is checked first, so nothing is made when one can't be stored: the problem comes
    /// back instead.
    /// </summary>
    public async Task<(EntryId? EntryId, MetadataProblem? Problem)> AddAsync(ElsewhereDraft draft, CancellationToken ct = default)
    {
        var fields = Fields(draft, await vocabularies.GetAsync(ct)).ToList();
        if (string.IsNullOrWhiteSpace(draft.Title)) return (null, new MetadataProblem(MetadataFields.Title, "A book needs a title."));
        foreach (var (field, typed) in fields)
            foreach (var part in MetadataValues.Split(field, typed))
                if (MetadataService.Check(field, part) is { } problem) return (null, problem);

        var entryId = await entries.AddElsewhereAsync(ct);
        foreach (var (field, typed) in fields)
            if (await metadata.SetAsync(entryId, field, typed, ct) is { } problem) return (entryId, problem);
        return (entryId, null);
    }

    /// <summary>
    /// The fields the draft fills. The one game system box takes an edition too: "5e" names D&amp;D's 5th edition, which
    /// implies its system, rather than a new system called 5e.
    /// </summary>
    static IEnumerable<(MetadataField Field, string Typed)> Fields(ElsewhereDraft draft, Vocabulary vocabulary)
    {
        yield return (MetadataFields.Title, draft.Title);
        if (!string.IsNullOrWhiteSpace(draft.System))
            yield return (vocabulary.Resolve("system", draft.System) is null && vocabulary.Resolve("edition", draft.System) is not null
                ? MetadataFields.Edition : MetadataFields.System, draft.System);
        if (draft.Types is { Count: > 0 } types) yield return (MetadataFields.Types, string.Join(", ", types));
        if (!string.IsNullOrWhiteSpace(draft.Publisher)) yield return (MetadataFields.Publisher, draft.Publisher);
        if (draft.AlsoOwn is { Count: > 0 } owns) yield return (MetadataFields.AlsoOwn, string.Join(", ", owns));
    }

    /// <summary>"Remove from library" (choice 5). Returns what Undo needs, or null for a card that isn't owned elsewhere.</summary>
    public async Task<RemovedEntry?> RemoveAsync(EntryId entryId, CancellationToken ct = default)
    {
        var removed = await entries.RemoveElsewhereAsync(entryId, ct);
        if (removed is not null) await projector.ProjectAsync([entryId], ct);
        return removed;
    }

    /// <summary>Undoes <see cref="RemoveAsync"/>: the book is back, with everything set on it, on a new card.</summary>
    public async Task<EntryId> UndoRemoveAsync(RemovedEntry removed, CancellationToken ct = default)
    {
        var entryId = await entries.RestoreElsewhereAsync(removed, ct);
        await projector.ProjectAsync([entryId], ct);
        return entryId;
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default) => (await matches.GetPendingAsync(ct)).Count;

    /// <summary>The "Is this its file?" cards waiting, oldest first.</summary>
    public async Task<IReadOnlyList<ElsewhereItem>> GetPendingAsync(CancellationToken ct = default)
    {
        var pending = await matches.GetPendingAsync(ct);
        if (pending.Count == 0) return [];
        var titles = await queries.GetEntryTitlesAsync([.. pending.SelectMany(p => new[] { p.EntryId, p.FileEntryId })], ct);
        var books = (await cards.GetEntriesAsync([.. pending.Select(p => p.EntryId)], ct)).ToDictionary(e => e.EntryId);
        var items = new List<ElsewhereItem>(pending.Count);
        foreach (var match in pending)
        {
            var path = (await library.GetLocationsAsync(match.DocumentId, ct)).FirstOrDefault(l => l.State != FileLocationState.Missing)?.FullPath;
            items.Add(new ElsewhereItem(match, titles.GetValueOrDefault(match.EntryId) ?? "Untitled", books.GetValueOrDefault(match.EntryId)?.AlsoOwn,
                titles.GetValueOrDefault(match.FileEntryId) ?? System.IO.Path.GetFileNameWithoutExtension(path) ?? "Untitled", path));
        }
        return items;
    }

    /// <summary>Answers a card (choice 6). Returns what undoes it, or null when it isn't waiting any more.</summary>
    public async Task<ElsewhereDecision?> AnswerAsync(PendingElsewhere match, ElsewhereAnswer answer, CancellationToken ct = default)
    {
        var decision = await matches.AnswerAsync(match.Id, answer, ct);
        if (decision is not null && answer == ElsewhereAnswer.SameBook) await projector.ProjectAsync([match.EntryId, match.FileEntryId], ct);
        return decision;
    }

    public async Task UndoAsync(ElsewhereDecision decision, CancellationToken ct = default)
    {
        await matches.UndoAsync(decision, ct);
        if (decision.Answer == ElsewhereAnswer.SameBook) await projector.ProjectAsync([decision.Match.EntryId, decision.Match.FileEntryId], ct);
    }
}
