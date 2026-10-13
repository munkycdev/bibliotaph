using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// What the "Add a book I own elsewhere" dialog collects: a title, and optionally a game system, types, a publisher
/// and where it is owned. Each is typed as the inspector takes it; multi-value fields are lists.
/// </summary>
public sealed record ElsewhereDraft(string Title, string? System = null, IReadOnlyList<string>? Types = null, string? Publisher = null,
    IReadOnlyList<string>? AlsoOwn = null);

/// <summary>
/// A "Is this its file?" card for Needs review: the book owned elsewhere and where it is owned, and the file that may be
/// its, with its card's title and where it is.
/// </summary>
public sealed record ElsewhereItem(PendingElsewhere Match, string BookTitle, string? AlsoOwn, string FileTitle, string? Path);

/// <summary>
/// Books the user owns elsewhere, in print or on a VTT: adding one, removing it again, and the Needs review cards
/// that offer a file as its. Their metadata are the user's own values, set through <see cref="MetadataService"/> as
/// the inspector sets them, so bulk edit, search and Undo treat them as any card's. Match asks about a file when it
/// is new or read again; a book added, or a title or publisher changed on either side, has the files already in the
/// library checked here by the same rule.
/// </summary>
public sealed class ElsewhereService(EntryStore entries, ElsewhereStore matches, MetadataService metadata, VocabularyStore vocabularies,
    MetadataProjector projector, LibraryStore library, IndexQueries queries, LibraryQueries cards, ILogger<ElsewhereService>? log = null)
    : IDisposable
{
    readonly ILogger _log = log ?? NullLogger<ElsewhereService>.Instance;

    /// <summary>
    /// One check at a time, and none while a book is being added: a check run after its title is set, before its
    /// publisher is, could ask about a file whose publisher says otherwise.
    /// </summary>
    readonly SemaphoreSlim _checking = new(1, 1);

    readonly Lock _queueLock = new();

    /// <summary>Entries projected since the queued check took its own: null while no check is queued or running.</summary>
    HashSet<EntryId>? _queued;

    public void Dispose() => _checking.Dispose();

    /// <summary>The terms the dialog offers: game systems, types and places to own a book.</summary>
    public Task<Vocabulary> GetVocabularyAsync(CancellationToken ct = default) => vocabularies.GetAsync(ct);

    /// <summary>
    /// Makes the card. Every value is checked first, so nothing is made when one can't be stored: the problem comes
    /// back instead. The files already in the library that could be the book's are then offered in Needs review.
    /// </summary>
    public async Task<(EntryId? EntryId, MetadataProblem? Problem)> AddAsync(ElsewhereDraft draft, CancellationToken ct = default)
    {
        var fields = Fields(draft, await vocabularies.GetAsync(ct)).ToList();
        if (string.IsNullOrWhiteSpace(draft.Title)) return (null, new MetadataProblem(MetadataFields.Title, "A book needs a title."));
        foreach (var (field, typed) in fields)
            foreach (var part in MetadataValues.Split(field, typed))
                if (MetadataService.Check(field, part) is { } problem) return (null, problem);

        await _checking.WaitAsync(ct);
        try
        {
            var entryId = await entries.AddElsewhereAsync(ct);
            foreach (var (field, typed) in fields)
                if (await metadata.SetAsync(entryId, field, typed, ct) is { } problem) return (entryId, problem);
            await ProposeFilesAsync([entryId], ct);
            return (entryId, null);
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>
    /// Checks the files already in the library against those of <paramref name="entryIds"/> that are books owned
    /// elsewhere, and those of them that are files against every such book, and offers each match in Needs review as
    /// Match does. A pair asked about before isn't asked again. Returns how many cards were added.
    /// </summary>
    public async Task<int> CheckFilesAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        await _checking.WaitAsync(ct);
        try
        {
            return await ProposeFilesAsync(entryIds, ct);
        }
        finally
        {
            _checking.Release();
        }
    }

    async Task<int> ProposeFilesAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct)
    {
        if (entryIds.Count == 0) return 0;
        var asked = entryIds.ToHashSet();
        List<EntryId> proposed = [];
        foreach (var (book, file, documentId) in await queries.GetElsewhereFilesAsync(ct))
        {
            if (!asked.Contains(book) && !asked.Contains(file)) continue;
            if (!await matches.ProposeAsync(book, documentId, ct)) continue;
            _log.LogInformation("Document {Document} has the title of entry {Entry}, owned elsewhere; proposed as its file", documentId, book);
            proposed.Add(book);
        }
        // Projecting the books' marks tells the library, and so Needs review's count, that something changed.
        if (proposed.Count > 0) await projector.ProjectMarksAsync([.. proposed.Distinct()], ct);
        return proposed.Count;
    }

    /// <summary>
    /// Checks the files again whenever cards' metadata are projected: after an edit to a title or publisher, a file
    /// read, and at startup, which covers books added before this check existed. Called once, at startup.
    /// </summary>
    public void CheckFilesWhenProjected() => projector.Projected += (_, entryIds) => Queue(entryIds);

    /// <summary>
    /// Queues a check of <paramref name="entryIds"/> off the projecting thread. Entries projected while a check runs
    /// wait for one more check together, so a burst of projections while files are read costs a check or two, not one each.
    /// </summary>
    void Queue(IReadOnlyCollection<EntryId> entryIds)
    {
        if (entryIds.Count == 0) return;
        lock (_queueLock)
        {
            if (_queued is not null)
            {
                _queued.UnionWith(entryIds);
                return;
            }
            _queued = [.. entryIds];
        }
        _ = Task.Run(CheckQueuedAsync);
    }

    async Task CheckQueuedAsync()
    {
        while (true)
        {
            HashSet<EntryId> entryIds;
            lock (_queueLock)
            {
                entryIds = _queued!;
                if (entryIds.Count == 0)
                {
                    _queued = null;
                    return;
                }
                _queued = [];
            }
            try
            {
                await CheckFilesAsync(entryIds);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Couldn't check the library's files against the books owned elsewhere");
            }
        }
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

    /// <summary>"Remove from library". Returns what Undo needs, or null for a card that isn't owned elsewhere.</summary>
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

    /// <summary>Answers a card. Returns what undoes it, or null when it isn't waiting any more.</summary>
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
