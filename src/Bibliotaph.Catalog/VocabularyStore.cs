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
        var slug = Slug(label);
        if (slug.Length == 0) throw new ArgumentException("A term needs some letters or numbers.", nameof(label));

        await using var db = await contexts.CreateDbContextAsync(ct);
        var taken = await db.VocabularyTerms.Where(t => t.Vocabulary == vocabulary && t.Key.StartsWith(slug)).Select(t => t.Key).ToListAsync(ct);
        var key = slug;
        for (var n = 2; taken.Contains(key); n++) key = $"{slug}-{n.ToString(CultureInfo.InvariantCulture)}";
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
