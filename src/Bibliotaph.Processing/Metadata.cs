using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// The user's marks on their books, which the projector copies into index.db: their hearts, when they were last opened,
/// the collections and session packs they are in, and their notes.
/// </summary>
public sealed record MarkStores(FavoriteStore Favorites, ReadingStore Reading, CollectionStore Collections, SessionStore Sessions, NoteStore Notes);

/// <summary>
/// Copies the library's cards from catalog.db into index.db: which document each entry shows (entry_doc), and its
/// effective metadata (entry_meta, entry_facet, entry_fts, term_alias), where the library lists, filters and searches
/// it, and which entries a model has read (entry_ai), for the AI badge and filter. It also copies the user's marks: the
/// groups each entry is in (entry_scope: favourites, collections and session packs, with the collections' names in scope_name),
/// when it was last opened (entry_opened) and the text of its notes, its own and its pages', for search (entry_note). index.db holds
/// only this projection, never the assertions, so rebuilding it loses nothing: the projection runs again.
/// <paramref name="covers"/> finds the covers books owned elsewhere got from their online library.
/// </summary>
public sealed class MetadataProjector(MetadataStore metadata, EntryStore entries, VocabularyStore vocabularies, IndexStore index, IndexQueries queries,
    SettingsStore settings, ClassificationStore runs, MarkStores marks, CoverCache covers, ILogger<MetadataProjector>? log = null) : IDisposable
{
    const int Batch = 200;

    readonly ILogger _log = log ?? NullLogger<MetadataProjector>.Instance;

    /// <summary>
    /// One projection at a time. Each reads catalog.db then writes index.db, so two interleaved could write back what
    /// the other had just taken away: a stage projecting an image as its own card after the image joined a pack.
    /// </summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>Raised after entries' metadata changed in index.db, on a background thread.</summary>
    public event EventHandler<IReadOnlyCollection<EntryId>>? Projected;

    public async Task ProjectAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return;
        await _gate.WaitAsync(ct);
        IReadOnlyCollection<EntryId> projected;
        try
        {
            projected = await WriteAsync(entryIds, ct);
        }
        finally
        {
            _gate.Release();
        }
        Projected?.Invoke(this, projected);
    }

    /// <summary>
    /// Copies only the user's marks for these entries (their heart, the groups they are in, when they were opened, their
    /// notes), after one of those changed: lighter than <see cref="ProjectAsync"/>, which also projects their metadata.
    /// </summary>
    public async Task ProjectMarksAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        if (entryIds.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            await WriteMarksAsync(entryIds, ct);
        }
        finally
        {
            _gate.Release();
        }
        Projected?.Invoke(this, entryIds);
    }

    /// <summary>
    /// After collections changed: their names, and the marks of <paramref name="entryIds"/>, the entries whose
    /// collections changed. Raises <see cref="Projected"/> even when no entry changed, as after a rename.
    /// </summary>
    public async Task ProjectCollectionsAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await WriteCollectionNamesAsync(ct);
            await WriteMarksAsync(entryIds, ct);
        }
        finally
        {
            _gate.Release();
        }
        Projected?.Invoke(this, entryIds);
    }

    /// <summary>The marks of these entries, or of every entry when <paramref name="entryIds"/> is null.</summary>
    async Task WriteMarksAsync(IReadOnlyCollection<EntryId>? entryIds, CancellationToken ct)
    {
        if (entryIds is { Count: 0 }) return;
        List<(EntryId, string)> scopes =
        [
            .. (await marks.Favorites.GetAsync(entryIds, ct)).Select(id => (id, ScopeKeys.Favorites)),
            .. await marks.Collections.GetScopesAsync(entryIds, ct),
            .. await marks.Sessions.GetScopesAsync(entryIds, ct),
        ];
        await index.SetScopesAsync(entryIds, scopes, ct);
        await index.SetOpenedAsync(entryIds, await marks.Reading.GetOpenedAsync(entryIds, ct), ct);
        await index.SetNotesAsync(entryIds, await marks.Notes.GetSearchTextAsync(entryIds, ct), ct);
        if (entryIds is null) await WriteCollectionNamesAsync(ct);
    }

    async Task WriteCollectionNamesAsync(CancellationToken ct)
    {
        await index.SetScopeNamesAsync([.. (await marks.Collections.ListAsync(ct)).SelectMany(c =>
            new[] { (ScopeKeys.Collection(c.Id), c.Name), (ScopeKeys.CollectionOwn(c.Id), c.Name) })], ct);
    }

    /// <summary>
    /// Records which cards show a document that has just been read, so it appears in the library. Taken with the other
    /// projections, so it can't put back a card that one has just removed.
    /// </summary>
    public async Task ProjectShownByAsync(long documentId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await index.SetEntriesAsync([.. (await entries.GetShownByAsync(documentId, ct)).Select(ToRow)], ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Projects these entries and the adventures marked in their files, whose cards show their file's title and take
    /// its game system, publisher and the rest. Returns every entry projected.
    /// </summary>
    async Task<IReadOnlyCollection<EntryId>> WriteAsync(IReadOnlyCollection<EntryId> requested, CancellationToken ct)
    {
        IReadOnlyCollection<EntryId> entryIds = [.. requested.Union(await entries.GetPartIdsAsync(requested, ct))];
        var vocabulary = await vocabularies.GetAsync(ct);
        var reviewAll = await ReviewAllAsync(ct);
        var current = await entries.GetCurrentAsync(entryIds, ct);
        await index.SetEntriesAsync([.. current.Select(ToRow)], ct);
        // An entry with no file to show, such as one whose copy joined another card, leaves the library.
        var shown = current.Select(e => e.EntryId).ToHashSet();
        await index.RemoveEntriesAsync([.. entryIds.Where(id => !shown.Contains(id))], ct);
        var all = await metadata.GetManyAsync(shown, ct);
        await index.SetMetadataAsync([.. all.Values.Select(m => Build(m, vocabulary, reviewAll))], ct);
        await index.ClearMetadataAsync([.. shown.Where(id => !all.ContainsKey(id))], ct);
        await index.SetAiReadAsync(shown, await runs.GetReadByAsync(shown, ct), ct);
        await WriteMarksAsync(shown, ct);
        return entryIds;
    }

    EntryDocRow ToRow(EntryDocument entry) => new(entry.EntryId, entry.DocumentId, entry.Kind, entry.Copies)
    {
        Cover = entry.SyncId is { } syncId ? covers.FindElsewhere(syncId) : null,
        Name = entry.Name,
        AddedUtc = entry.AddedUtc,
        Members = entry.Members is { } members ? [.. members.Select(m => new EntryMemberRow(m.DocumentId, m.EntryId, m.Name))] : [],
        ParentEntryId = entry.ParentEntryId,
        Pages = entry.Pages,
        Parts = entry.Parts,
    };

    /// <summary>Copies every name of every term into index.db, for field search. Run when a term is added.</summary>
    public async Task ProjectVocabularyAsync(CancellationToken ct = default)
    {
        var vocabulary = await vocabularies.GetAsync(ct);
        await index.SetTermAliasesAsync([.. vocabulary.Aliases.Select(a => new TermAliasRow(a.Term.Vocabulary, a.Alias, a.Term.Key))], ct);
    }

    /// <summary>Projects every entry and the vocabulary's aliases. Run at startup, and after the vocabulary changes.</summary>
    public async Task ProjectAllAsync(CancellationToken ct = default)
    {
        await ProjectVocabularyAsync(ct);
        IReadOnlyCollection<EntryId> projected;
        await _gate.WaitAsync(ct);
        try
        {
            projected = await WriteAllAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        Projected?.Invoke(this, projected);
    }

    async Task<IReadOnlyCollection<EntryId>> WriteAllAsync(CancellationToken ct)
    {
        var vocabulary = await vocabularies.GetAsync(ct);
        var reviewAll = await ReviewAllAsync(ct);
        var current = await entries.GetCurrentAsync(ct: ct);
        foreach (var chunk in current.Chunk(Batch))
            await index.SetEntriesAsync([.. chunk.Select(ToRow)], ct);
        var shown = current.Select(e => e.EntryId).ToHashSet();
        var (indexed, withMetadata) = await queries.GetEntryIdsAsync(ct);
        await index.RemoveEntriesAsync([.. indexed.Concat(withMetadata).Distinct().Where(id => !shown.Contains(id))], ct);
        var all = (await metadata.GetManyAsync(null, ct)).Where(m => shown.Contains(m.Key)).ToDictionary();
        foreach (var chunk in all.Values.Chunk(Batch))
            await index.SetMetadataAsync([.. chunk.Select(m => Build(m, vocabulary, reviewAll))], ct);
        await index.ClearMetadataAsync([.. withMetadata.Where(id => shown.Contains(id) && !all.ContainsKey(id))], ct);
        await index.SetAiReadAsync(null, (await runs.GetReadByAsync(ct: ct)).Where(r => shown.Contains(r.Key)).ToDictionary(), ct);
        await WriteMarksAsync(null, ct);
        _log.LogInformation("Projected metadata for {Count} entries", all.Count);
        return [.. all.Keys];
    }

    /// <summary>Whether every suggestion goes to Needs review, not only those a person has to look at.</summary>
    async Task<bool> ReviewAllAsync(CancellationToken ct) =>
        await settings.GetAsync(SettingKeys.ReviewAll, ct) == bool.TrueString;

    /// <summary>An entry's index.db metadata row: its effective values, labelled through the vocabulary.</summary>
    public static EntryMetaRow Build(EntryMetadata entry, Vocabulary vocabulary, bool reviewAll = false)
    {
        var effective = entry.Compute();
        string? One(MetadataField field) => effective[field].First?.Value;
        string? Joined(MetadataField field) => effective[field].Values.Count == 0 ? null : string.Join("; ", effective[field].Values.Select(v => v.Value));

        var facets = new List<FacetRow>();
        foreach (var field in MetadataFields.All.Where(f => f.Kind == FieldKind.Term))
            foreach (var value in effective[field].Values)
                facets.Add(new FacetRow(field.Key, value.Value, vocabulary.Label(field.Vocabulary!, value.Value), value.Confirmed));

        // Where to get it and where else it is owned are filtered and searched by term; a term with two links is one
        // facet, which keeps the first link (a confirmed one first) for cards to open.
        foreach (var field in MetadataFields.All.Where(f => f.Kind == FieldKind.Store))
            foreach (var term in effective[field].Values.GroupBy(v => StoreLink.Parse(v.Value).Store, StringComparer.Ordinal))
                facets.Add(new FacetRow(field.Key, term.Key, vocabulary.Label(field.Vocabulary!, term.Key), term.Any(v => v.Confirmed),
                    term.OrderByDescending(v => v.Confirmed).Select(v => StoreLink.Parse(v.Value).Url).FirstOrDefault(u => u is not null)));

        // An edition implies its system, so filtering by D&D finds books only known to be 5e.
        var edition = effective[MetadataFields.Edition].First;
        var editionTerm = edition is null ? null : vocabulary.Find("edition", edition.Value);
        if (editionTerm?.ParentKey is { } parent && facets.All(f => !(f.Field == "system" && f.Value == parent)))
            facets.Add(new FacetRow("system", parent, vocabulary.Label("system", parent), edition!.Confirmed));

        var levels = effective[MetadataFields.Levels].First is { } l && LevelRange.TryParse(l.Value, out var range) ? range : (LevelRange?)null;
        var players = effective[MetadataFields.Players].First is { } p && PlayerRange.TryParse(p.Value, out var party) ? party : (PlayerRange?)null;

        return new EntryMetaRow
        {
            EntryId = entry.EntryId,
            Title = One(MetadataFields.Title),
            Publisher = One(MetadataFields.Publisher),
            Series = One(MetadataFields.Series),
            Authors = Joined(MetadataFields.Authors),
            Year = int.TryParse(One(MetadataFields.Year), out var year) ? year : null,
            SystemLabel = SystemLabel(effective, vocabulary),
            KindLabel = effective[MetadataFields.Types].Values.Count == 0 ? null
                : string.Join(", ", effective[MetadataFields.Types].Values.Select(v => vocabulary.Label("type", v.Value))),
            LevelMin = levels is { NotApplicable: false } known ? known.Min : null,
            LevelMax = levels is { NotApplicable: false } known2 ? known2.Max : null,
            Levels = levels is null ? LevelState.Unknown : levels.Value.NotApplicable ? LevelState.NotApplicable : LevelState.Known,
            PlayersMin = players?.Min,
            PlayersMax = players?.Max,
            Reviews = MetadataReview.Find(effective, reviewAll).Count,
            Suggested = effective.Fields.Any(f => f.Field.IsClosed && f.Values.Any(v => !v.Confirmed)),
            Tags = Joined(MetadataFields.Tags),
            ConfirmedText = SearchText(facets.Where(f => f.Confirmed), vocabulary),
            ProvisionalText = SearchText(facets.Where(f => !f.Confirmed), vocabulary),
            Facets = facets,
        };
    }

    /// <summary>
    /// "D&amp;D 5e", "PF2e", "Call of Cthulhu": an edition's short name, after its system's when the edition's is only a
    /// number ("5e"), or the system's alone.
    /// </summary>
    internal static string? SystemLabel(EffectiveMetadata effective, Vocabulary vocabulary)
    {
        var edition = effective[MetadataFields.Edition].First is { } e ? vocabulary.Find("edition", e.Value) ?? new Term("edition", e.Value, e.Value) : null;
        var systemKey = effective[MetadataFields.System].First?.Value ?? edition?.ParentKey;
        var system = systemKey is null ? null : vocabulary.ShortLabel("system", systemKey);
        if (edition is null) return system;
        return system is not null && edition.Brief.Length > 0 && char.IsDigit(edition.Brief[0]) ? $"{system} {edition.Brief}" : edition.Brief;
    }

    /// <summary>Every name of these values that someone might type: label and short label.</summary>
    static string SearchText(IEnumerable<FacetRow> facets, Vocabulary vocabulary) =>
        string.Join(" · ", facets.SelectMany(f => vocabulary.Find(f.Field, f.Value) is { } term ? new[] { term.Label, term.ShortLabel } : [f.Label])
            .Where(t => !string.IsNullOrEmpty(t)).Distinct());
}

/// <summary>
/// Rule hints for documents: reads where the file is and what the PDF says about itself, stores the suggestions in
/// catalog.db on the document's entry and projects the result. Packs get theirs from their folder's names. Also lists
/// and switches the folder labels Settings shows, and gives files the folder rules switched on where they are.
/// </summary>
public sealed class MetadataHints(LibraryStore library, EntryStore entries, IndexQueries queries, MetadataStore metadata, VocabularyStore vocabularies,
    MetadataProjector projector, PackStore packs, CorrectionStore rules)
{
    /// <summary>
    /// Stores packs' hints from their folder's names: every folder down to the pack's own, or its ZIP, counts through
    /// the vocabulary as it does for a file, and the pack's name is its title. Every pack's, or only those among
    /// <paramref name="entryIds"/>. Returns the packs whose hints were stored, to project.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> StorePacksAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        var places = await packs.GetPlacesAsync(entryIds, ct);
        if (places.Count == 0) return [];
        var vocabulary = await vocabularies.GetAsync(ct);
        var ignored = await vocabularies.GetIgnoredFolderLabelsAsync(ct);
        foreach (var (packId, place) in places)
            await metadata.ReplaceHintsAsync(packId, null, PackHints(place, vocabulary, ignored), ct);
        return [.. places.Keys];
    }

    /// <summary>A pack's hints: read as if an image sat directly in its folder or ZIP, with the pack's name as the title.</summary>
    internal static IReadOnlyList<MetadataProposal> PackHints(PackPlace place, Vocabulary vocabulary, IReadOnlySet<(string Folder, string Vocabulary, string Key)>? ignored = null)
    {
        var fromFolders = RuleHints.Propose(new HintSource(Path.Combine(place.FolderPath, "pack.png")), vocabulary, ignored)
            .Where(p => p.Field != MetadataFields.Title);
        return [.. fromFolders, new MetadataProposal(MetadataFields.Title, PackStore.Name(place.FolderPath, place.IsArchive), AssertionOrigin.Filename,
            PackStore.FileName(place.FolderPath))];
    }

    /// <summary>Re-reads a document's names and replaces the rule-hint suggestions it gave its entry. False when it has no file location left.</summary>
    public async Task<bool> ApplyAsync(long documentId, CancellationToken ct = default)
    {
        if (await StoreAsync(documentId, ct) is not { } stored) return false;
        await projector.ProjectAsync([stored.Entry], ct);
        return true;
    }

    /// <summary>
    /// Reads a document's hints again once its text is in, for the store its watermark names, and projects its entry
    /// only when that changed something.
    /// </summary>
    public async Task RefreshAsync(long documentId, CancellationToken ct = default)
    {
        if (await StoreAsync(documentId, ct) is { Changed: true } stored) await projector.ProjectAsync([stored.Entry], ct);
    }

    /// <summary>
    /// Stores a document's hints on its entry, which it returns with whether they changed; null when the document has
    /// no file location left.
    /// </summary>
    async Task<(EntryId Entry, bool Changed)?> StoreAsync(long documentId, CancellationToken ct)
    {
        var place = await library.GetHintPathAsync(documentId, ct);
        if (place is not var (rootId, path) || await entries.GetEntryAsync(documentId, ct) is not { } entry) return null;
        var info = await queries.GetEmbeddedInfoAsync(documentId, ct);
        // The watermark lines are read here, matched against the store names and dropped: never stored or logged.
        var watermarks = Watermarks.Find(await queries.GetAllPageTextsAsync(documentId, ct));
        var source = new HintSource(path, info?.Title, info?.Author, info?.Subject, info?.Keywords, info?.Producer, watermarks, await RulesAsync(rootId, ct));
        var proposals = RuleHints.Propose(source, await vocabularies.GetAsync(ct), await vocabularies.GetIgnoredFolderLabelsAsync(ct));
        return (entry.EntryId, await metadata.ReplaceHintsAsync(entry.EntryId, entry.ContentHash, proposals, ct));
    }

    /// <summary>The folder rules switched on in one library folder.</summary>
    async Task<IReadOnlyList<FolderRuleHint>> RulesAsync(long rootId, CancellationToken ct)
    {
        return [.. (await rules.GetRulesAsync(FolderRuleState.On, ct))
            .Where(r => r.Folder.RootId == rootId && MetadataFields.Find(r.Field) is not null)
            .Select(r => new FolderRuleHint(r.Folder.Folder, MetadataFields.Get(r.Field), r.Value))];
    }

    /// <summary>
    /// Reads the hints again for every file in a folder and below, after one of its folder rules was switched on or
    /// off, and projects their cards.
    /// </summary>
    public async Task RefreshFolderAsync(FolderKey folder, CancellationToken ct = default)
    {
        var stored = new List<EntryId>();
        foreach (var documentId in await library.GetDocumentsUnderAsync(folder, ct))
            if (await StoreAsync(documentId, ct) is { } entry) stored.Add(entry.Entry);
        await projector.ProjectAsync([.. stored.Distinct()], ct);
    }

    /// <summary>
    /// The vocabulary terms folder names in the library stand for, with how many documents sit under each and whether
    /// the user has switched it off, for Settings > Library.
    /// </summary>
    public async Task<IReadOnlyList<FolderLabelUse>> GetFolderLabelsAsync(CancellationToken ct = default)
    {
        var vocabulary = await vocabularies.GetAsync(ct);
        var ignored = await vocabularies.GetIgnoredFolderLabelsAsync(ct);
        var uses = new Dictionary<(string Folder, string Vocabulary, string Key), (string Folder, Term Term, HashSet<long> Documents)>();
        foreach (var (documentId, path) in await library.GetRelativePathsAsync(ct))
            foreach (var folder in RuleHints.FolderNames(path))
                foreach (var label in RuleHints.Labels(folder, vocabulary))
                {
                    var key = (MetadataText.Normalize(folder), label.Term.Vocabulary, label.Term.Key);
                    if (!uses.TryGetValue(key, out var use)) uses[key] = use = (folder, label.Term, []);
                    use.Documents.Add(documentId);
                }
        return [.. uses.Select(u => new FolderLabelUse(u.Value.Folder, u.Value.Term, u.Value.Documents.Count, !ignored.Contains(u.Key)))
            .OrderBy(u => u.Folder, StringComparer.CurrentCultureIgnoreCase).ThenBy(u => u.Term.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Switches a folder label on or off and refreshes the hints of every document under a folder of that name.</summary>
    public async Task SetFolderLabelEnabledAsync(string folder, Term term, bool enabled, CancellationToken ct = default)
    {
        if (!await vocabularies.SetFolderLabelEnabledAsync(folder, term.Vocabulary, term.Key, enabled, ct)) return;
        var normalized = MetadataText.Normalize(folder);
        var affected = (await library.GetRelativePathsAsync(ct))
            .Where(p => RuleHints.FolderNames(p.RelativePath).Any(f => MetadataText.Normalize(f) == normalized))
            .Select(p => p.DocumentId).Distinct().ToList();
        var stored = new List<EntryId>();
        foreach (var documentId in affected)
            if (await StoreAsync(documentId, ct) is { } entry) stored.Add(entry.Entry);
        stored.AddRange(await StorePacksAsync(ct: ct));
        await projector.ProjectAsync([.. stored.Distinct()], ct);
    }
}

/// <summary>A folder name that stands for a term, the documents under it, and whether the label is switched on.</summary>
public sealed record FolderLabelUse(string Folder, Term Term, int Documents, bool Enabled);

/// <summary>Hints from names: folder names, the file name and the PDF's own information as provisional metadata.</summary>
public sealed class RuleHintsStage(MetadataHints hints, PartProposalService proposals) : IStage
{
    public Stage Stage => Stage.RuleHints;

    public Lane Lane => Lane.Index;

    public Stage? After => Stage.Probe;

    /// <summary>A library indexed before rule hints existed gets them without probing anything again.</summary>
    public bool Backfill => true;

    public StageNames Names => new("Hints from names", "Reading hints from its name", "Working on");

    /// <summary>Reads hints from the file's names, then whether its bookmarks and blurbs say it holds several adventures.</summary>
    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (!await hints.ApplyAsync(job.DocumentId, ct)) return new StageOutcome.Done(StageStatus.Skipped, [], "The file has gone, so there are no names to read.");
        await proposals.ProposeAsync(job.DocumentId, ct);
        return StageOutcome.Complete();
    }
}

/// <summary>A value the user typed that can't be stored, and why.</summary>
public sealed record MetadataProblem(MetadataField Field, string Message);

/// <summary>
/// The inspector's metadata: what an entry's fields show and why, and the user's edits. Every change goes to
/// catalog.db as an assertion state or a rejection, then the entry is projected again. A value the user sets may
/// make a folder rule worth proposing, which <paramref name="rules"/> looks into.
/// </summary>
public sealed class MetadataService(MetadataStore metadata, VocabularyStore vocabularies, MetadataProjector projector, FolderRules rules,
    ChapterScanService chapters)
{
    /// <summary>What the AI found reading the book chapter by chapter, shown apart from its own details.</summary>
    public ChapterScanService Chapters => chapters;

    public async Task<(EffectiveMetadata Metadata, Vocabulary Vocabulary)> GetAsync(EntryId entryId, CancellationToken ct = default) =>
        ((await metadata.GetAsync(entryId, ct)).Compute(), await vocabularies.GetAsync(ct));

    /// <summary>
    /// Saves what the user typed for a field: a list, comma-separated, for multi-value fields. Term fields resolve each
    /// value through the vocabulary, adding the user's own term when nothing matches; publishers take the vocabulary's
    /// spelling when they match one. Returns the problem instead of saving when a value can't be stored.
    /// </summary>
    public async Task<MetadataProblem?> SetAsync(EntryId entryId, MetadataField field, string typed, CancellationToken ct = default)
    {
        var values = new List<string>();
        var added = false;
        foreach (var part in MetadataValues.Split(field, typed))
        {
            var (value, problem, isNew) = await ReadAsync(field, part, ct);
            if (problem is not null) return problem;
            added |= isNew;
            values.Add(value!);
        }
        if (field.Kind == FieldKind.Store) values = WithoutBareDuplicates(values);
        await metadata.SetValuesAsync(entryId, field, values, ct);
        if (added) await projector.ProjectVocabularyAsync(ct);
        await projector.ProjectAsync([entryId], ct);
        if (values.Count > 0) await rules.ConsiderAsync([entryId], [field], ct);
        return null;
    }

    /// <summary>
    /// What was typed for a field in stored form, read as <see cref="SetAsync"/> reads it, without saving: for a value
    /// made elsewhere, such as an import from an online library. A name the vocabulary doesn't know becomes the user's
    /// own term (Added). Returns the problem instead when a value can't be stored.
    /// </summary>
    public async Task<(IReadOnlyList<string> Values, MetadataProblem? Problem, bool Added)> StoredAsync(MetadataField field, string typed,
        CancellationToken ct = default)
    {
        var values = new List<string>();
        var added = false;
        foreach (var part in MetadataValues.Split(field, typed))
        {
            var (value, problem, isNew) = await ReadAsync(field, part, ct);
            if (problem is not null) return ([], problem, added);
            added |= isNew;
            values.Add(value!);
        }
        return (values, null, added);
    }

    /// <summary>
    /// One typed value in stored form. A term field resolves it through the vocabulary, adding the user's own term when
    /// nothing matches (IsNew); a publisher takes the vocabulary's spelling.
    /// </summary>
    async Task<(string? Value, MetadataProblem? Problem, bool IsNew)> ReadAsync(MetadataField field, string part, CancellationToken ct)
    {
        if (Check(field, part) is { } problem) return (null, problem, false);
        if (field.Kind == FieldKind.Store)
        {
            var (term, name, url) = StoreLink.Read(part, await vocabularies.GetAsync(ct), field);
            var key = term?.Key ?? (await vocabularies.ResolveOrAddAsync(field.Vocabulary!, name, ct)).Key;
            return (new StoreLink(key, url).ToString(), null, term is null);
        }
        if (field.Kind == FieldKind.Term)
        {
            var isNew = (await vocabularies.GetAsync(ct)).Resolve(field.Vocabulary!, part) is null;
            return ((await vocabularies.ResolveOrAddAsync(field.Vocabulary!, part, ct)).Key, null, isNew);
        }
        var value = MetadataValues.Parse(field, part).Value!;
        if (field.Vocabulary is { } aliases && (await vocabularies.GetAsync(ct)).Resolve(aliases, value) is { } known) value = known.Label;
        return (value, null, false);
    }

    /// <summary>
    /// A linked field's values, leaving out a place given both bare and with a link: "D&amp;D Beyond" ticked and its link
    /// typed is the link alone.
    /// </summary>
    static List<string> WithoutBareDuplicates(List<string> values)
    {
        var linked = values.Select(StoreLink.Parse).Where(v => v.HasLink).Select(v => v.Store).ToHashSet(StringComparer.Ordinal);
        return [.. values.Where(v => StoreLink.Parse(v) is var parsed && (parsed.HasLink || !linked.Contains(parsed.Store))).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Why a typed value can't be stored in a field, or null when it can.</summary>
    public static MetadataProblem? Check(MetadataField field, string part)
    {
        if (field.Kind == FieldKind.Term)
            return MetadataText.Normalize(part).Length == 0 ? new MetadataProblem(field, $"{field.Label} needs some letters or numbers.") : null;
        return MetadataValues.Parse(field, part).Problem is { } problem ? new MetadataProblem(field, problem) : null;
    }

    /// <summary>"Use this": an alternative becomes the value (single fields) or joins the values (multi-value fields).</summary>
    public async Task UseAsync(EntryId entryId, MetadataField field, string value, CancellationToken ct = default)
    {
        var current = (await metadata.GetAsync(entryId, ct)).Compute()[field];
        IReadOnlyList<string> values = field.Multiple ? [.. current.Values.Select(v => v.Value), value] : [value];
        await metadata.SetValuesAsync(entryId, field, values, ct);
        await projector.ProjectAsync([entryId], ct);
        await rules.ConsiderAsync([entryId], [field], ct);
    }

    public async Task ConfirmAsync(EntryId entryId, MetadataField field, CancellationToken ct = default)
    {
        await metadata.ConfirmAsync(entryId, field, ct);
        await projector.ProjectAsync([entryId], ct);
        await rules.ConsiderAsync([entryId], [field], ct);
    }

    public async Task RejectAsync(EntryId entryId, MetadataField field, string normalized, CancellationToken ct = default)
    {
        await metadata.RejectAsync(entryId, field, normalized, ct);
        await projector.ProjectAsync([entryId], ct);
    }

    public async Task ResetAsync(EntryId entryId, MetadataField field, CancellationToken ct = default)
    {
        await metadata.ResetAsync(entryId, field, ct);
        await projector.ProjectAsync([entryId], ct);
    }

    /// <summary>Several entries' metadata, for the bulk editor. An entry with none yet has <see cref="EffectiveMetadata.Empty"/>.</summary>
    public async Task<(IReadOnlyDictionary<EntryId, EffectiveMetadata> Metadata, Vocabulary Vocabulary)> GetManyAsync(IReadOnlyCollection<EntryId> entryIds,
        CancellationToken ct = default)
    {
        var all = await metadata.GetManyAsync(entryIds, ct);
        return (entryIds.Distinct().ToDictionary(id => id, id => all.TryGetValue(id, out var m) ? m.Compute() : EffectiveMetadata.Empty),
            await vocabularies.GetAsync(ct));
    }

    /// <summary>
    /// A bulk edit: every change on every entry, in one transaction, by the inspector's rules
    /// (<see cref="MetadataStore.ApplyBulkAsync"/>). A Set or Add value is a term's key, or what was typed, which is
    /// read as <see cref="SetAsync"/> reads it: a name the vocabulary doesn't know becomes the user's own term. Remove
    /// values are stored ones, as the field shows them. Every value is checked before anything is saved. Then the
    /// changed entries are projected a batch at a time, <paramref name="progress"/> counting them, so search sees
    /// the new values. Returns the problem, or what undoes the edit.
    /// </summary>
    public async Task<(MetadataProblem? Problem, IReadOnlyList<FieldSnapshot>? Undo)> EditManyAsync(IReadOnlyCollection<EntryId> entryIds,
        IReadOnlyList<BulkChange> changes, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        if (changes.FirstOrDefault(c => c.Action is BulkAction.Set or BulkAction.Add && Check(c.Field, c.Value ?? "") is not null) is { } bad)
            return (Check(bad.Field, bad.Value ?? ""), null);

        var stored = new List<BulkChange>();
        var added = false;
        foreach (var change in changes)
        {
            if (change.Action is BulkAction.Remove or BulkAction.Reset) stored.Add(change);
            else if (change.Field.Kind == FieldKind.Term && (await vocabularies.GetAsync(ct)).Find(change.Field.Vocabulary!, change.Value!) is { } term)
                stored.Add(change with { Value = term.Key });
            else
            {
                var (value, _, isNew) = await ReadAsync(change.Field, change.Value!, ct);
                added |= isNew;
                stored.Add(change with { Value = value });
            }
        }

        var undo = await metadata.ApplyBulkAsync(entryIds, stored, ct);
        if (added) await projector.ProjectVocabularyAsync(ct);
        await ProjectAsync([.. undo.Select(s => s.EntryId).Distinct()], progress, ct);
        var set = stored.Where(c => c.Action is BulkAction.Set or BulkAction.Add).Select(c => c.Field).Distinct().ToList();
        if (set.Count > 0 && undo.Count > 0) await rules.ConsiderAsync([.. undo.Select(s => s.EntryId).Distinct()], set, ct);
        return (null, undo);
    }

    /// <summary>Undoes a bulk edit: every field it changed goes back as it was, in one transaction, and search follows.</summary>
    public async Task UndoManyAsync(IReadOnlyList<FieldSnapshot> undo, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        await metadata.RestoreAsync(undo, ct);
        await ProjectAsync([.. undo.Select(s => s.EntryId).Distinct()], progress, ct);
    }

    /// <summary>Projects entries a batch at a time, reporting how many are done, so a large selection shows how far it has got.</summary>
    async Task ProjectAsync(IReadOnlyList<EntryId> entryIds, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        const int Batch = 100;
        var done = 0;
        foreach (var batch in entryIds.Chunk(Batch))
        {
            await projector.ProjectAsync(batch, ct);
            done += batch.Length;
            progress?.Report((done, entryIds.Count));
        }
    }
}
