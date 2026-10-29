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

    /// <summary>Drops the loaded vocabulary after terms were changed outside this store (library sync's merge), so the next load reads them.</summary>
    internal void Forget() => _cached = null;

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
    /// Every term's label by its vocabulary and key, pending and rejected ones too: what a suggestion names, in words
    /// another device can resolve to its own key for the term.
    /// </summary>
    public async Task<IReadOnlyDictionary<(string Vocabulary, string Key), string>> GetLabelsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return (await db.VocabularyTerms.AsNoTracking().Select(t => new { t.Vocabulary, t.Key, t.Label }).ToListAsync(ct))
            .ToDictionary(t => (t.Vocabulary, t.Key), t => t.Label);
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

    // New terms a classifier proposes: pending until the user adds, maps or rejects them in Needs review.

    /// <summary>
    /// The term a classifier's <paramref name="label"/> stands for: an active term it names exactly, else the pending
    /// or rejected term proposed with that name before, else a new pending term. The state says what to do with the
    /// value: use it, hold it until the term is decided, or drop it.
    /// </summary>
    public async Task<(Term Term, TermState State)> ProposeTermAsync(string vocabulary, string label, CancellationToken ct = default)
    {
        var (term, state, _) = await ProposeTermAsync(vocabulary, label, addAtOnce: false, ct);
        return (term, state);
    }

    /// <summary>
    /// As <see cref="ProposeTermAsync(string, string, CancellationToken)"/>, but with <paramref name="addAtOnce"/> a
    /// new term joins the vocabulary as the AI uses it, and one still pending is added now; a rejected one stays
    /// rejected. <c>Added</c> says the vocabulary just grew.
    /// </summary>
    public async Task<(Term Term, TermState State, bool Added)> ProposeTermAsync(string vocabulary, string label, bool addAtOnce, CancellationToken ct = default)
    {
        if ((await GetAsync(ct)).Resolve(vocabulary, label) is { } known) return (known, TermState.Active, false);
        label = MetadataText.Tidy(label);
        var normalized = MetadataText.Normalize(label);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var proposed = (await db.VocabularyTerms.AsNoTracking().Where(t => t.Vocabulary == vocabulary && t.State != TermState.Active).ToListAsync(ct))
            .FirstOrDefault(t => MetadataText.Normalize(t.Label) == normalized);
        if (proposed is not null)
        {
            if (proposed.State != TermState.Pending || !addAtOnce) return (ToTerm(proposed), proposed.State, false);
            await DecidePendingAsync(proposed.Id, target: null, accept: true, ct);
            return (ToTerm(proposed), TermState.Active, true);
        }
        var row = new VocabularyTerm
        {
            Vocabulary = vocabulary,
            Key = await NewKeyAsync(db, vocabulary, label, ct),
            Label = label,
            Origin = TermOrigin.Model,
            State = addAtOnce ? TermState.Active : TermState.Pending,
            CreatedUtc = _clock.GetUtcNow().UtcDateTime,
        };
        db.VocabularyTerms.Add(row);
        await db.SaveChangesAsync(ct);
        if (addAtOnce) _cached = null;
        return (ToTerm(row), row.State, addAtOnce);
    }

    static Term ToTerm(VocabularyTerm t) => new(t.Vocabulary, t.Key, t.Label, t.ShortLabel, t.ParentKey);

    /// <summary>Pending terms that some document is waiting on, with how many and a quote from one of them.</summary>
    public async Task<IReadOnlyList<PendingTerm>> GetPendingAsync(CancellationToken ct = default) => await GetPendingAsync(null, ct);

    /// <summary>
    /// The pending terms with values waiting on them among <paramref name="among"/> (every entry when null), each
    /// counting only those entries: a term proposed only for books in a removed folder isn't waiting on anyone.
    /// </summary>
    public async Task<IReadOnlyList<PendingTerm>> GetPendingAsync(IReadOnlyCollection<EntryId>? among, CancellationToken ct = default)
    {
        var shown = among?.Select(e => e.Value).ToHashSet();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var terms = await db.VocabularyTerms.AsNoTracking().Where(t => t.State == TermState.Pending).OrderBy(t => t.Id).ToListAsync(ct);
        var waiting = await db.Assertions.AsNoTracking().Where(a => a.State == AssertionState.AwaitingTerm)
            .Select(a => new { a.Field, a.NormalizedValue, a.EntryId, a.EvidenceQuote }).ToListAsync(ct);
        var pending = new List<PendingTerm>();
        foreach (var term in terms)
        {
            if (MetadataFields.ForVocabulary(term.Vocabulary) is not { } field) continue;
            var uses = waiting.Where(a => a.Field == field.Key && a.NormalizedValue == term.Key && (shown is null || shown.Contains(a.EntryId))).ToList();
            if (uses.Count == 0) continue;
            pending.Add(new PendingTerm(term.Id, ToTerm(term), field, uses.Select(u => u.EntryId).Distinct().Count(),
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
            [.. rows.Select(r => new EntryId(r.EntryId)).Distinct()]);
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

    // Find overlaps and Combine: terms that look like the same thing, folded into one.

    /// <summary>The active terms of a vocabulary as the overlap check sees them: every name, and whether they came with Bibliotaph.</summary>
    public async Task<IReadOnlyList<OverlapTerm>> GetOverlapTermsAsync(string vocabulary, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.VocabularyTerms.AsNoTracking().Include(t => t.Aliases)
            .Where(t => t.Vocabulary == vocabulary && t.State == TermState.Active).OrderBy(t => t.Id).ToListAsync(ct);
        return [.. rows.Select(t => new OverlapTerm(t.Key, t.Label, t.ShortLabel, [.. t.Aliases.Select(a => a.Text)], t.Origin == TermOrigin.Starter))];
    }

    /// <summary>How many books have each term of a vocabulary as a value, suggested or kept, by key; terms no book has are left out.</summary>
    public async Task<IReadOnlyDictionary<string, int>> CountBooksAsync(string vocabulary, CancellationToken ct = default)
    {
        if (MetadataFields.ForVocabulary(vocabulary) is not { } field) return new Dictionary<string, int>(StringComparer.Ordinal);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.Assertions.AsNoTracking()
            .Where(a => a.Field == field.Key && (a.State == AssertionState.Provisional || a.State == AssertionState.Confirmed))
            .Select(a => new { a.NormalizedValue, a.EntryId }).Distinct().ToListAsync(ct);
        return rows.GroupBy(r => r.NormalizedValue, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    /// <summary>The pairs of a vocabulary the user said are not the same thing.</summary>
    public async Task<IReadOnlyList<KeptApart>> GetKeptApartAsync(string vocabulary, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.VocabularyKeptAparts.AsNoTracking().Where(k => k.Vocabulary == vocabulary).OrderBy(k => k.Id)
            .Select(k => new KeptApart(k.Id, k.FirstKey, k.SecondKey)).ToListAsync(ct);
    }

    /// <summary>Remembers that these terms are not the same thing, every pair of them, so Find overlaps leaves them alone. Returns the rows added.</summary>
    public async Task<IReadOnlyList<long>> KeepApartAsync(string vocabulary, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var known = await db.VocabularyKeptAparts.Where(k => k.Vocabulary == vocabulary).Select(k => new { k.FirstKey, k.SecondKey }).ToListAsync(ct);
        var added = new List<VocabularyKeptApart>();
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var (first, second) in keys.SelectMany((a, i) => keys.Skip(i + 1).Select(b => string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a))).Distinct())
        {
            if (first == second || known.Any(k => k.FirstKey == first && k.SecondKey == second)) continue;
            var row = new VocabularyKeptApart { Vocabulary = vocabulary, FirstKey = first, SecondKey = second, CreatedUtc = now };
            db.VocabularyKeptAparts.Add(row);
            added.Add(row);
        }
        await db.SaveChangesAsync(ct);
        return [.. added.Select(r => r.Id)];
    }

    public async Task ForgetKeptApartAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.VocabularyKeptAparts.Where(k => ids.Contains(k.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Combines terms: every name of each source becomes a name of the target, every book value, rejection and folder
    /// rule with a source's key gets the target's, editions of a source system move to the target, and each source is
    /// marked merged. A book with a source and the target from the same origin keeps one row, the stronger. What
    /// <see cref="UndoMergeAsync"/> needs comes back.
    /// </summary>
    public async Task<TermMerge> MergeAsync(long targetId, IReadOnlyList<long> sourceIds, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var target = await db.VocabularyTerms.Include(t => t.Aliases).SingleOrDefaultAsync(t => t.Id == targetId && t.State == TermState.Active, ct)
            ?? throw new ArgumentException("There is no such active term to combine into.", nameof(targetId));
        var sources = await db.VocabularyTerms.Include(t => t.Aliases)
            .Where(t => sourceIds.Contains(t.Id) && t.Id != targetId && t.Vocabulary == target.Vocabulary && t.State == TermState.Active).ToListAsync(ct);
        if (sources.Count == 0) throw new ArgumentException("There are no such active terms to combine.", nameof(sourceIds));
        var merge = await MergeAsync(db, target, sources, ct);
        _cached = null;
        return merge;
    }

    /// <summary>
    /// Combines <paramref name="sourceKey"/> into <paramref name="targetKey"/> as another of the user's devices did;
    /// nothing happens when either isn't an active term here. Returns whether anything changed.
    /// </summary>
    public async Task<bool> MergeByKeyAsync(string vocabulary, string targetKey, string sourceKey, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var target = await db.VocabularyTerms.Include(t => t.Aliases)
            .SingleOrDefaultAsync(t => t.Vocabulary == vocabulary && t.Key == targetKey && t.State == TermState.Active, ct);
        var source = await db.VocabularyTerms.Include(t => t.Aliases)
            .SingleOrDefaultAsync(t => t.Vocabulary == vocabulary && t.Key == sourceKey && t.State == TermState.Active, ct);
        if (target is null || source is null || target.Id == source.Id) return false;
        await MergeAsync(db, target, [source], ct);
        _cached = null;
        return true;
    }

    async Task<TermMerge> MergeAsync(CatalogDbContext db, VocabularyTerm target, List<VocabularyTerm> sources, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var field = MetadataFields.ForVocabulary(target.Vocabulary);
        var sourceKeys = sources.Select(s => s.Key).ToList();
        var merged = new List<MergedTerm>();
        var addedAliases = new List<VocabularyAlias>();
        var assertions = new List<TermMerge.AssertionWas>();
        var rejections = new List<TermMerge.RejectionWas>();
        var rules = new List<TermMerge.RuleWas>();
        var reparented = new List<(long, string)>();
        var entries = new HashSet<long>();

        foreach (var source in sources)
        {
            merged.Add(new MergedTerm(source.Id, source.Key, source.Label, [.. source.Aliases.Select(a => a.Text)]));
            var taken = Names(target).Concat(target.Aliases.Select(a => a.Normalized)).ToHashSet(StringComparer.Ordinal);
            foreach (var name in source.Aliases.Select(a => a.Text).Prepend(source.ShortLabel).Prepend(source.Label).OfType<string>())
            {
                var normalized = MetadataText.Normalize(name);
                if (normalized.Length == 0 || !taken.Add(normalized)) continue;
                var alias = new VocabularyAlias { Text = name, Normalized = normalized };
                target.Aliases.Add(alias);
                addedAliases.Add(alias);
            }
            db.VocabularyAliases.RemoveRange(source.Aliases);
            source.Aliases.Clear();
            source.State = TermState.Merged;
            source.MergedIntoKey = target.Key;
        }

        if (field is not null)
        {
            var keys = sourceKeys.Append(target.Key).ToList();
            var rows = await db.Assertions.Where(a => a.Field == field.Key && keys.Contains(a.NormalizedValue)).ToListAsync(ct);
            foreach (var byEntry in rows.GroupBy(a => a.EntryId))
            {
                var kept = byEntry.Where(a => a.NormalizedValue == target.Key).ToList();
                foreach (var row in byEntry.Where(a => a.NormalizedValue != target.Key))
                {
                    entries.Add(row.EntryId);
                    var twin = kept.FirstOrDefault(k => k.Origin == row.Origin && k.State != AssertionState.Superseded);
                    if (twin is not null && twin.State != AssertionState.Rejected)
                    {
                        // Both say the same thing now: the weaker row steps aside, the stronger stays as it was.
                        var weaker = Rank(row.State) > Rank(twin.State) ? twin : row;
                        assertions.Add(new TermMerge.AssertionWas(weaker.Id, row.NormalizedValue, weaker.ValueJson, weaker.NormalizedValue, weaker.State, weaker.DecidedUtc));
                        if (weaker == twin)
                        {
                            assertions.Add(new TermMerge.AssertionWas(row.Id, row.NormalizedValue, row.ValueJson, row.NormalizedValue, row.State, row.DecidedUtc));
                            row.ValueJson = JsonSerializer.Serialize(target.Key);
                            row.NormalizedValue = target.Key;
                            kept.Add(row);
                        }
                        weaker.State = AssertionState.Superseded;
                        weaker.DecidedUtc = now;
                        continue;
                    }
                    assertions.Add(new TermMerge.AssertionWas(row.Id, row.NormalizedValue, row.ValueJson, row.NormalizedValue, row.State, row.DecidedUtc));
                    row.ValueJson = JsonSerializer.Serialize(target.Key);
                    row.NormalizedValue = target.Key;
                    kept.Add(row);
                }
            }

            // A rejection of a source name becomes one of the target, unless the book keeps the target: the user said no
            // to one name of the thing and yes to another, and the value they chose stays.
            var keeps = rows.Where(a => a.NormalizedValue == target.Key && a.State == AssertionState.Confirmed).Select(a => a.EntryId).ToHashSet();
            var rejected = await db.Rejections.Where(r => r.Field == field.Key && keys.Contains(r.NormalizedValue)).ToListAsync(ct);
            foreach (var row in rejected.Where(r => r.NormalizedValue != target.Key))
            {
                entries.Add(row.EntryId);
                rejections.Add(new TermMerge.RejectionWas(row.Id, row.EntryId, row.NormalizedValue, row.CreatedUtc));
                if (keeps.Contains(row.EntryId) || rejected.Any(r => r.EntryId == row.EntryId && r.NormalizedValue == target.Key)) db.Rejections.Remove(row);
                else row.NormalizedValue = target.Key;
            }

            var folderRules = await db.FolderRules.Where(r => r.Field == field.Key && keys.Contains(r.Value)).ToListAsync(ct);
            foreach (var rule in folderRules.Where(r => r.Value != target.Key))
            {
                rules.Add(new TermMerge.RuleWas(rule.Id, rule.Value, rule.SourceRootId, rule.Folder, rule.State, rule.Books, rule.CreatedUtc, rule.DecidedUtc));
                if (folderRules.Any(r => r.SourceRootId == rule.SourceRootId && r.Folder == rule.Folder && r.Value == target.Key)) db.FolderRules.Remove(rule);
                else rule.Value = target.Key;
            }
        }

        foreach (var edition in await db.VocabularyTerms.Where(t => t.ParentKey != null && sourceKeys.Contains(t.ParentKey)).ToListAsync(ct))
        {
            reparented.Add((edition.Id, edition.ParentKey!));
            edition.ParentKey = target.Key;
        }

        await db.SaveChangesAsync(ct);
        return new TermMerge(target.Id, target.Key, target.Label, target.Vocabulary, field?.Key, merged, [.. addedAliases.Select(a => a.Id)], assertions, rejections, rules,
            reparented, [.. entries.Select(e => new EntryId(e))]);
    }

    static int Rank(AssertionState state) => state switch
    {
        AssertionState.Confirmed => 3,
        AssertionState.Provisional => 2,
        AssertionState.AwaitingTerm => 1,
        _ => 0,
    };

    /// <summary>Puts combined terms back: their names, their books' values, rejections, folder rules and editions as they were.</summary>
    public async Task UndoMergeAsync(TermMerge merge, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.VocabularyAliases.Where(a => merge.AddedAliasIds.Contains(a.Id)).ExecuteDeleteAsync(ct);
        var sources = await db.VocabularyTerms.Include(t => t.Aliases).Where(t => merge.Merged.Select(m => m.TermId).Contains(t.Id)).ToListAsync(ct);
        foreach (var source in sources)
        {
            var was = merge.Merged.First(m => m.TermId == source.Id);
            source.State = TermState.Active;
            source.MergedIntoKey = null;
            foreach (var text in was.Aliases.Where(t => source.Aliases.All(a => a.Text != t)))
                source.Aliases.Add(new VocabularyAlias { Text = text, Normalized = MetadataText.Normalize(text) });
        }
        var ids = merge.Assertions.Select(a => a.Id).ToList();
        foreach (var row in await db.Assertions.Where(a => ids.Contains(a.Id)).ToListAsync(ct))
        {
            var was = merge.Assertions.First(a => a.Id == row.Id);
            row.ValueJson = was.ValueJson;
            row.NormalizedValue = was.Normalized;
            row.State = was.State;
            row.DecidedUtc = was.DecidedUtc;
        }
        if (merge.Field is { } field)
        {
            var rejectionIds = merge.Rejections.Select(r => r.Id).ToList();
            var rejections = await db.Rejections.Where(r => rejectionIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var was in merge.Rejections)
            {
                if (rejections.TryGetValue(was.Id, out var row)) row.NormalizedValue = was.Normalized;
                else db.Rejections.Add(new Rejection { EntryId = was.EntryId, Field = field, NormalizedValue = was.Normalized, CreatedUtc = was.CreatedUtc });
            }
            var ruleIds = merge.Rules.Select(r => r.Id).ToList();
            var rules = await db.FolderRules.Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
            foreach (var was in merge.Rules)
            {
                if (rules.TryGetValue(was.Id, out var rule)) rule.Value = was.Value;
                else db.FolderRules.Add(new FolderRule
                {
                    SourceRootId = was.SourceRootId, Folder = was.Folder, Field = field, Value = was.Value, State = was.State, Books = was.Books,
                    CreatedUtc = was.CreatedUtc, DecidedUtc = was.DecidedUtc,
                });
            }
        }
        var editionIds = merge.Reparented.Select(r => r.TermId).ToList();
        foreach (var edition in await db.VocabularyTerms.Where(t => editionIds.Contains(t.Id)).ToListAsync(ct))
            edition.ParentKey = merge.Reparented.First(r => r.TermId == edition.Id).ParentKey;
        await db.SaveChangesAsync(ct);
        _cached = null;
    }

    /// <summary>"Warhammer Fantasy Roleplay" becomes "warhammer-fantasy-roleplay"; "D&amp;D" becomes "d-and-d".</summary>
    internal static string Slug(string label) =>
        string.Join('-', MetadataText.Words(MetadataText.Normalize(label).Replace("&", " and ", StringComparison.Ordinal).Replace("+", " plus ", StringComparison.Ordinal)));
}

/// <summary>Two terms of a vocabulary the user said are not the same thing, by key.</summary>
public sealed record KeptApart(long Id, string FirstKey, string SecondKey);

/// <summary>A term combined into another: what Undo gives back.</summary>
public sealed record MergedTerm(long TermId, string Key, string Label, IReadOnlyList<string> Aliases);

/// <summary>
/// What combining terms changed, enough to put it all back: the target, the terms merged into it with the names
/// they had, the names the target gained, and every book value, rejection, folder rule and edition re-pointed.
/// </summary>
public sealed record TermMerge(long TargetId, string TargetKey, string TargetLabel, string Vocabulary, string? Field, IReadOnlyList<MergedTerm> Merged,
    IReadOnlyList<long> AddedAliasIds, IReadOnlyList<TermMerge.AssertionWas> Assertions, IReadOnlyList<TermMerge.RejectionWas> Rejections,
    IReadOnlyList<TermMerge.RuleWas> Rules, IReadOnlyList<(long TermId, string ParentKey)> Reparented, IReadOnlyList<EntryId> EntryIds)
{
    /// <summary>A book value as it was: its row, the source key it came from, and its state.</summary>
    public sealed record AssertionWas(long Id, string FromKey, string ValueJson, string Normalized, AssertionState State, DateTime? DecidedUtc);

    /// <summary>A rejection as it was; one the target already had alongside was removed and is added back.</summary>
    public sealed record RejectionWas(long Id, long EntryId, string Normalized, DateTime CreatedUtc);

    /// <summary>A folder rule as it was; one the target already had for the folder was removed and is added back.</summary>
    public sealed record RuleWas(long Id, string Value, long SourceRootId, string Folder, FolderRuleState State, int Books, DateTime CreatedUtc, DateTime? DecidedUtc);
}

/// <summary>A pending term in Needs review: the field it would fill, how many documents wait on it, and a quote.</summary>
public sealed record PendingTerm(long Id, Term Term, MetadataField Field, int Documents, string? Example);

/// <summary>What deciding a pending term changed, so it can be undone, and the entries to project again.</summary>
public sealed record TermDecision(long TermId, IReadOnlyList<TermDecision.Row> Rows, IReadOnlyList<EntryId> EntryIds, long? AddedAliasId = null)
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
