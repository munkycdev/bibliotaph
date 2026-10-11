using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// A correction offered to the books that look like the corrected one (scenario review, row 8): "Change 14 other books
/// from this folder too?". <see cref="Before"/> and <see cref="After"/> are what the field showed before and after the
/// correction, in stored form; <see cref="Changes"/> are the bulk edit that repeats it on <see cref="Books"/>.
/// </summary>
public sealed record LookAlikeOffer(EntryId EntryId, MetadataField Field, FolderKey Folder, IReadOnlyList<EntryId> Books, IReadOnlyList<BulkChange> Changes,
    IReadOnlyList<string> Before, IReadOnlyList<string> After)
{
    /// <summary>The folder as the question names it: its own name, or "this library folder" for the top of one.</summary>
    public string FolderName => Folder.Name is { Length: > 0 } name ? name : "this library folder";
}

/// <summary>A "You said this book was cataloged wrong" card for Needs review: the book, with its title, and when it was left.</summary>
public sealed record CorrectionItem(EntryId EntryId, long? DocumentId, string Title, DateTime CreatedUtc);

/// <summary>
/// Which books look like a corrected one, and what repeating the correction on them is (scenario review, row 8). Kept
/// deliberately narrow, since a wrong guess about many books is worse than asking about none:
/// <list type="bullet">
/// <item>A look-alike has a file directly in the same folder as the corrected book's current file. Subfolders don't count.</item>
/// <item>The user hasn't settled the field on it (no value they set or kept there), so no decision of theirs is overridden.</item>
/// <item>The field shows exactly what the corrected book's showed before the correction: the same wrong guess from the
/// folder, the AI or the file, or nothing at all when the user filled in a gap.</item>
/// <item>Title, year and where to get a book are its own, so they are never offered.</item>
/// </list>
/// </summary>
public static class LookAlikes
{
    /// <summary>Fields that belong to one book alone: never offered to look-alikes, and never a folder rule.</summary>
    public static IReadOnlySet<MetadataField> PerBook { get; } = new HashSet<MetadataField> { MetadataFields.Title, MetadataFields.Year, MetadataFields.GetItFrom };

    /// <summary>
    /// The bulk edit that repeats a correction: the field's new value set (a field with one value, when the user gave
    /// or chose it), values taken out removed and rejected, values put in added. Empty when the field shows the same
    /// values as before (Keep this confirms; it doesn't correct) or the field is one book's own.
    /// </summary>
    public static IReadOnlyList<BulkChange> Changes(EffectiveField before, EffectiveField after)
    {
        var field = before.Field;
        if (PerBook.Contains(field)) return [];
        var removed = before.Values.Where(b => after.Values.All(a => a.Normalized != b.Normalized)).ToList();
        var added = after.Values.Where(a => before.Values.All(b => b.Normalized != a.Normalized)).ToList();
        if (removed.Count == 0 && added.Count == 0) return [];
        if (!field.Multiple)
        {
            // A rejected guess with nothing chosen in its place, or with the next suggestion showing, is repeated as a
            // rejection; a value the user gave is repeated as that value.
            if (after.First is { Confirmed: true } chosen && added.Count > 0) return [new BulkChange(field, BulkAction.Set, chosen.Value)];
            return [.. removed.Select(r => new BulkChange(field, BulkAction.Remove, r.Value))];
        }
        return [.. removed.Select(r => new BulkChange(field, BulkAction.Remove, r.Value)),
            .. added.Where(a => a.Confirmed).Select(a => new BulkChange(field, BulkAction.Add, a.Value))];
    }

    /// <summary>Whether a book's field looks like the corrected book's did before the correction, and the user hasn't settled it.</summary>
    public static bool IsLookAlike(EffectiveField candidate, EffectiveField before) =>
        !candidate.HasConfirmed && candidate.DecidedUtc is null
        && candidate.Values.Select(v => v.Normalized).ToHashSet(StringComparer.Ordinal).SetEquals(before.Values.Select(v => v.Normalized));
}

/// <summary>
/// Easier corrections (scenario review, row 8): "Cataloged wrong…" left unfinished becomes a Needs review card that
/// opens the editor again, and a correction is offered to the books from the same folder that look like the
/// corrected one, through the bulk edit (slice 4e), whose Undo takes it all back.
/// </summary>
public sealed class CorrectionService(CorrectionStore store, MetadataStore metadata, MetadataService edits, LibraryStore library, LibraryQueries cards)
{
    /// <summary>The user closed the editor opened by Cataloged wrong… without changing or keeping anything: Needs review keeps a card.</summary>
    public Task<bool> LeaveUnfinishedAsync(EntryId entryId, CancellationToken ct = default) => store.RequestAsync(entryId, ct);

    /// <summary>The book's correction is done: its card, if it had one, goes.</summary>
    public Task<DateTime?> FinishAsync(EntryId entryId, CancellationToken ct = default) => store.ClearAsync(entryId, ct);

    /// <summary>The cards waiting, oldest first, for the books the library still shows.</summary>
    public async Task<IReadOnlyList<CorrectionItem>> GetWaitingAsync(CancellationToken ct = default)
    {
        var requests = await store.GetRequestsAsync(ct);
        if (requests.Count == 0) return [];
        // A book whose library folder was removed isn't shown, so it has no card either.
        var visible = (await library.GetVisibleEntryIdsAsync(ct: ct)).ToHashSet();
        var shown = (await cards.GetEntriesAsync([.. requests.Select(r => r.EntryId).Where(visible.Contains)], ct)).ToDictionary(e => e.EntryId);
        return [.. requests.Where(r => shown.ContainsKey(r.EntryId))
            .Select(r => new CorrectionItem(r.EntryId, shown[r.EntryId].DocumentId > 0 ? shown[r.EntryId].DocumentId : null, shown[r.EntryId].Title, r.CreatedUtc))];
    }

    /// <summary>"It's fine now" on a card: it goes, and the returned action brings it back.</summary>
    public async Task<Func<Task>> DismissAsync(CorrectionItem item, CancellationToken ct = default)
    {
        var created = await store.ClearAsync(item.EntryId, ct);
        return () => created is { } when ? store.RestoreAsync(item.EntryId, when, CancellationToken.None) : Task.CompletedTask;
    }

    /// <summary>
    /// The look-alikes of a book just corrected (<see cref="LookAlikes"/>), and the bulk edit that would correct them
    /// the same way; null when there are none, or the change isn't one to repeat.
    /// </summary>
    public async Task<LookAlikeOffer?> FindLookAlikesAsync(EntryId entryId, EffectiveField before, EffectiveField after, CancellationToken ct = default)
    {
        var changes = LookAlikes.Changes(before, after);
        if (changes.Count == 0) return null;
        if ((await library.GetEntryFoldersAsync([entryId], ct)).FirstOrDefault(f => f.Present) is not { } home) return null;
        var others = (await library.GetEntriesInFolderAsync(home.Folder, ct)).Where(e => e != entryId).ToList();
        if (others.Count == 0) return null;
        var all = await metadata.GetManyAsync(others, ct);
        var books = others
            .Where(e => LookAlikes.IsLookAlike((all.TryGetValue(e, out var m) ? m.Compute() : EffectiveMetadata.Empty)[before.Field], before))
            .ToList();
        return books.Count == 0 ? null
            : new LookAlikeOffer(entryId, before.Field, home.Folder, books, changes, [.. before.Values.Select(v => v.Value)], [.. after.Values.Select(v => v.Value)]);
    }

    /// <summary>
    /// "Change them": the offer's bulk edit on its books, as the bulk editor applies one (<see cref="MetadataService.EditManyAsync"/>).
    /// Returns the problem, or what undoes it all at once.
    /// </summary>
    public Task<(MetadataProblem? Problem, IReadOnlyList<FieldSnapshot>? Undo)> ApplyAsync(LookAlikeOffer offer, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default) =>
        edits.EditManyAsync(offer.Books, offer.Changes, progress, ct);
}

/// <summary>
/// Folder rules (scenario review, row 8), built beside the folder labels: when the user sets the same value for a field
/// on <see cref="Threshold"/> books from one folder, Needs review proposes a rule that gives it, as a suggestion, to
/// every file in that folder and below, new files included. A rule is proposed once; Use this rule switches it on, No
/// thanks off, and Settings > Library folders lists the rules with a switch each, as it lists the folder labels.
/// </summary>
public sealed class FolderRules(CorrectionStore store, LibraryStore library, MetadataStore metadata, VocabularyStore vocabularies, MetadataHints hints)
{
    /// <summary>Books from one folder the user must have set a value on before a rule is proposed.</summary>
    public const int Threshold = 3;

    /// <summary>
    /// The values set on at least <paramref name="threshold"/> of these books for <paramref name="field"/>, with how
    /// many books each, most first. Every value the user decided counts: typed, chosen, set in bulk or kept, since a
    /// typed value that matches a suggestion is stored as that suggestion kept. A value from a folder rule doesn't.
    /// </summary>
    public static IReadOnlyList<(string Value, int Books)> RepeatedValues(IEnumerable<EntryMetadata> books, MetadataField field, int threshold = Threshold) =>
        [.. books
            .SelectMany(b => b.Claims
                .Where(c => c.Field == field.Key && c.Origin != AssertionOrigin.FolderRule && c.State == AssertionState.Confirmed
                    && b.Rejections.All(r => r.Field != field.Key || r.Normalized != c.Normalized))
                .DistinctBy(c => c.Normalized)
                .Select(c => (b.EntryId, Claim: c)))
            .GroupBy(x => x.Claim.Normalized, StringComparer.Ordinal)
            .Select(g => (Value: g.OrderByDescending(x => x.Claim.CreatedUtc).First().Claim.Value, Books: g.Select(x => x.EntryId).Distinct().Count()))
            .Where(v => v.Books >= threshold)
            .OrderByDescending(v => v.Books).ThenBy(v => v.Value, StringComparer.Ordinal)];

    /// <summary>
    /// After the user set values on these books, proposes a rule for each folder of theirs where a value now repeats
    /// on <see cref="Threshold"/> books, unless the folder's own names already suggest it, or it was proposed before.
    /// Returns how many rules it proposed.
    /// </summary>
    public async Task<int> ConsiderAsync(IReadOnlyCollection<EntryId> entryIds, IReadOnlyCollection<MetadataField> fields, CancellationToken ct = default)
    {
        var considered = fields.Where(f => !LookAlikes.PerBook.Contains(f)).Distinct().ToList();
        if (considered.Count == 0 || entryIds.Count == 0) return 0;
        var folders = (await library.GetEntryFoldersAsync(entryIds, ct)).Where(f => f.Present).Select(f => f.Folder).Distinct().ToList();
        var proposed = 0;
        Vocabulary? vocabulary = null;
        IReadOnlySet<(string Folder, string Vocabulary, string Key)>? ignored = null;
        foreach (var folder in folders)
        {
            var books = await library.GetEntriesInFolderAsync(folder, ct);
            if (books.Count < Threshold) continue;
            var all = await metadata.GetManyAsync(books, ct);
            foreach (var field in considered)
                foreach (var (value, count) in RepeatedValues(all.Values, field))
                {
                    vocabulary ??= await vocabularies.GetAsync(ct);
                    ignored ??= await vocabularies.GetIgnoredFolderLabelsAsync(ct);
                    if (FromFolderNames(folder, field, value, vocabulary, ignored)) continue;
                    if (await store.ProposeAsync(folder, field.Key, value, count, ct)) proposed++;
                }
        }
        return proposed;
    }

    /// <summary>Whether the folder's own names (its folder labels) already suggest this value, which makes a rule pointless.</summary>
    static bool FromFolderNames(FolderKey folder, MetadataField field, string value, Vocabulary vocabulary,
        IReadOnlySet<(string Folder, string Vocabulary, string Key)> ignored)
    {
        var normalized = MetadataValues.Normalize(field, value);
        return RuleHints.Propose(new HintSource(folder.Folder.Length == 0 ? "file.pdf" : $"{folder.Folder}{Path.DirectorySeparatorChar}file.pdf"), vocabulary, ignored)
            .Any(p => p.Origin == AssertionOrigin.Folder && p.Field == field && p.Normalized == normalized);
    }

    /// <summary>The rules waiting in Needs review.</summary>
    public Task<IReadOnlyList<FolderRuleInfo>> GetProposedAsync(CancellationToken ct = default) => store.GetRulesAsync(FolderRuleState.Proposed, ct);

    /// <summary>The rules the user answered, for Settings > Library folders: on ones with their switch on, the rest off.</summary>
    public async Task<IReadOnlyList<FolderRuleInfo>> GetAnsweredAsync(CancellationToken ct = default) =>
        [.. (await store.GetRulesAsync(ct: ct)).Where(r => r.State != FolderRuleState.Proposed)];

    /// <summary>
    /// Use this rule (on) or No thanks (off), from Needs review or Settings: the files in the folder have their hints
    /// read again, so the rule's suggestion arrives or goes. Returns what undoes it.
    /// </summary>
    public async Task<Func<Task>> SetAsync(FolderRuleInfo rule, bool on, CancellationToken ct = default)
    {
        var oneValue = MetadataFields.Find(rule.Field) is { Multiple: false };
        var was = await store.SetStateAsync(rule.Id, on ? FolderRuleState.On : FolderRuleState.Off, oneValue, ct);
        await hints.RefreshFolderAsync(rule.Folder, ct);
        return async () =>
        {
            await store.RestoreAsync(was, CancellationToken.None);
            await hints.RefreshFolderAsync(rule.Folder, CancellationToken.None);
        };
    }
}
