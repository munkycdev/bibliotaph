using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Copies effective metadata from catalog.db into index.db (doc_meta, doc_facet, doc_fts, term_alias), where the
/// library lists, filters and searches it. index.db holds only this projection, never the assertions, so rebuilding it
/// loses nothing: the projection runs again.
/// </summary>
public sealed class MetadataProjector(MetadataStore metadata, VocabularyStore vocabularies, IndexStore index, IndexQueries queries,
    ILogger<MetadataProjector>? log = null)
{
    const int Batch = 200;

    readonly ILogger _log = log ?? NullLogger<MetadataProjector>.Instance;

    /// <summary>Raised after documents' metadata changed in index.db, on a background thread.</summary>
    public event EventHandler<IReadOnlyCollection<long>>? Projected;

    public async Task ProjectAsync(IReadOnlyCollection<long> documentIds, CancellationToken ct = default)
    {
        if (documentIds.Count == 0) return;
        var vocabulary = await vocabularies.GetAsync(ct);
        var all = await metadata.GetManyAsync(documentIds, ct);
        await index.SetMetadataAsync([.. all.Values.Select(m => Build(m, vocabulary))], ct);
        await index.ClearMetadataAsync([.. documentIds.Where(id => !all.ContainsKey(id))], ct);
        Projected?.Invoke(this, documentIds);
    }

    /// <summary>Copies every name of every term into index.db, for field search. Run when a term is added.</summary>
    public async Task ProjectVocabularyAsync(CancellationToken ct = default)
    {
        var vocabulary = await vocabularies.GetAsync(ct);
        await index.SetTermAliasesAsync([.. vocabulary.Aliases.Select(a => new TermAliasRow(a.Term.Vocabulary, a.Alias, a.Term.Key))], ct);
    }

    /// <summary>Projects every document and the vocabulary's aliases. Run at startup, and after the vocabulary changes.</summary>
    public async Task ProjectAllAsync(CancellationToken ct = default)
    {
        await ProjectVocabularyAsync(ct);
        var vocabulary = await vocabularies.GetAsync(ct);
        var (_, withMetadata) = await queries.GetDocumentIdsAsync(ct);
        var all = await metadata.GetManyAsync(null, ct);
        foreach (var chunk in all.Values.Chunk(Batch))
            await index.SetMetadataAsync([.. chunk.Select(m => Build(m, vocabulary))], ct);
        await index.ClearMetadataAsync([.. withMetadata.Where(id => !all.ContainsKey(id))], ct);
        _log.LogInformation("Projected metadata for {Count} documents", all.Count);
        Projected?.Invoke(this, [.. all.Keys]);
    }

    /// <summary>A document's index.db metadata row: its effective values, labelled through the vocabulary.</summary>
    public static DocMetaRow Build(DocumentMetadata document, Vocabulary vocabulary)
    {
        var effective = document.Compute();
        string? One(MetadataField field) => effective[field].First?.Value;
        string? Joined(MetadataField field) => effective[field].Values.Count == 0 ? null : string.Join("; ", effective[field].Values.Select(v => v.Value));

        var facets = new List<DocFacetRow>();
        foreach (var field in MetadataFields.All.Where(f => f.Kind == FieldKind.Term))
            foreach (var value in effective[field].Values)
                facets.Add(new DocFacetRow(field.Key, value.Value, vocabulary.Label(field.Vocabulary!, value.Value), value.Confirmed));

        // An edition implies its system, so filtering by D&D finds books only known to be 5e.
        var edition = effective[MetadataFields.Edition].First;
        var editionTerm = edition is null ? null : vocabulary.Find("edition", edition.Value);
        if (editionTerm?.ParentKey is { } parent && facets.All(f => !(f.Field == "system" && f.Value == parent)))
            facets.Add(new DocFacetRow("system", parent, vocabulary.Label("system", parent), edition!.Confirmed));

        var levels = effective[MetadataFields.Levels].First is { } l && LevelRange.TryParse(l.Value, out var range) ? range : (LevelRange?)null;

        return new DocMetaRow
        {
            DocumentId = document.DocumentId,
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
            NeedsReview = effective.NeedsReview,
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
    static string SearchText(IEnumerable<DocFacetRow> facets, Vocabulary vocabulary) =>
        string.Join(" · ", facets.SelectMany(f => vocabulary.Find(f.Field, f.Value) is { } term ? new[] { term.Label, term.ShortLabel } : [f.Label])
            .Where(t => !string.IsNullOrEmpty(t)).Distinct());
}

/// <summary>
/// Rule hints for documents: reads where the file is and what the PDF says about itself, stores the suggestions in
/// catalog.db and projects the result. Also lists and switches the folder labels Settings shows.
/// </summary>
public sealed class MetadataHints(LibraryStore library, IndexQueries queries, MetadataStore metadata, VocabularyStore vocabularies, MetadataProjector projector)
{
    /// <summary>Re-reads a document's names and replaces its rule-hint suggestions. False when it has no file location left.</summary>
    public async Task<bool> ApplyAsync(long documentId, CancellationToken ct = default)
    {
        if (!await StoreAsync(documentId, ct)) return false;
        await projector.ProjectAsync([documentId], ct);
        return true;
    }

    async Task<bool> StoreAsync(long documentId, CancellationToken ct)
    {
        var path = await library.GetRelativePathAsync(documentId, ct);
        if (path is null) return false;
        var info = await queries.GetEmbeddedInfoAsync(documentId, ct);
        var source = new HintSource(path, info?.Title, info?.Author, info?.Subject, info?.Keywords);
        var proposals = RuleHints.Propose(source, await vocabularies.GetAsync(ct), await vocabularies.GetIgnoredFolderLabelsAsync(ct));
        await metadata.ReplaceHintsAsync(documentId, proposals, ct);
        return true;
    }

    /// <summary>
    /// The vocabulary terms folder names in the library stand for, with how many documents sit under each and whether
    /// the user has switched it off, for Settings > Library folders.
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
        foreach (var documentId in affected) await StoreAsync(documentId, ct);
        await projector.ProjectAsync(affected, ct);
    }
}

/// <summary>A folder name that stands for a term, the documents under it, and whether the label is switched on.</summary>
public sealed record FolderLabelUse(string Folder, Term Term, int Documents, bool Enabled);

/// <summary>Hints from names: folder names, the file name and the PDF's own information as provisional metadata.</summary>
public sealed class RuleHintsStage(MetadataHints hints) : IStage
{
    public Stage Stage => Stage.RuleHints;

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct) =>
        await hints.ApplyAsync(job.DocumentId, ct)
            ? StageOutcome.Complete()
            : new StageOutcome.Done(StageStatus.Skipped, [], "The file has gone, so there are no names to read.");
}

/// <summary>A value the user typed that can't be stored, and why.</summary>
public sealed record MetadataProblem(MetadataField Field, string Message);

/// <summary>
/// The inspector's metadata: what a document's fields show and why, and the user's edits. Every change goes to
/// catalog.db as an assertion state or a rejection, then the document is projected again.
/// </summary>
public sealed class MetadataService(MetadataStore metadata, VocabularyStore vocabularies, MetadataProjector projector)
{
    public async Task<(EffectiveMetadata Metadata, Vocabulary Vocabulary)> GetAsync(long documentId, CancellationToken ct = default) =>
        ((await metadata.GetAsync(documentId, ct)).Compute(), await vocabularies.GetAsync(ct));

    /// <summary>
    /// Saves what the user typed for a field: a list, comma-separated, for multi-value fields. Term fields resolve each
    /// value through the vocabulary, adding the user's own term when nothing matches; publishers take the vocabulary's
    /// spelling when they match one. Returns the problem instead of saving when a value can't be stored.
    /// </summary>
    public async Task<MetadataProblem?> SetAsync(long documentId, MetadataField field, string typed, CancellationToken ct = default)
    {
        var values = new List<string>();
        var added = false;
        foreach (var part in MetadataValues.Split(field, typed))
        {
            if (field.Kind == FieldKind.Term)
            {
                if (MetadataText.Normalize(part).Length == 0) return new MetadataProblem(field, $"{field.Label} needs some letters or numbers.");
                added |= (await vocabularies.GetAsync(ct)).Resolve(field.Vocabulary!, part) is null;
                values.Add((await vocabularies.ResolveOrAddAsync(field.Vocabulary!, part, ct)).Key);
                continue;
            }
            var (value, problem) = MetadataValues.Parse(field, part);
            if (problem is not null) return new MetadataProblem(field, problem);
            if (field.Vocabulary is { } aliases && (await vocabularies.GetAsync(ct)).Resolve(aliases, value!) is { } known) value = known.Label;
            values.Add(value!);
        }
        await metadata.SetValuesAsync(documentId, field, values, ct);
        if (added) await projector.ProjectVocabularyAsync(ct);
        await projector.ProjectAsync([documentId], ct);
        return null;
    }

    /// <summary>"Use this": an alternative becomes the value (single fields) or joins the values (multi-value fields).</summary>
    public async Task UseAsync(long documentId, MetadataField field, string value, CancellationToken ct = default)
    {
        var current = (await metadata.GetAsync(documentId, ct)).Compute()[field];
        IReadOnlyList<string> values = field.Multiple ? [.. current.Values.Select(v => v.Value), value] : [value];
        await metadata.SetValuesAsync(documentId, field, values, ct);
        await projector.ProjectAsync([documentId], ct);
    }

    public async Task ConfirmAsync(long documentId, MetadataField field, CancellationToken ct = default)
    {
        await metadata.ConfirmAsync(documentId, field, ct);
        await projector.ProjectAsync([documentId], ct);
    }

    public async Task RejectAsync(long documentId, MetadataField field, string normalized, CancellationToken ct = default)
    {
        await metadata.RejectAsync(documentId, field, normalized, ct);
        await projector.ProjectAsync([documentId], ct);
    }

    public async Task ResetAsync(long documentId, MetadataField field, CancellationToken ct = default)
    {
        await metadata.ResetAsync(documentId, field, ct);
        await projector.ProjectAsync([documentId], ct);
    }
}
