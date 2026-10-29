using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// A Needs review card for one field of one entry, and how many cards propose the same. <see cref="DocumentId"/> is
/// the document the entry's card shows, which opens at the evidence.
/// </summary>
public sealed record ReviewItem(EntryId EntryId, long DocumentId, string Title, ReviewIssue Issue, int GroupSize);

/// <summary>
/// Everything in Needs review's Metadata suggestions tab, and the vocabulary to label it with. <see cref="Parts"/> are
/// the adventures whose pages weren't found in the copy their file shows now, <see cref="Proposals"/> the files that
/// look like they hold several adventures, <see cref="Folders"/> the folders that could be collections, and
/// <see cref="Suggestions"/> the books that could join a collection.
/// </summary>
public sealed record ReviewList(IReadOnlyList<ReviewItem> Items, IReadOnlyList<PendingTerm> Terms, Vocabulary Vocabulary, IReadOnlyList<VersionItem> Versions,
    IReadOnlyList<PackProposal> Packs, IReadOnlyList<ElsewhereItem>? Elsewhere = null, IReadOnlyList<RevisionItem>? Revisions = null,
    IReadOnlyList<CorrectionItem>? Corrections = null, IReadOnlyList<FolderRuleInfo>? Rules = null, IReadOnlyList<UnplacedPart>? Parts = null,
    IReadOnlyList<PartProposalItem>? Proposals = null, IReadOnlyList<FolderCollectionProposal>? Folders = null,
    IReadOnlyList<CollectionSuggestion>? Suggestions = null, IReadOnlyList<OverlapProposal>? Overlaps = null)
{
    public int Count => Items.Count + Terms.Count + Versions.Count + Packs.Count + (Elsewhere?.Count ?? 0) + (Revisions?.Count ?? 0)
        + (Corrections?.Count ?? 0) + (Rules?.Count ?? 0) + (Parts?.Count ?? 0)
        + (Proposals?.Count ?? 0) + (Folders?.Count ?? 0) + (Suggestions?.Count ?? 0) + (Overlaps?.Count ?? 0);
}

/// <summary>
/// Where Needs review's cards come from beyond fields and terms: new versions, packs, books owned elsewhere, places in
/// new versions, books cataloged wrong, folder rules, adventures inside files, and collections from folders and books.
/// </summary>
public sealed record ReviewCards(
    CopiesService Copies,
    PackService Packs,
    ElsewhereService Elsewhere,
    PagePlaces Places,
    CorrectionService Corrections,
    FolderRules Rules,
    PartsService Parts,
    PartProposalService Proposals,
    FolderCollections Folders,
    CollectionSuggestions Suggestions,
    LibraryStore Library);

/// <summary>
/// Needs review's metadata suggestions: which cards there are, and the user's decisions about them. Every
/// decision about a field can be undone exactly, from a snapshot taken before it; a decision about a new term, from
/// what it changed. The entries are projected again after each, so the library and the count keep up.
/// </summary>
public sealed class ReviewService(
    MetadataStore metadata,
    VocabularyStore vocabularies,
    MetadataService edits,
    MetadataProjector projector,
    IndexQueries queries,
    SettingsStore settings,
    VocabularyService vocabulary,
    ReviewCards cards)
{
    public async Task<bool> GetReviewAllAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKeys.ReviewAll, ct) == bool.TrueString;

    /// <summary>Chooses whether every suggestion goes to Needs review, and recounts every entry's cards.</summary>
    public async Task SetReviewAllAsync(bool reviewAll, CancellationToken ct = default)
    {
        if (await GetReviewAllAsync(ct) == reviewAll) return;
        await settings.SetAsync(SettingKeys.ReviewAll, reviewAll.ToString(), ct);
        await projector.ProjectAllAsync(ct);
    }

    /// <summary>
    /// How many cards there are, for the sidebar: as last projected, plus the new terms, the "new version?" cards, the
    /// folders proposed as packs, the files that may be those of books owned elsewhere, the new versions with places
    /// that need a look, the books said to be cataloged wrong, the folder rules proposed, the adventures whose pages
    /// weren't found, the files that look like they hold adventures, the folders that could be collections, the
    /// books that could join collections, and the terms that may be the same thing.
    /// </summary>
    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        var shown = await cards.Library.GetVisibleEntryIdsAsync(ct: ct);
        return await queries.CountReviewsAsync(shown, ct) + (await vocabularies.GetPendingAsync(shown, ct)).Count + await cards.Copies.CountVersionsAsync(ct)
            + (await cards.Packs.GetProposalsAsync(ct)).Count + await cards.Elsewhere.CountPendingAsync(ct)
            + await cards.Places.CountRevisionsAsync(ct)
            + (await cards.Corrections.GetWaitingAsync(ct)).Count + (await cards.Rules.GetProposedAsync(ct)).Count
            + (await cards.Parts.GetUnplacedAsync(ct)).Count
            + await cards.Proposals.CountPendingAsync(ct)
            + await cards.Folders.CountAsync(ct)
            + await cards.Suggestions.CountAsync(ct)
            + (await vocabulary.FindAllOverlapsAsync(ct)).Count;
    }

    /// <summary>
    /// The cards: sources that disagree first, then missing titles, then other suggestions, each by title. The cards
    /// come from catalog.db, so they are current even when a projection is still on its way. Making the list again
    /// brings back the cards put away with Not now.
    /// </summary>
    public async Task<ReviewList> GetQueueAsync(CancellationToken ct = default)
    {
        cards.Folders.ClearNotNow();
        cards.Suggestions.ClearNotNow();
        var reviewAll = await GetReviewAllAsync(ct);
        // Only the books the library shows: a removed folder's books keep what was projected for them, unseen.
        var shown = await cards.Library.GetVisibleEntryIdsAsync(ct: ct);
        var entries = await queries.GetReviewEntriesAsync(shown, ct);
        var all = await metadata.GetManyAsync([.. entries.Select(e => e.EntryId)], ct);
        var found = entries
            .Where(e => all.ContainsKey(e.EntryId) && e.DocumentId is not null)
            .SelectMany(e => MetadataReview.Find(all[e.EntryId].Compute(), reviewAll).Select(issue => (e.EntryId, DocumentId: e.DocumentId!.Value, e.Title, Issue: issue)))
            .ToList();
        var groups = found.Where(f => f.Issue.CanAccept).GroupBy(f => f.Issue.GroupKey).ToDictionary(g => g.Key, g => g.Count());
        var items = found
            .OrderBy(f => f.Issue.Kind)
            .Select(f => new ReviewItem(f.EntryId, f.DocumentId, f.Title, f.Issue, f.Issue.CanAccept ? groups[f.Issue.GroupKey] : 1))
            .ToList();
        return await ListAsync(items, await vocabularies.GetPendingAsync(shown, ct), ct);
    }

    /// <summary>
    /// The cards beyond fields and terms as they wait now, to find one again and answer it; unlike making the list,
    /// this leaves the cards put away with Not now away.
    /// </summary>
    public Task<ReviewList> GetOffersAsync(CancellationToken ct = default) => ListAsync([], [], ct);

    async Task<ReviewList> ListAsync(IReadOnlyList<ReviewItem> items, IReadOnlyList<PendingTerm> terms, CancellationToken ct) =>
        new(items, terms, await vocabularies.GetAsync(ct), await cards.Copies.GetVersionsAsync(ct),
            await cards.Packs.GetProposalsAsync(ct), await cards.Elsewhere.GetPendingAsync(ct), await cards.Places.GetRevisionsAsync(ct),
            await cards.Corrections.GetWaitingAsync(ct), await cards.Rules.GetProposedAsync(ct),
            await cards.Parts.GetUnplacedAsync(ct), await cards.Proposals.GetPendingAsync(ct),
            await cards.Folders.GetProposalsAsync(ct), await cards.Suggestions.GetSuggestionsAsync(ct), await vocabulary.FindAllOverlapsAsync(ct));

    /// <summary>One entry's card for a field, as the queue would make it now; null when it isn't waiting any more.</summary>
    public async Task<ReviewIssue?> FindAsync(EntryId entryId, string fieldKey, ReviewKind kind, CancellationToken ct = default)
    {
        var all = await metadata.GetManyAsync([entryId], ct);
        if (!all.TryGetValue(entryId, out var entry)) return null;
        return MetadataReview.Find(entry.Compute(), await GetReviewAllAsync(ct)).FirstOrDefault(i => i.Field.Key == fieldKey && i.Kind == kind);
    }

    /// <summary>A new term still waiting for a decision, or null.</summary>
    public async Task<PendingTerm?> FindTermAsync(long id, CancellationToken ct = default) =>
        (await vocabularies.GetPendingAsync(ct)).FirstOrDefault(t => t.Id == id);

    public Task<Vocabulary> GetVocabularyAsync(CancellationToken ct = default) => vocabularies.GetAsync(ct);

    /// <summary>
    /// Answers "Make a collection from these books?": Accept, Not now or Never. Returns what undoes it, or null when the
    /// folder isn't waiting any more.
    /// </summary>
    public Task<Func<Task>?> AnswerFolderAsync(FolderCollectionProposal proposal, CollectionAnswer answer, CancellationToken ct = default) =>
        cards.Folders.AnswerAsync(proposal, answer, ct);

    /// <summary>Answers "Add these books to this collection?": Accept, Not now or Never. Returns what undoes it.</summary>
    public async Task<Func<Task>?> AnswerSuggestionAsync(CollectionSuggestion suggestion, CollectionAnswer answer, CancellationToken ct = default) =>
        await cards.Suggestions.AnswerAsync(suggestion, answer, ct);

    /// <summary>"It's fine now" on a "cataloged wrong" card. Returns what brings the card back.</summary>
    public Task<Func<Task>> DismissCorrectionAsync(CorrectionItem item, CancellationToken ct = default) =>
        cards.Corrections.DismissAsync(item, ct);

    /// <summary>Use this rule, or No thanks, on a proposed folder rule. Returns what undoes it.</summary>
    public Task<Func<Task>> AnswerRuleAsync(FolderRuleInfo rule, bool use, CancellationToken ct = default) =>
        cards.Rules.SetAsync(rule, use, ct);

    /// <summary>
    /// Use this page on a "new version" card: the place points at the page suggested for it in the new version from now
    /// on, checked. Returns what undoes it, or null when the place is gone.
    /// </summary>
    public Task<Func<Task>?> UsePlaceAsync(RevisionPlace place, CancellationToken ct = default) =>
        cards.Places.UseAsync(place.Place, place.Check.DocumentId, place.Check.FirstPdfPage, place.Check.LastPdfPage, ct);

    /// <summary>Answers "You own X elsewhere. Is this its file?". Returns what undoes it, or null when it isn't waiting any more.</summary>
    public async Task<Func<Task>?> AnswerElsewhereAsync(PendingElsewhere match, ElsewhereAnswer answer, CancellationToken ct = default)
    {
        if (await cards.Elsewhere.AnswerAsync(match, answer, ct) is not { } decision) return null;
        return () => cards.Elsewhere.UndoAsync(decision);
    }

    /// <summary>
    /// Answers "Make these images one card?": <see cref="PackAnswer.Packed"/> or <see cref="PackAnswer.Split"/> (Keep
    /// separate). Returns what undoes it, or null when the folder is no longer a proposal.
    /// </summary>
    public async Task<Func<Task>?> AnswerPackAsync(PackProposal proposal, PackAnswer answer, CancellationToken ct = default)
    {
        var previous = await cards.Packs.AnswerAsync(proposal.PackId, answer, ct);
        return previous is { } p ? () => cards.Packs.UndoAsync(proposal.PackId, p) : null;
    }

    /// <summary>Answers a "new version?" card. Returns what undoes it, or null when it isn't waiting any more.</summary>
    public Task<VersionDecision?> AnswerVersionAsync(PendingVersion version, VersionAnswer answer, CancellationToken ct = default) =>
        cards.Copies.AnswerVersionAsync(version, answer, ct);

    public Task UndoVersionAsync(VersionDecision decision, CancellationToken ct = default) => cards.Copies.UndoVersionAsync(decision, ct);

    /// <summary>Makes the card's suggestion the field's value, confirmed. Returns what undoes it.</summary>
    public async Task<FieldSnapshot> AcceptAsync(ReviewItem item, CancellationToken ct = default)
    {
        var before = await metadata.SnapshotAsync(item.EntryId, item.Issue.Field, ct);
        await metadata.SetValuesAsync(item.EntryId, item.Issue.Field, [.. item.Issue.Suggested.Select(v => v.Value)], ct);
        await projector.ProjectAsync([item.EntryId], ct);
        return before;
    }

    /// <summary>Accept for several cards at once, as "Accept all" does for cards that propose the same thing.</summary>
    public async Task<IReadOnlyList<FieldSnapshot>> AcceptAllAsync(IReadOnlyList<ReviewItem> items, CancellationToken ct = default)
    {
        var undo = new List<FieldSnapshot>();
        foreach (var item in items)
        {
            undo.Add(await metadata.SnapshotAsync(item.EntryId, item.Issue.Field, ct));
            await metadata.SetValuesAsync(item.EntryId, item.Issue.Field, [.. item.Issue.Suggested.Select(v => v.Value)], ct);
        }
        await projector.ProjectAsync([.. items.Select(i => i.EntryId).Distinct()], ct);
        return undo;
    }

    /// <summary>Rejects what the card proposes, from every source now and later, and keeps what the field shows.</summary>
    public async Task<FieldSnapshot> RejectAsync(ReviewItem item, CancellationToken ct = default)
    {
        var before = await metadata.SnapshotAsync(item.EntryId, item.Issue.Field, ct);
        await metadata.RejectAndKeepAsync(item.EntryId, item.Issue.Field,
            [.. item.Issue.Proposed.Select(v => v.Normalized)], [.. item.Issue.Current.Select(v => v.Value)], ct);
        await projector.ProjectAsync([item.EntryId], ct);
        return before;
    }

    /// <summary>Saves what the user typed instead, as the inspector does. Returns the problem, or what undoes it.</summary>
    public async Task<(MetadataProblem? Problem, FieldSnapshot? Undo)> EditAsync(ReviewItem item, string typed, CancellationToken ct = default)
    {
        var before = await metadata.SnapshotAsync(item.EntryId, item.Issue.Field, ct);
        var problem = await edits.SetAsync(item.EntryId, item.Issue.Field, typed, ct);
        return problem is null ? (null, before) : (problem, null);
    }

    public async Task UndoAsync(IReadOnlyList<FieldSnapshot> snapshots, CancellationToken ct = default)
    {
        foreach (var snapshot in snapshots) await metadata.RestoreAsync(snapshot, ct);
        await projector.ProjectAsync([.. snapshots.Select(s => s.EntryId).Distinct()], ct);
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
        await projector.ProjectAsync(decision.EntryIds, ct);
        vocabulary.ScheduleRefresh();
    }

    async Task<TermDecision> DecideAsync(Func<Task<TermDecision>> decide, bool vocabularyChanged, CancellationToken ct)
    {
        var decision = await decide();
        await projector.ProjectAsync(decision.EntryIds, ct);
        // A new term or name can match folder names too, so the hints from names are read again.
        if (vocabularyChanged) vocabulary.ScheduleRefresh();
        return decision;
    }
}

/// <summary>One term of an overlap proposal: its id and key, its label, and how many books use it.</summary>
public sealed record OverlapMember(long Id, string Key, string Label, int Books);

/// <summary>
/// Terms that may be the same thing: which, why, whether the AI said so (<see cref="FromAi"/>) or the names alone,
/// and which to keep by default. <see cref="Id"/> names the group by its vocabulary and keys, so a card can find it
/// again; <see cref="Stamp"/> says what the card showed, so an answer isn't given to a group that changed.
/// </summary>
public sealed record OverlapProposal(string Id, string Vocabulary, IReadOnlyList<OverlapMember> Terms, string Reason, bool FromAi, long KeepId)
{
    public string Stamp => Hash([Reason, .. Terms.Select(t => $"{t.Id}|{t.Label}")]);

    /// <summary>The id of a group of these keys in a vocabulary.</summary>
    public static string IdOf(string vocabulary, IEnumerable<string> keys) => Hash([vocabulary, .. keys.Order(StringComparer.Ordinal)]);

    static string Hash(IEnumerable<string> parts) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', parts))))[..16];
}

/// <summary>What Combine did, for Undo, and the note it shows.</summary>
public sealed record TermCombination(TermMerge Merge, IReadOnlyList<ChapterScanWas> Scans, string Note);

/// <summary>A pair kept apart, by the labels of its terms.</summary>
public sealed record KeptApartPair(long Id, string First, string Second);

/// <summary>
/// Settings > Vocabulary. Each edit changes catalog.db at once; bringing the library up to date with it (search
/// names, labels, and the hints from folder and file names, which match through the vocabulary) follows in the
/// background, once a run of edits pauses.
/// </summary>
public sealed class VocabularyService(VocabularyStore vocabularies, MetadataProjector projector, IJobControl indexing, MetadataHints hints,
    SettingsStore settings, ILogger<VocabularyService>? log = null, TimeSpan? settle = null, ChapterScanStore? scans = null,
    ChapterProjection? chapters = null, LibraryStore? library = null, TermOverlapFinder? finder = null)
{
    readonly ILogger _log = log ?? NullLogger<VocabularyService>.Instance;
    readonly TimeSpan _settle = settle ?? TimeSpan.FromSeconds(2);
    CancellationTokenSource? _scheduled;
    readonly Lock _overlapLock = new();
    readonly Dictionary<string, (string Names, IReadOnlyList<TermOverlap> Groups)> _nameGroups = new(StringComparer.Ordinal);

    // Find overlaps and Combine.

    /// <summary>The vocabularies Find overlaps compares: every list of terms, and publishers, whose field holds free text.</summary>
    public static IReadOnlyList<string> OverlapVocabularies { get; } = [.. MetadataFields.TermVocabularies, MetadataFields.Publisher.Vocabulary!];

    /// <summary>Whether the AI will compare the names too, in the background, or only the names are compared.</summary>
    public bool AsksAi => finder?.CanAsk ?? false;

    /// <summary>Has the AI compare the names again once things settle, when it is set up and the names have changed.</summary>
    public void FindOverlapsWithAi() => finder?.Schedule();

    /// <summary>
    /// The groups of a vocabulary's terms that may be the same thing: those the names alone say so, and those the AI
    /// said so, each with how many books use each term and the term most books use as the one to keep. A group
    /// whose terms were combined, or any pair of which the user kept apart, is left out.
    /// </summary>
    public async Task<IReadOnlyList<OverlapProposal>> FindOverlapsAsync(string vocabulary, CancellationToken ct = default)
    {
        var terms = await vocabularies.GetOverlapTermsAsync(vocabulary, ct);
        var kept = (await vocabularies.GetKeptApartAsync(vocabulary, ct)).Select(k => (k.FirstKey, k.SecondKey)).ToList();
        var apart = kept.ToHashSet();
        var byName = NameGroups(vocabulary, terms, kept);
        var groups = byName.Select(g => (g.Keys, g.Reason, FromAi: false)).ToList();
        var byKey = terms.ToDictionary(t => t.Key, StringComparer.Ordinal);
        foreach (var found in await vocabularies.GetAiOverlapsAsync(vocabulary, ct))
        {
            // In the list's order, as a name group is, so the term that came first reads first.
            var keys = terms.Where(t => found.Keys.Contains(t.Key, StringComparer.Ordinal)).Select(t => t.Key).ToList();
            if (keys.Count < 2 || keys.Any(a => keys.Any(b => apart.Contains((a, b)) || apart.Contains((b, a))))) continue;
            // The names already say so: one card, not two.
            if (byName.Any(g => keys.All(g.Keys.Contains))) continue;
            groups.Add((keys, found.Reason, true));
        }
        if (groups.Count == 0) return [];

        var books = await vocabularies.CountBooksAsync(vocabulary, ct);
        var ids = (await vocabularies.ListAsync(vocabulary, ct)).ToDictionary(e => e.Key, e => e.Id, StringComparer.Ordinal);
        return [.. groups.Select(group =>
        {
            var members = group.Keys.Select(k => new OverlapMember(ids[k], k, byKey[k].Label, books.GetValueOrDefault(k))).ToList();
            // The term most books use stays; among equals, the one that came with Bibliotaph, then the one made first.
            var keep = members.OrderByDescending(m => m.Books).ThenByDescending(m => byKey[m.Key].Starter).ThenBy(m => m.Id).First();
            return new OverlapProposal(OverlapProposal.IdOf(vocabulary, group.Keys), vocabulary, members, group.Reason, group.FromAi, keep.Id);
        })];
    }

    /// <summary>The groups of every vocabulary, in the order Settings lists them.</summary>
    public async Task<IReadOnlyList<OverlapProposal>> FindAllOverlapsAsync(CancellationToken ct = default)
    {
        var all = new List<OverlapProposal>();
        foreach (var vocabulary in OverlapVocabularies) all.AddRange(await FindOverlapsAsync(vocabulary, ct));
        return all;
    }

    /// <summary>A group as it is now, by id, or null once it isn't proposed any more.</summary>
    public async Task<OverlapProposal?> FindOverlapAsync(string id, CancellationToken ct = default) =>
        (await FindAllOverlapsAsync(ct)).FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Comparing names is the slow part and the sidebar counts often, so a vocabulary's groups are kept until a
    /// name, or a pair kept apart, changes.
    /// </summary>
    IReadOnlyList<TermOverlap> NameGroups(string vocabulary, IReadOnlyList<OverlapTerm> terms, IReadOnlyList<(string First, string Second)> kept)
    {
        var names = string.Join('\n', terms.Select(t => $"{t.Key}={string.Join('|', t.Names)}{(t.Starter ? "*" : "")}")) + "\n"
            + string.Join('\n', kept.Select(k => $"{k.First}~{k.Second}"));
        lock (_overlapLock)
            if (_nameGroups.TryGetValue(vocabulary, out var cached) && cached.Names == names) return cached.Groups;
        var groups = TermOverlaps.Find(terms, kept);
        lock (_overlapLock) _nameGroups[vocabulary] = (names, groups);
        return groups;
    }

    /// <summary>
    /// Combines terms into <paramref name="keepId"/>: the catalog's values, rejections, folder rules, editions and
    /// chapter findings follow, and the library is brought up to date. Returns what Undo needs, with a note.
    /// </summary>
    public async Task<TermCombination> CombineAsync(long keepId, IReadOnlyList<long> mergeIds, CancellationToken ct = default)
    {
        var merge = await vocabularies.MergeAsync(keepId, mergeIds, ct);
        var scansWere = new List<ChapterScanWas>();
        if (scans is not null && ChapterFields.All.Contains(merge.Vocabulary))
            foreach (var source in merge.Merged)
                scansWere.AddRange(await scans.ReplaceTermAsync(merge.Vocabulary, source.Key, merge.TargetKey, merge.TargetLabel, ct));
        await AfterCombineAsync(merge.EntryIds, scansWere, ct);
        var books = merge.EntryIds.Count;
        var note = $"Combined {merge.Merged.Count + 1:N0} terms into {merge.TargetLabel}" + (books > 0 ? $" · {books:N0} {(books == 1 ? "book" : "books")}" : "");
        return new TermCombination(merge, scansWere, note);
    }

    public async Task UndoCombineAsync(TermCombination combination, CancellationToken ct = default)
    {
        await vocabularies.UndoMergeAsync(combination.Merge, ct);
        if (scans is not null) await scans.RestoreAsync(combination.Scans, ct);
        await AfterCombineAsync(combination.Merge.EntryIds, combination.Scans, ct);
    }

    async Task AfterCombineAsync(IReadOnlyList<EntryId> entryIds, IReadOnlyList<ChapterScanWas> scansChanged, CancellationToken ct)
    {
        await projector.ProjectVocabularyAsync(ct);
        await projector.ProjectAsync(entryIds, ct);
        if (scans is not null && chapters is not null && library is not null && scansChanged.Count > 0)
        {
            var hashes = scansChanged.Select(s => s.ContentHash).ToHashSet(StringComparer.Ordinal);
            var records = await scans.GetManyAsync(hashes, ct);
            foreach (var (documentId, hash) in (await library.GetDocumentHashesAsync(ct)).Where(d => hashes.Contains(d.ContentHash)))
                if (records.TryGetValue(hash, out var record)) await chapters.ProjectAsync(documentId, record.Chapters, ct);
        }
        ScheduleRefresh();
    }

    /// <summary>Remembers that the terms of a proposal are not the same thing, so Find overlaps leaves every pair of them alone.</summary>
    public Task<IReadOnlyList<long>> KeepApartAsync(string vocabulary, IReadOnlyList<string> keys, CancellationToken ct = default) =>
        vocabularies.KeepApartAsync(vocabulary, keys, ct);

    /// <summary>The pairs kept apart, each with the labels of its terms, so one can be forgotten.</summary>
    public async Task<IReadOnlyList<KeptApartPair>> KeptApartAsync(string vocabulary, CancellationToken ct = default)
    {
        var labels = (await vocabularies.ListAsync(vocabulary, ct)).ToDictionary(e => e.Key, e => e.Label, StringComparer.Ordinal);
        return [.. (await vocabularies.GetKeptApartAsync(vocabulary, ct))
            .Select(k => new KeptApartPair(k.Id, labels.GetValueOrDefault(k.FirstKey, k.FirstKey), labels.GetValueOrDefault(k.SecondKey, k.SecondKey)))];
    }

    public Task ForgetKeptApartAsync(long id, CancellationToken ct = default) => vocabularies.ForgetKeptApartAsync([id], ct);

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

    /// <summary>Whether a new term the AI uses joins <paramref name="vocabulary"/> at once, rather than asking in Needs review.</summary>
    public Task<bool> AddsAiTermsAsync(string vocabulary, CancellationToken ct = default) => settings.AddsAiTermsAsync(vocabulary, ct);

    /// <summary>
    /// Chooses whether a new term the AI uses joins <paramref name="vocabulary"/> at once. Choosing it adds that
    /// vocabulary's terms waiting in Needs review too, so the list clears, and returns how many it added.
    /// </summary>
    public async Task<int> SetAddsAiTermsAsync(string vocabulary, bool add, CancellationToken ct = default)
    {
        await settings.SetAddsAiTermsAsync(vocabulary, add, ct);
        if (!add) return 0;
        var waiting = (await vocabularies.GetPendingAsync(ct)).Where(p => p.Term.Vocabulary == vocabulary).ToList();
        if (waiting.Count == 0) return 0;
        var entries = new HashSet<EntryId>();
        foreach (var term in waiting) entries.UnionWith((await vocabularies.AcceptPendingAsync(term.Id, ct)).EntryIds);
        await projector.ProjectAsync([.. entries], ct);
        ScheduleRefresh();
        return waiting.Count;
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
    /// Copies the names to index.db, relabels every document, then reads every document's names again, and every
    /// pack's folder names. In that order, so fresh hints are never overwritten by the relabelling.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await projector.ProjectAllAsync(ct);
        var queued = await indexing.RerunAsync(Stage.RuleHints, ct);
        await projector.ProjectAsync(await hints.StorePacksAsync(ct: ct), ct);
        _log.LogInformation("Vocabulary changed; reading names again for {Count} documents", queued);
        finder?.Schedule();
    }
}
