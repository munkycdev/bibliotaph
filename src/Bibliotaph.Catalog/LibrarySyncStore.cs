using System.Globalization;
using System.Text.Json;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>A metadata field's value in the sync file: the values the user kept (stored form) and rejected (normalised), each in order.</summary>
public sealed record SyncedMetadata(IReadOnlyList<string> Values, IReadOnlyList<string> Rejected);

/// <summary>Pages in the sync file: zero-based PDF pages of the file with this content hash, with the labels and fingerprints that find them in another version.</summary>
public sealed record SyncedPages(string Hash, int First, int Last, string? FirstLabel = null, string? LastLabel = null, string? FirstFingerprint = null,
    string? LastFingerprint = null);

/// <summary>
/// The user's work in catalog.db as library sync records (library sync plan, choice 5): each record's fields by name,
/// as compact JSON. <see cref="Cards"/> finds a card by any of its keys: a hash of any of its files, any of a pack's
/// images, or the sync id of its entry ("e:…"). <see cref="Aliases"/> are a card's keys besides its own.
/// </summary>
public sealed record SyncSnapshot(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Records,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases,
    IReadOnlyDictionary<string, long> Cards,
    IReadOnlyDictionary<long, string> CardKeys)
{
    /// <summary>The entry a key names here, by its own key or any alias of it.</summary>
    public long? FindCard(string key) => Cards.TryGetValue(key, out var id) ? id : null;

    /// <summary>The key this catalog names the card <paramref name="key"/> stands for, if it has it.</summary>
    public string? LocalKey(string key) => FindCard(key) is { } id ? CardKeys.GetValueOrDefault(id) : null;
}

/// <summary>
/// What library sync reads and writes in catalog.db (library sync plan): the user's work as sync records, what this PC
/// last knew of each record (sync_state), and merged records applied back to the catalog's own tables. It never touches
/// file locations, documents, roots, pack decisions, runs or settings: those belong to this PC.
/// </summary>
public sealed partial class LibrarySyncStore(IDbContextFactory<CatalogDbContext> contexts, MetadataStore metadata, VocabularyStore vocabularies,
    TimeProvider? clock = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    static string Json<T>(T value) => SyncJson.Value(value);

    static string Id(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>Reads the user's work, in one transaction so the records agree with each other.</summary>
    public async Task<SyncSnapshot> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var records = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var cards = new Dictionary<string, long>(StringComparer.Ordinal);
        var cardKeys = new Dictionary<long, string>();

        // Cards: a book by the lowest hash of its files, a pack by the lowest of its images', a book owned elsewhere by its sync id.
        var entries = await db.Entries.AsNoTracking().Where(e => e.MergedIntoEntryId == null)
            .Select(e => new { e.Id, e.Kind, e.SyncId, e.ParentEntryId }).ToListAsync(ct);
        var hashes = (await db.EntrySources.AsNoTracking().Where(s => s.FirstPdfPage == null)
            .Select(s => new { s.EntryId, s.Document.ContentHash }).ToListAsync(ct)).ToLookup(s => s.EntryId, s => s.ContentHash);
        var members = entries.Where(e => e.ParentEntryId != null).ToLookup(e => e.ParentEntryId!.Value, e => e.Id);
        var cardFields = new Dictionary<long, Dictionary<string, string>>();
        foreach (var entry in entries)
        {
            cards[SyncKeys.For(SyncKeys.Elsewhere, entry.SyncId)] = entry.Id;
            List<string> keys = entry.Kind switch
            {
                EntryKind.Pack => [.. members[entry.Id].SelectMany(m => hashes[m]).Distinct().Order(StringComparer.Ordinal).Select(SyncKeys.ForPack)],
                EntryKind.Part => [],
                _ => [.. hashes[entry.Id].Distinct().Order(StringComparer.Ordinal).Select(SyncKeys.ForBook)],
            };
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (keys.Count == 0)
            {
                if (entry.Kind != EntryKind.Elsewhere) continue;
                keys.Add(SyncKeys.For(SyncKeys.Elsewhere, entry.SyncId));
                fields[SyncFields.Exists] = Json(true);
            }
            foreach (var key in keys) cards[key] = entry.Id;
            cardKeys[entry.Id] = keys[0];
            aliases[keys[0]] = keys[1..];
            cardFields[entry.Id] = fields;
        }

        // The user's decisions about each card's metadata: what they kept, with the text they kept, and what they rejected.
        var kept = await db.Assertions.AsNoTracking().Where(a => a.State == AssertionState.Confirmed)
            .Select(a => new { a.EntryId, a.Field, a.ValueJson, a.NormalizedValue, a.Origin, a.DecidedUtc }).ToListAsync(ct);
        var rejected = await db.Rejections.AsNoTracking().Select(r => new { r.EntryId, r.Field, r.NormalizedValue }).ToListAsync(ct);
        var decided = kept.Select(k => (k.EntryId, k.Field)).Union(rejected.Select(r => (r.EntryId, r.Field)));
        var keptBy = kept.ToLookup(k => (k.EntryId, k.Field));
        var rejectedBy = rejected.ToLookup(r => (r.EntryId, r.Field), r => r.NormalizedValue);
        foreach (var (entryId, field) in decided)
        {
            if (!cardFields.TryGetValue(entryId, out var fields) || MetadataFields.Find(field) is null) continue;
            var values = keptBy[(entryId, field)].GroupBy(k => k.NormalizedValue)
                .Select(g => g.OrderByDescending(k => k.Origin == AssertionOrigin.User).ThenByDescending(k => k.DecidedUtc).First())
                .OrderBy(k => k.NormalizedValue, StringComparer.Ordinal)
                .Select(k => JsonSerializer.Deserialize<string>(k.ValueJson) ?? "").ToList();
            fields[SyncFields.MetadataPrefix + field] = Json(new SyncedMetadata(values, [.. rejectedBy[(entryId, field)].Distinct().Order(StringComparer.Ordinal)]));
        }

        foreach (var id in await db.Favorites.AsNoTracking().Select(f => f.EntryId).ToListAsync(ct))
            if (cardFields.TryGetValue(id, out var fields)) fields[SyncFields.Favorite] = Json(true);
        foreach (var note in await db.Notes.AsNoTracking().Where(n => n.PageRefId == null).Select(n => new { n.EntryId, n.Text }).ToListAsync(ct))
            if (cardFields.TryGetValue(note.EntryId, out var fields)) fields[SyncFields.Note] = Json(note.Text);

        var collections = await db.Collections.AsNoTracking().ToListAsync(ct);
        var collectionIds = collections.ToDictionary(c => c.Id, c => c.SyncId);
        foreach (var item in await db.CollectionItems.AsNoTracking().ToListAsync(ct))
            if (cardFields.TryGetValue(item.EntryId, out var fields) && collectionIds.TryGetValue(item.CollectionId, out var syncId))
                fields[SyncFields.InCollectionPrefix + Id(syncId)] = Json(true);
        foreach (var c in collections)
            records[SyncKeys.For(SyncKeys.Collection, c.SyncId)] = Fields(
                (SyncFields.Name, Json(c.Name)),
                (SyncFields.Description, c.Description is { } d ? Json(d) : null),
                (SyncFields.Parent, c.ParentId is { } p && collectionIds.TryGetValue(p, out var parent) ? Json(Id(parent)) : null));

        var views = await db.SmartViews.AsNoTracking().ToListAsync(ct);
        foreach (var v in views)
            records[SyncKeys.For(SyncKeys.SavedSearch, v.SyncId)] = Fields((SyncFields.Name, Json(v.Name)), (SyncFields.Definition, Json(v.Definition)));

        var packs = await db.SessionPacks.AsNoTracking().ToListAsync(ct);
        var packIds = packs.ToDictionary(p => p.Id, p => p.SyncId);
        foreach (var p in packs)
            records[SyncKeys.For(SyncKeys.Binder, p.SyncId)] = Fields(
                (SyncFields.Title, Json(p.Title)),
                (SyncFields.Date, p.Date is { } date ? Json(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null),
                (SyncFields.Notes, p.Notes is { } n ? Json(n) : null),
                (SyncFields.AtHand, p.KeepAtHand ? Json(true) : null),
                (SyncFields.System, p.GameSystem is { } s ? Json(s) : null));
        var sections = await db.SessionSections.AsNoTracking().ToListAsync(ct);
        var sectionIds = sections.ToDictionary(s => s.Id, s => s.SyncId);
        foreach (var s in sections.Where(s => packIds.ContainsKey(s.PackId)))
            records[SyncKeys.For(SyncKeys.Section, s.SyncId)] = Fields(
                (SyncFields.Binder, Json(Id(packIds[s.PackId]))), (SyncFields.Name, Json(s.Name)), (SyncFields.Position, Json(s.Position)));

        var items = await db.SessionItems.AsNoTracking().ToListAsync(ct);
        var pageNotes = await db.Notes.AsNoTracking().Where(n => n.PageRefId != null).ToListAsync(ct);
        var refIds = items.Where(i => i.PageRefId != null).Select(i => i.PageRefId!.Value).Concat(pageNotes.Select(n => n.PageRefId!.Value)).Distinct().ToList();
        var pages = (await db.PageRefs.AsNoTracking().Where(r => refIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Document.ContentHash, r.FirstPdfPage, r.LastPdfPage, r.FirstLabel, r.LastLabel, r.FirstFingerprint, r.LastFingerprint })
                .ToListAsync(ct))
            .ToDictionary(r => r.Id, r => Json(new SyncedPages(r.ContentHash, r.FirstPdfPage, r.LastPdfPage, r.FirstLabel, r.LastLabel, r.FirstFingerprint, r.LastFingerprint)));
        foreach (var i in items)
        {
            if (!packIds.TryGetValue(i.PackId, out var pack) || !cardKeys.TryGetValue(i.EntryId, out var card)) continue;
            records[SyncKeys.For(SyncKeys.Item, i.SyncId)] = Fields(
                (SyncFields.Binder, Json(Id(pack))),
                (SyncFields.Section, i.SectionId is { } section && sectionIds.TryGetValue(section, out var s) ? Json(Id(s)) : null),
                (SyncFields.Position, Json(i.Position)),
                (SyncFields.Card, Json(card)),
                (SyncFields.Pages, i.PageRefId is { } r ? pages.GetValueOrDefault(r) : null),
                (SyncFields.Label, i.Label is { } l ? Json(l) : null),
                (SyncFields.Note, i.Note is { } n ? Json(n) : null));
        }
        foreach (var n in pageNotes)
        {
            if (!cardKeys.TryGetValue(n.EntryId, out var card) || !pages.TryGetValue(n.PageRefId!.Value, out var on)) continue;
            records[SyncKeys.For(SyncKeys.PageNote, n.SyncId)] = Fields((SyncFields.Card, Json(card)), (SyncFields.Pages, on), (SyncFields.Text, Json(n.Text)));
        }

        var viewIds = views.ToDictionary(v => v.Id, v => v.SyncId);
        foreach (var pin in await db.Pins.AsNoTracking().ToListAsync(ct))
        {
            var position = Json(pin.Position);
            if (pin.Kind == PinKind.Entry)
            {
                if (pin.EntryId is { } entryId && cardFields.TryGetValue(entryId, out var fields)) fields[SyncFields.PinPosition] = position;
                continue;
            }
            var key = pin.Kind switch
            {
                PinKind.Favorites => SyncKeys.FavoritesPin,
                PinKind.Collection when pin.CollectionId is { } c && collectionIds.TryGetValue(c, out var id) => SyncKeys.For(SyncKeys.Pin + SyncKeys.Collection, id),
                PinKind.Binder when pin.PackId is { } p && packIds.TryGetValue(p, out var id) => SyncKeys.For(SyncKeys.Pin + SyncKeys.Binder, id),
                PinKind.SavedSearch when pin.SmartViewId is { } v && viewIds.TryGetValue(v, out var id) => SyncKeys.For(SyncKeys.Pin + SyncKeys.SavedSearch, id),
                _ => null,
            };
            if (key is not null) records[key] = Fields((SyncFields.Position, position));
        }

        foreach (var term in await db.VocabularyTerms.AsNoTracking().Include(t => t.Aliases).Where(t => t.State == TermState.Active).ToListAsync(ct))
        {
            var names = term.Aliases.Select(a => a.Text).Order(StringComparer.Ordinal).ToList();
            records[SyncKeys.ForTerm(term.Vocabulary, term.Key)] = Fields(
                (SyncFields.Label, Json(term.Label)),
                (SyncFields.ShortLabel, term.ShortLabel is { } s ? Json(s) : null),
                (SyncFields.Parent, term.ParentKey is { } p ? Json(p) : null),
                (SyncFields.Aliases, names.Count > 0 ? Json(names) : null));
        }

        foreach (var pair in await db.CopyDecisions.AsNoTracking().Where(d => d.Answer == CopyAnswer.NotSameBook).Select(d => new { d.FirstHash, d.SecondHash }).ToListAsync(ct))
            records[SyncKeys.ForNotSameBook(pair.FirstHash, pair.SecondHash)] = Fields((SyncFields.Exists, Json(true)));

        foreach (var (entryId, fields) in cardFields) records[cardKeys[entryId]] = fields;
        return new SyncSnapshot(records, aliases, cards, cardKeys);
    }

    static Dictionary<string, string> Fields(params (string Name, string? Value)[] fields) =>
        fields.Where(f => f.Value is not null).ToDictionary(f => f.Name, f => f.Value!, StringComparer.Ordinal);

    sealed record StoredField(string? Value, DateTime ChangedUtc);

    /// <summary>What this PC knows of each record, and whether each is waiting for something it needs here.</summary>
    public async Task<IReadOnlyDictionary<string, (SyncRecord Record, bool Pending)>> LoadStateAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.SyncStates.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => (ToRecord(r), r.Pending), StringComparer.Ordinal);
    }

    static SyncRecord ToRecord(SyncState row)
    {
        var stored = JsonSerializer.Deserialize<Dictionary<string, StoredField>>(row.FieldsJson, SyncJson.Options) ?? [];
        var fields = stored.ToDictionary(f => f.Key, f => new SyncField(f.Value.Value, DateTime.SpecifyKind(f.Value.ChangedUtc, DateTimeKind.Utc)), StringComparer.Ordinal);
        var aliases = row.AliasesJson is { } json ? JsonSerializer.Deserialize<List<string>>(json, SyncJson.Options) ?? [] : [];
        return new SyncRecord(row.Key, fields, row.DeletedUtc is { } d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null) { Aliases = aliases };
    }

    /// <summary>Saves the records that changed, and forgets those in <paramref name="removed"/>, in one transaction.</summary>
    public async Task SaveStateAsync(IReadOnlyCollection<(SyncRecord Record, bool Pending)> changed, IReadOnlyCollection<string> removed, CancellationToken ct = default)
    {
        if (changed.Count == 0 && removed.Count == 0) return;
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var keys = changed.Select(c => c.Record.Key).Concat(removed).Distinct().ToList();
        var rows = new Dictionary<string, SyncState>(StringComparer.Ordinal);
        foreach (var chunk in keys.Chunk(500))
            foreach (var row in await db.SyncStates.Where(s => chunk.Contains(s.Key)).ToListAsync(ct)) rows[row.Key] = row;
        foreach (var key in removed)
            if (rows.Remove(key, out var row)) db.SyncStates.Remove(row);
        foreach (var (record, pending) in changed)
        {
            var fields = JsonSerializer.Serialize(record.Fields.ToDictionary(f => f.Key, f => new StoredField(f.Value.Value, f.Value.ChangedUtc), StringComparer.Ordinal),
                SyncJson.Options);
            var aliases = record.Aliases.Count > 0 ? JsonSerializer.Serialize(record.Aliases, SyncJson.Options) : null;
            if (!rows.TryGetValue(record.Key, out var row))
            {
                row = new SyncState { Key = record.Key, FieldsJson = fields };
                db.SyncStates.Add(row);
                rows[record.Key] = row;
            }
            row.FieldsJson = fields;
            row.AliasesJson = aliases;
            row.DeletedUtc = record.DeletedUtc;
            row.Pending = pending;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
