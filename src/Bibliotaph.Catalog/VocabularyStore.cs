using System.Globalization;
using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// The vocabularies in catalog.db: seeding the starter terms, loading them for matching and labelling, and adding the
/// terms a user types that aren't there yet. The loaded <see cref="Vocabulary"/> is cached until a term changes.
/// </summary>
public sealed class VocabularyStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>The starter vocabulary version catalog.db has been seeded to.</summary>
    public const string SeedVersionKey = "vocabulary.seed";

    const string StarterResource = "Bibliotaph.Catalog.starter-vocabulary.json";

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    volatile Vocabulary? _cached;

    sealed record StarterFile(int Version, List<StarterTerm> Terms);

    sealed record StarterTerm(string Vocabulary, string Key, string Label, string? Short, string? Parent, List<string>? Aliases, int? Since);

    /// <summary>
    /// Adds the starter terms and aliases newer than the stored seed version. A term that is already there keeps its
    /// label and state, whoever changed them; it only gains aliases. Running it again does nothing. Returns the
    /// number of terms added.
    /// </summary>
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        var starter = LoadStarter();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var stored = await db.Settings.Where(s => s.Key == SeedVersionKey).Select(s => s.Value).SingleOrDefaultAsync(ct);
        var seeded = int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0;
        if (seeded >= starter.Version) return 0;

        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await db.VocabularyTerms.Include(t => t.Aliases).ToDictionaryAsync(t => (t.Vocabulary, t.Key), ct);
        var added = 0;
        foreach (var term in starter.Terms.Where(t => (t.Since ?? 1) > seeded))
        {
            if (!existing.TryGetValue((term.Vocabulary, term.Key), out var row))
            {
                row = new VocabularyTerm
                {
                    Vocabulary = term.Vocabulary,
                    Key = term.Key,
                    Label = term.Label,
                    ShortLabel = term.Short,
                    ParentKey = term.Parent,
                    Origin = TermOrigin.Starter,
                    State = TermState.Active,
                    CreatedUtc = now,
                };
                db.VocabularyTerms.Add(row);
                existing[(term.Vocabulary, term.Key)] = row;
                added++;
            }
            foreach (var alias in term.Aliases ?? [])
            {
                var normalized = MetadataText.Normalize(alias);
                if (normalized.Length > 0 && row.Aliases.All(a => a.Normalized != normalized))
                    row.Aliases.Add(new VocabularyAlias { Text = alias, Normalized = normalized });
            }
        }

        var setting = await db.Settings.FindAsync([SeedVersionKey], ct);
        var version = starter.Version.ToString(CultureInfo.InvariantCulture);
        if (setting is null) db.Settings.Add(new Setting { Key = SeedVersionKey, Value = version });
        else setting.Value = version;
        await db.SaveChangesAsync(ct);
        _cached = null;
        return added;
    }

    static StarterFile LoadStarter()
    {
        using var stream = typeof(VocabularyStore).Assembly.GetManifestResourceStream(StarterResource)
            ?? throw new InvalidOperationException($"The starter vocabulary resource {StarterResource} is missing.");
        return JsonSerializer.Deserialize<StarterFile>(stream, JsonOptions)
            ?? throw new InvalidOperationException("The starter vocabulary is empty.");
    }

    /// <summary>The active terms and their aliases. Pending and rejected terms neither match nor label.</summary>
    public async Task<Vocabulary> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } cached) return cached;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.VocabularyTerms.AsNoTracking().Include(t => t.Aliases)
            .Where(t => t.State == TermState.Active)
            .OrderBy(t => t.Origin).ThenBy(t => t.Id)
            .ToListAsync(ct);
        var vocabulary = new Vocabulary(
            rows.Select(t => new Term(t.Vocabulary, t.Key, t.Label, t.ShortLabel, t.ParentKey)),
            rows.SelectMany(t => t.Aliases.Select(a => (t.Vocabulary, t.Key, a.Text))));
        _cached = vocabulary;
        return vocabulary;
    }

    /// <summary>
    /// The term <paramref name="label"/> names in <paramref name="vocabulary"/>, adding it as the user's own term when
    /// nothing matches. Its key is made from the label and never changes after.
    /// </summary>
    public async Task<Term> ResolveOrAddAsync(string vocabulary, string label, CancellationToken ct = default)
    {
        if ((await GetAsync(ct)).Resolve(vocabulary, label) is { } known) return known;
        label = MetadataText.Tidy(label);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var key = await NewKeyAsync(db, vocabulary, label, ct);
        db.VocabularyTerms.Add(new VocabularyTerm
        {
            Vocabulary = vocabulary,
            Key = key,
            Label = label,
            Origin = TermOrigin.User,
            State = TermState.Active,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        _cached = null;
        return new Term(vocabulary, key, label);
    }

    static async Task<string> NewKeyAsync(CatalogDbContext db, string vocabulary, string label, CancellationToken ct)
    {
        var slug = Slug(label);
        if (slug.Length == 0) throw new ArgumentException("A term needs some letters or numbers.", nameof(label));
        var taken = await db.VocabularyTerms.Where(t => t.Vocabulary == vocabulary && t.Key.StartsWith(slug)).Select(t => t.Key).ToListAsync(ct);
        var key = slug;
        for (var n = 2; taken.Contains(key); n++) key = $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}";
        return key;
    }

    // New terms a classifier proposes (choice 4 (b)): pending until the user adds, maps or rejects them in Needs review.

    /// <summary>
    /// The term a classifier's <paramref name="label"/> stands for: an active term it names exactly, else the pending
    /// or rejected term proposed with that name before, else a new pending term. The state says what to do with the
    /// value: use it, hold it until the term is decided, or drop it.
    /// </summary>
    public async Task<(Term Term, TermState State)> ProposeTermAsync(string vocabulary, string label, CancellationToken ct = default)
    {
        if ((await GetAsync(ct)).Resolve(vocabulary, label) is { } known) return (known, TermState.Active);
        label = MetadataText.Tidy(label);
        var normalized = MetadataText.Normalize(label);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var proposed = (await db.VocabularyTerms.AsNoTracking().Where(t => t.Vocabulary == vocabulary && t.State != TermState.Active).ToListAsync(ct))
            .FirstOrDefault(t => MetadataText.Normalize(t.Label) == normalized);
        if (proposed is not null) return (ToTerm(proposed), proposed.State);
        var row = new VocabularyTerm
        {
            Vocabulary = vocabulary,
            Key = await NewKeyAsync(db, vocabulary, label, ct),
            Label = label,
            Origin = TermOrigin.Model,
            State = TermState.Pending,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        };
        db.VocabularyTerms.Add(row);
        await db.SaveChangesAsync(ct);
        return (ToTerm(row), TermState.Pending);
    }

    static Term ToTerm(VocabularyTerm t) => new(t.Vocabulary, t.Key, t.Label, t.ShortLabel, t.ParentKey);

    /// <summary>Pending terms that some document is waiting on, with how many and a quote from one of them.</summary>
    public async Task<IReadOnlyList<PendingTerm>> GetPendingAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var terms = await db.VocabularyTerms.AsNoTracking().Where(t => t.State == TermState.Pending).OrderBy(t => t.Id).ToListAsync(ct);
        var waiting = await db.Assertions.AsNoTracking().Where(a => a.State == AssertionState.AwaitingTerm)
            .Select(a => new { a.Field, a.NormalizedValue, a.DocumentId, a.EvidenceQuote }).ToListAsync(ct);
        var pending = new List<PendingTerm>();
        foreach (var term in terms)
        {
            if (MetadataFields.ForVocabulary(term.Vocabulary) is not { } field) continue;
            var uses = waiting.Where(a => a.Field == field.Key && a.NormalizedValue == term.Key).ToList();
            if (uses.Count == 0) continue;
            pending.Add(new PendingTerm(term.Id, ToTerm(term), field, uses.Select(u => u.DocumentId).Distinct().Count(),
                uses.Select(u => u.EvidenceQuote).FirstOrDefault(q => !string.IsNullOrWhiteSpace(q))));
        }
        return pending;
    }

    /// <summary>Adds a pending term to its vocabulary as it was proposed; the values waiting on it become suggestions.</summary>
    public Task<TermDecision> AcceptPendingAsync(long termId, CancellationToken ct = default) =>
        DecidePendingAsync(termId, target: null, accept: true, ct);

    /// <summary>
    /// Files a pending term under an existing one: its name becomes another name for <paramref name="targetKey"/>,
    /// and the values waiting on it become suggestions of that term.
    /// </summary>
    public Task<TermDecision> MapPendingAsync(long termId, string targetKey, CancellationToken ct = default) =>
        DecidePendingAsync(termId, targetKey, accept: true, ct);

    /// <summary>Rejects a pending term, and the values waiting on it. Proposing the same name again changes nothing.</summary>
    public Task<TermDecision> RejectPendingAsync(long termId, CancellationToken ct = default) =>
        DecidePendingAsync(termId, target: null, accept: false, ct);

    async Task<TermDecision> DecidePendingAsync(long termId, string? target, bool accept, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var term = await db.VocabularyTerms.SingleAsync(t => t.Id == termId, ct);
        if (term.State != TermState.Pending) throw new InvalidOperationException($"The term {term.Key} is not pending.");
        var field = MetadataFields.ForVocabulary(term.Vocabulary) ?? throw new InvalidOperationException($"No field uses the {term.Vocabulary} vocabulary.");
        var rows = await db.Assertions.Where(a => a.State == AssertionState.AwaitingTerm && a.Field == field.Key && a.NormalizedValue == term.Key).ToListAsync(ct);
        var decision = new TermDecision(term.Id, [.. rows.Select(r => new TermDecision.Row(r.Id, r.ValueJson, r.NormalizedValue))],
            [.. rows.Select(r => r.DocumentId).Distinct()]);
        var now = _clock.GetUtcNow().UtcDateTime;

        VocabularyTerm? into = null;
        if (target is not null)
        {
            into = await db.VocabularyTerms.Include(t => t.Aliases)
                .SingleOrDefaultAsync(t => t.Vocabulary == term.Vocabulary && t.Key == target && t.State == TermState.Active, ct)
                ?? throw new ArgumentException($"There is no active {term.Vocabulary} term {target}.", nameof(target));
            var name = MetadataText.Normalize(term.Label);
            if ((await ClaimAsync(db, term.Vocabulary, name, exceptTermId: null, ct)) is null)
            {
                var alias = new VocabularyAlias { Text = term.Label, Normalized = name };
                into.Aliases.Add(alias);
                await db.SaveChangesAsync(ct);
                decision = decision with { AddedAliasId = alias.Id };
            }
        }

        term.State = accept && into is null ? TermState.Active : TermState.Rejected;
        foreach (var row in rows)
        {
            if (into is not null)
            {
                row.ValueJson = JsonSerializer.Serialize(into.Key);
                row.NormalizedValue = into.Key;
            }
            row.State = accept ? AssertionState.Provisional : AssertionState.Rejected;
            row.DecidedUtc = accept ? null : now;
        }
        await db.SaveChangesAsync(ct);
        _cached = null;
        return decision;
    }

    /// <summary>Undoes a decision about a pending term: the term is pending again and its values wait on it again.</summary>
    public async Task UndoAsync(TermDecision decision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var term = await db.VocabularyTerms.SingleAsync(t => t.Id == decision.TermId, ct);
        term.State = TermState.Pending;
        if (decision.AddedAliasId is { } aliasId && await db.VocabularyAliases.FindAsync([aliasId], ct) is { } alias) db.VocabularyAliases.Remove(alias);
        var ids = decision.Rows.Select(r => r.Id).ToList();
        var rows = await db.Assertions.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        foreach (var row in rows)
        {
            var was = decision.Rows.First(r => r.Id == row.Id);
            row.ValueJson = was.ValueJson;
            row.NormalizedValue = was.Normalized;
            row.State = AssertionState.AwaitingTerm;
            row.DecidedUtc = null;
        }
        await db.SaveChangesAsync(ct);
        _cached = null;
    }

    // Settings > Vocabulary: the user's own edits. A name means one term per vocabulary, so a name that is another
    // term's alias moves here, and a name that is another term's own label, short label or key is refused.

    /// <summary>The active terms of a vocabulary with their aliases, by label.</summary>
    public async Task<IReadOnlyList<VocabularyEntry>> ListAsync(string vocabulary, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.VocabularyTerms.AsNoTracking().Include(t => t.Aliases)
            .Where(t => t.Vocabulary == vocabulary && t.State == TermState.Active).ToListAsync(ct);
        return [.. rows.Select(ToEntry).OrderBy(e => e.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    static VocabularyEntry ToEntry(VocabularyTerm t) =>
        new(t.Id, t.Vocabulary, t.Key, t.Label, t.ShortLabel, t.Origin, [.. t.Aliases.OrderBy(a => a.Text, StringComparer.CurrentCultureIgnoreCase).Select(a => new AliasEntry(a.Id, a.Text))]);

    /// <summary>Adds the user's own term, which <see cref="ResolveOrAddAsync"/> can't when the name is another term's alias.</summary>
    public async Task<VocabularyEdit> AddTermAsync(string vocabulary, string label, CancellationToken ct = default)
    {
        label = MetadataText.Tidy(label);
        if (Slug(label).Length == 0) return VocabularyEdit.Refused("A name needs some letters or numbers.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var (problem, note) = await TakeNameAsync(db, vocabulary, label, exceptTermId: null, ct);
        if (problem is not null) return VocabularyEdit.Refused(problem);
        var row = new VocabularyTerm
        {
            Vocabulary = vocabulary,
            Key = await NewKeyAsync(db, vocabulary, label, ct),
            Label = label,
            Origin = TermOrigin.User,
            State = TermState.Active,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        };
        db.VocabularyTerms.Add(row);
        await db.SaveChangesAsync(ct);
        _cached = null;
        return new VocabularyEdit(row.Id, null, note);
    }

    /// <summary>
    /// Renames a term (its key, which documents store, stays). The old label becomes another name for it, so folders
    /// and searches that used it still find it. An empty <paramref name="shortLabel"/> clears it.
    /// </summary>
    public async Task<VocabularyEdit> RenameAsync(long termId, string label, string? shortLabel, CancellationToken ct = default)
    {
        label = MetadataText.Tidy(label);
        shortLabel = string.IsNullOrWhiteSpace(shortLabel) ? null : MetadataText.Tidy(shortLabel);
        if (Slug(label).Length == 0) return VocabularyEdit.Refused("A name needs some letters or numbers.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var term = await db.VocabularyTerms.Include(t => t.Aliases).SingleAsync(t => t.Id == termId, ct);
        var notes = new List<string>();
        foreach (var name in new[] { label, shortLabel }.OfType<string>())
        {
            var (problem, note) = await TakeNameAsync(db, term.Vocabulary, name, term.Id, ct);
            if (problem is not null) return VocabularyEdit.Refused(problem);
            if (note is not null) notes.Add(note);
        }
        var oldLabel = term.Label;
        term.Label = label;
        term.ShortLabel = shortLabel;
        // The new names were aliases of this term; as its names they need no alias rows.
        db.VocabularyAliases.RemoveRange(term.Aliases.Where(a => a.Normalized == MetadataText.Normalize(label) || (shortLabel is not null && a.Normalized == MetadataText.Normalize(shortLabel))));
        var old = MetadataText.Normalize(oldLabel);
        if (old != MetadataText.Normalize(label) && old != MetadataText.Normalize(shortLabel ?? "") && term.Aliases.All(a => a.Normalized != old))
        {
            term.Aliases.Add(new VocabularyAlias { Text = oldLabel, Normalized = old });
            notes.Add($"“{oldLabel}” stays as another name for it.");
        }
        await db.SaveChangesAsync(ct);
        _cached = null;
        return new VocabularyEdit(term.Id, null, notes.Count == 0 ? null : string.Join(" ", notes));
    }

    /// <summary>Adds another name for a term, moving it from the term that had it as an alias, if any.</summary>
    public async Task<VocabularyEdit> AddAliasAsync(long termId, string text, CancellationToken ct = default)
    {
        text = MetadataText.Tidy(text);
        var normalized = MetadataText.Normalize(text);
        if (normalized.Length == 0) return VocabularyEdit.Refused("A name needs some letters or numbers.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var term = await db.VocabularyTerms.Include(t => t.Aliases).SingleAsync(t => t.Id == termId, ct);
        if (Names(term).Contains(normalized) || term.Aliases.Any(a => a.Normalized == normalized))
            return VocabularyEdit.Refused($"“{text}” is already a name for {term.Label}.");
        var (problem, note) = await TakeNameAsync(db, term.Vocabulary, text, term.Id, ct);
        if (problem is not null) return VocabularyEdit.Refused(problem);
        term.Aliases.Add(new VocabularyAlias { Text = text, Normalized = normalized });
        await db.SaveChangesAsync(ct);
        _cached = null;
        return new VocabularyEdit(term.Id, null, note);
    }

    public async Task RemoveAliasAsync(long aliasId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.VocabularyAliases.FindAsync([aliasId], ct) is not { } alias) return;
        db.VocabularyAliases.Remove(alias);
        await db.SaveChangesAsync(ct);
        _cached = null;
    }

    /// <summary>The names a term answers to besides its aliases, in comparison form.</summary>
    static HashSet<string> Names(VocabularyTerm term) =>
        [.. new[] { term.Label, term.ShortLabel, term.Key }.OfType<string>().Select(MetadataText.Normalize).Where(n => n.Length > 0)];

    /// <summary>The active term in <paramref name="vocabulary"/>, other than <paramref name="exceptTermId"/>, that answers to a name, and how.</summary>
    static async Task<(VocabularyTerm Term, VocabularyAlias? Alias)?> ClaimAsync(CatalogDbContext db, string vocabulary, string normalized, long? exceptTermId, CancellationToken ct)
    {
        var terms = await db.VocabularyTerms.Include(t => t.Aliases)
            .Where(t => t.Vocabulary == vocabulary && t.State == TermState.Active && t.Id != exceptTermId).ToListAsync(ct);
        if (terms.FirstOrDefault(t => Names(t).Contains(normalized)) is { } named) return (named, null);
        foreach (var term in terms)
            if (term.Aliases.FirstOrDefault(a => a.Normalized == normalized) is { } alias) return (term, alias);
        return null;
    }

    /// <summary>
    /// Frees <paramref name="name"/> for a term: refused when another term is called that, and taken from another
    /// term that has it as an alias (with a note saying so). The caller saves.
    /// </summary>
    static async Task<(string? Problem, string? Note)> TakeNameAsync(CatalogDbContext db, string vocabulary, string name, long? exceptTermId, CancellationToken ct)
    {
        if (await ClaimAsync(db, vocabulary, MetadataText.Normalize(name), exceptTermId, ct) is not { } claim) return (null, null);
        var (other, alias) = claim;
        if (alias is null) return ($"“{name}” is already the name of {other.Label}.", null);
        db.VocabularyAliases.Remove(alias);
        return (null, $"“{alias.Text}” was another name for {other.Label}; now it isn't.");
    }

    /// <summary>"Warhammer Fantasy Roleplay" becomes "warhammer-fantasy-roleplay"; "D&amp;D" becomes "d-and-d".</summary>
    internal static string Slug(string label) =>
        string.Join('-', MetadataText.Words(MetadataText.Normalize(label).Replace("&", " and ", StringComparison.Ordinal).Replace("+", " plus ", StringComparison.Ordinal)));

    /// <summary>Folder labels the user switched off: (folder in comparison form, vocabulary, term key).</summary>
    public async Task<IReadOnlySet<(string Folder, string Vocabulary, string Key)>> GetIgnoredFolderLabelsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.IgnoredFolderLabels.AsNoTracking().Select(l => new { l.Folder, l.Vocabulary, l.TermKey }).ToListAsync(ct);
        return rows.Select(r => (r.Folder, r.Vocabulary, r.TermKey)).ToHashSet();
    }

    /// <summary>Switches a folder label off (<paramref name="enabled"/> false) or back on. Returns whether anything changed.</summary>
    public async Task<bool> SetFolderLabelEnabledAsync(string folder, string vocabulary, string key, bool enabled, CancellationToken ct = default)
    {
        var normalized = MetadataText.Normalize(folder);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.IgnoredFolderLabels.FirstOrDefaultAsync(l => l.Folder == normalized && l.Vocabulary == vocabulary && l.TermKey == key, ct);
        if (enabled == (row is null)) return false;
        if (row is null)
            db.IgnoredFolderLabels.Add(new IgnoredFolderLabel { Folder = normalized, Vocabulary = vocabulary, TermKey = key, CreatedUtc = _clock.GetUtcNow().UtcDateTime });
        else
            db.IgnoredFolderLabels.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>A pending term in Needs review: the field it would fill, how many documents wait on it, and a quote.</summary>
public sealed record PendingTerm(long Id, Term Term, MetadataField Field, int Documents, string? Example);

/// <summary>What deciding a pending term changed, so it can be undone, and the documents to project again.</summary>
public sealed record TermDecision(long TermId, IReadOnlyList<TermDecision.Row> Rows, IReadOnlyList<long> DocumentIds, long? AddedAliasId = null)
{
    public sealed record Row(long Id, string ValueJson, string Normalized);
}

/// <summary>A term as Settings > Vocabulary lists it.</summary>
public sealed record VocabularyEntry(long Id, string Vocabulary, string Key, string Label, string? ShortLabel, TermOrigin Origin, IReadOnlyList<AliasEntry> Aliases);

public sealed record AliasEntry(long Id, string Text);

/// <summary>
/// The result of an edit in Settings > Vocabulary: the term it changed, or why it was refused, and a note about
/// anything else it changed ("“one shot” was another name for Adventure; now it isn't.").
/// </summary>
public sealed record VocabularyEdit(long? TermId, string? Problem, string? Note)
{
    public static VocabularyEdit Refused(string problem) => new(null, problem, null);
}
