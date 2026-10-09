using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>A Needs review card for one field of one document, and how many cards propose the same.</summary>
public sealed record ReviewItem(long DocumentId, string Title, ReviewIssue Issue, int GroupSize);

/// <summary>Everything in Needs review's Metadata suggestions tab, and the vocabulary to label it with.</summary>
public sealed record ReviewList(IReadOnlyList<ReviewItem> Items, IReadOnlyList<PendingTerm> Terms, Vocabulary Vocabulary)
{
    public int Count => Items.Count + Terms.Count;
}

/// <summary>
/// Needs review's metadata suggestions (spec 5.5): which cards there are, and the user's decisions about them. Every
/// decision about a field can be undone exactly, from a snapshot taken before it; a decision about a new term, from
/// what it changed. The documents are projected again after each, so the library and the count keep up.
/// </summary>
public sealed class ReviewService(
    MetadataStore metadata,
    VocabularyStore vocabularies,
    MetadataService edits,
    MetadataProjector projector,
    IndexQueries queries,
    SettingsStore settings,
    VocabularyService vocabulary)
{
    public async Task<bool> GetReviewAllAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.ReviewAll, ct) == bool.TrueString;

    /// <summary>Chooses whether every suggestion goes to Needs review, and recounts every document's cards.</summary>
    public async Task SetReviewAllAsync(bool reviewAll, CancellationToken ct = default)
    {
        if (await GetReviewAllAsync(ct) == reviewAll) return;
        await settings.SetAsync(SettingKeys.ReviewAll, reviewAll.ToString(), ct);
        await projector.ProjectAllAsync(ct);
    }

    /// <summary>How many cards there are, for the sidebar: as last projected, plus the new terms.</summary>
    public async Task<long> CountAsync(CancellationToken ct = default) =>
        await queries.CountReviewsAsync(ct) + (await vocabularies.GetPendingAsync(ct)).Count;

    /// <summary>
    /// The cards: sources that disagree first, then missing titles, then other suggestions, each by title. The cards
    /// come from catalog.db, so they are current even when a projection is still on its way.
    /// </summary>
    public async Task<ReviewList> GetQueueAsync(CancellationToken ct = default)
    {
        var reviewAll = await GetReviewAllAsync(ct);
        var documents = await queries.GetReviewDocumentsAsync(ct);
        var all = await metadata.GetManyAsync([.. documents.Select(d => d.DocumentId)], ct);
        var found = documents
            .Where(d => all.ContainsKey(d.DocumentId))
            .SelectMany(d => MetadataReview.Find(all[d.DocumentId].Compute(), reviewAll).Select(issue => (d.DocumentId, d.Title, Issue: issue)))
            .ToList();
        var groups = found.Where(f => f.Issue.CanAccept).GroupBy(f => f.Issue.GroupKey).ToDictionary(g => g.Key, g => g.Count());
        var items = found
            .OrderBy(f => f.Issue.Kind)
            .Select(f => new ReviewItem(f.DocumentId, f.Title, f.Issue, f.Issue.CanAccept ? groups[f.Issue.GroupKey] : 1))
            .ToList();
        return new ReviewList(items, await vocabularies.GetPendingAsync(ct), await vocabularies.GetAsync(ct));
    }

    /// <summary>Makes the card's suggestion the field's value, confirmed. Returns what undoes it.</summary>
    public async Task<FieldSnapshot> AcceptAsync(ReviewItem item, CancellationToken ct = default)
    {
        var before = await metadata.SnapshotAsync(item.DocumentId, item.Issue.Field, ct);
        await metadata.SetValuesAsync(item.DocumentId, item.Issue.Field, [.. item.Issue.Suggested.Select(v => v.Value)], ct);
        await projector.ProjectAsync([item.DocumentId], ct);
        return before;
    }

    /// <summary>Accept for several cards at once, as "Accept all" does for cards that propose the same thing.</summary>
    public async Task<IReadOnlyList<FieldSnapshot>> AcceptAllAsync(IReadOnlyList<ReviewItem> items, CancellationToken ct = default)
    {
        var undo = new List<FieldSnapshot>();
        foreach (var item in items)
        {
            undo.Add(await metadata.SnapshotAsync(item.DocumentId, item.Issue.Field, ct));
            await metadata.SetValuesAsync(item.DocumentId, item.Issue.Field, [.. item.Issue.Suggested.Select(v => v.Value)], ct);
        }
        await projector.ProjectAsync([.. items.Select(i => i.DocumentId).Distinct()], ct);
        return undo;
    }

    /// <summary>Rejects what the card proposes, from every source now and later, and keeps what the field shows.</summary>
    public async Task<FieldSnapshot> RejectAsync(ReviewItem item, CancellationToken ct = default)
    {
        var before = await metadata.SnapshotAsync(item.DocumentId, item.Issue.Field, ct);
        await metadata.RejectAndKeepAsync(item.DocumentId, item.Issue.Field,
            [.. item.Issue.Proposed.Select(v => v.Normalized)], [.. item.Issue.Current.Select(v => v.Value)], ct);
        await projector.ProjectAsync([item.DocumentId], ct);
        return before;
    }

    /// <summary>Saves what the user typed instead, as the inspector does. Returns the problem, or what undoes it.</summary>
    public async Task<(MetadataProblem? Problem, FieldSnapshot? Undo)> EditAsync(ReviewItem item, string typed, CancellationToken ct = default)
    {
        var before = await metadata.SnapshotAsync(item.DocumentId, item.Issue.Field, ct);
        var problem = await edits.SetAsync(item.DocumentId, item.Issue.Field, typed, ct);
        return problem is null ? (null, before) : (problem, null);
    }

    public async Task UndoAsync(IReadOnlyList<FieldSnapshot> snapshots, CancellationToken ct = default)
    {
        foreach (var snapshot in snapshots) await metadata.RestoreAsync(snapshot, ct);
        await projector.ProjectAsync([.. snapshots.Select(s => s.DocumentId).Distinct()], ct);
    }

    public Task<TermDecision> AddTermAsync(PendingTerm term, CancellationToken ct = default) =>
        DecideAsync(() => vocabularies.AcceptPendingAsync(term.Id, ct), vocabularyChanged: true, ct);

    public Task<TermDecision> MapTermAsync(PendingTerm term, string targetKey, CancellationToken ct = default) =>
        DecideAsync(() => vocabularies.MapPendingAsync(term.Id, targetKey, ct), vocabularyChanged: true, ct);

    public Task<TermDecision> RejectTermAsync(PendingTerm term, CancellationToken ct = default) =>
        DecideAsync(() => vocabularies.RejectPendingAsync(term.Id, ct), vocabularyChanged: false, ct);

    public async Task UndoTermAsync(TermDecision decision, CancellationToken ct = default)
    {
        await vocabularies.UndoAsync(decision, ct);
        await projector.ProjectAsync(decision.DocumentIds, ct);
        vocabulary.ScheduleRefresh();
    }

    async Task<TermDecision> DecideAsync(Func<Task<TermDecision>> decide, bool vocabularyChanged, CancellationToken ct)
    {
        var decision = await decide();
        await projector.ProjectAsync(decision.DocumentIds, ct);
        // A new term or name can match folder names too, so the hints from names are read again.
        if (vocabularyChanged) vocabulary.ScheduleRefresh();
        return decision;
    }
}

/// <summary>
/// Settings > Vocabulary. Each edit changes catalog.db at once; bringing the library up to date with it (search
/// names, labels, and the hints from folder and file names, which match through the vocabulary) follows in the
/// background, once a run of edits pauses.
/// </summary>
public sealed class VocabularyService(VocabularyStore vocabularies, MetadataProjector projector, IndexingService indexing,
    ILogger<VocabularyService>? log = null, TimeSpan? settle = null)
{
    readonly ILogger _log = log ?? NullLogger<VocabularyService>.Instance;
    readonly TimeSpan _settle = settle ?? TimeSpan.FromSeconds(2);
    CancellationTokenSource? _scheduled;

    public Task<IReadOnlyList<VocabularyEntry>> ListAsync(string vocabulary, CancellationToken ct = default) => vocabularies.ListAsync(vocabulary, ct);

    public Task<VocabularyEdit> AddTermAsync(string vocabulary, string label, CancellationToken ct = default) =>
        AfterAsync(vocabularies.AddTermAsync(vocabulary, label, ct));

    public Task<VocabularyEdit> RenameAsync(long termId, string label, string? shortLabel, CancellationToken ct = default) =>
        AfterAsync(vocabularies.RenameAsync(termId, label, shortLabel, ct));

    public Task<VocabularyEdit> AddAliasAsync(long termId, string text, CancellationToken ct = default) =>
        AfterAsync(vocabularies.AddAliasAsync(termId, text, ct));

    public async Task RemoveAliasAsync(long aliasId, CancellationToken ct = default)
    {
        await vocabularies.RemoveAliasAsync(aliasId, ct);
        ScheduleRefresh();
    }

    async Task<VocabularyEdit> AfterAsync(Task<VocabularyEdit> edit)
    {
        var result = await edit;
        if (result.Problem is null) ScheduleRefresh();
        return result;
    }

    /// <summary>Brings the library up to date with the vocabulary once edits have paused, replacing any refresh still waiting.</summary>
    public void ScheduleRefresh()
    {
        var next = new CancellationTokenSource();
        Interlocked.Exchange(ref _scheduled, next)?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_settle, next.Token);
                await RefreshAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // A later edit scheduled its own refresh.
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Bringing the library up to date with the vocabulary failed");
            }
        });
    }

    /// <summary>
    /// Copies the names to index.db, relabels every document, then reads every document's names again. In that order,
    /// so a document's fresh hints are never overwritten by the relabelling.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await projector.ProjectAllAsync(ct);
        var queued = await indexing.RerunAsync(Stage.RuleHints, ct);
        _log.LogInformation("Vocabulary changed; reading names again for {Count} documents", queued);
    }
}
