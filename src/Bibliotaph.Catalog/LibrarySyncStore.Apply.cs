using System.Globalization;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Catalog;

/// <summary>
/// One record for the catalog to agree with after a merge: its fields as merged (<see cref="Wanted"/>; null deletes it)
/// and as the catalog has them now (<see cref="Current"/>; null when it isn't here).
/// </summary>
public sealed record SyncChange(string Key, IReadOnlyDictionary<string, string>? Wanted, IReadOnlyDictionary<string, string>? Current);

/// <summary>What applying merged records did: the keys that couldn't be applied yet (their book isn't here, say), and whether anything changed.</summary>
public sealed record SyncApplyResult(IReadOnlySet<string> NotApplied, bool Changed);

public sealed partial class LibrarySyncStore
{
    /// <summary>
    /// Makes the catalog agree with merged records: terms first, then collections, saved searches and binders, then
    /// cards, then what points at them (binder items, page notes, pins); deletions the other way round. A record whose
    /// book, file or binder isn't in this catalog is left for later and named in the result; a card is found with
    /// <paramref name="findCard"/>, which knows other PCs' names for it. Each record is its own save, so one that can't
    /// be applied doesn't stop the rest. Writes made here don't count as the user's
    /// (<see cref="CatalogWrites.Quiet"/>).
    /// </summary>
    public async Task<SyncApplyResult> ApplyAsync(IReadOnlyList<SyncChange> changes, Func<string, long?> findCard, CancellationToken ct = default)
    {
        using var quiet = CatalogWrites.Quiet();
        var notApplied = new HashSet<string>(StringComparer.Ordinal);
        var created = new Dictionary<string, long>(StringComparer.Ordinal);
        long? Card(string key) => created.TryGetValue(key, out var id) ? id : findCard(key);
        var changed = false;

        var upserts = changes.Where(c => c.Wanted is not null).OrderBy(c => Rank(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        foreach (var change in upserts)
        {
            try
            {
                var applied = await UpsertAsync(change.Key, change.Wanted!, change.Current, Card, created, ct);
                if (applied is null) notApplied.Add(change.Key);
                else changed |= applied.Value;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or FormatException or KeyNotFoundException or System.Text.Json.JsonException)
            {
                // Tried again on the next pass; the log is the only place a record that keeps failing shows up.
                _log.LogWarning(ex, "Library sync couldn't apply {Key}; it will be tried again", change.Key);
                notApplied.Add(change.Key);
            }
        }
        // A collection's parent may have been made after it, so parents are set once every collection is there.
        foreach (var change in upserts.Where(c => c.Key.StartsWith(SyncKeys.Collection, StringComparison.Ordinal) && !notApplied.Contains(c.Key)))
            changed |= await SetParentAsync(change.Key, change.Wanted!, ct);

        foreach (var change in changes.Where(c => c.Wanted is null).OrderByDescending(c => Rank(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            try
            {
                changed |= await DeleteAsync(change.Key, Card, ct);
            }
            catch (DbUpdateException ex)
            {
                _log.LogWarning(ex, "Library sync couldn't delete {Key}; it will be tried again", change.Key);
                notApplied.Add(change.Key);
            }
        }
        if (changed) vocabularies.Forget();
        return new SyncApplyResult(notApplied, changed);
    }

    /// <summary>The order records are applied in: what others point at first.</summary>
    static int Rank(string key) => key switch
    {
        _ when key.StartsWith(SyncKeys.Term, StringComparison.Ordinal) => 0,
        _ when key.StartsWith(SyncKeys.Collection, StringComparison.Ordinal) => 1,
        _ when key.StartsWith(SyncKeys.SavedSearch, StringComparison.Ordinal) => 2,
        _ when key.StartsWith(SyncKeys.Binder, StringComparison.Ordinal) => 3,
        _ when key.StartsWith(SyncKeys.Section, StringComparison.Ordinal) => 4,
        _ when SyncKeys.IsCard(key) => 5,
        _ when key.StartsWith(SyncKeys.Item, StringComparison.Ordinal) => 6,
        _ when key.StartsWith(SyncKeys.PageNote, StringComparison.Ordinal) => 7,
        _ when key.StartsWith(SyncKeys.Pin, StringComparison.Ordinal) => 8,
        _ => 9,
    };

    static T Read<T>(IReadOnlyDictionary<string, string> fields, string name) => SyncJson.Read<T>(fields[name])!;

    static T? ReadOrDefault<T>(IReadOnlyDictionary<string, string>? fields, string name) =>
        fields is not null && fields.TryGetValue(name, out var json) ? SyncJson.Read<T>(json) : default;

    static Guid? GuidOf(IReadOnlyDictionary<string, string> fields, string name) =>
        ReadOrDefault<string>(fields, name) is { } text && Guid.TryParse(text, out var id) ? id : null;

    /// <summary>Applies one record. Null when it can't be yet; otherwise whether anything changed.</summary>
    async Task<bool?> UpsertAsync(string key, IReadOnlyDictionary<string, string> wanted, IReadOnlyDictionary<string, string>? current, Func<string, long?> card,
        Dictionary<string, long> created, CancellationToken ct)
    {
        if (SyncKeys.IsCard(key)) return await UpsertCardAsync(key, wanted, current, card, created, ct);
        if (key.StartsWith(SyncKeys.Term, StringComparison.Ordinal)) return await UpsertTermAsync(key, wanted, ct);
        if (key.StartsWith(SyncKeys.NotSameBook, StringComparison.Ordinal)) return await UpsertNotSameBookAsync(key, ct);
        if (key.StartsWith(SyncKeys.Pin, StringComparison.Ordinal))
        {
            await using var db = await contexts.CreateDbContextAsync(ct);
            return await PinTargetAsync(db, key[SyncKeys.Pin.Length..], ct) is { } target ? await SetPinAsync(db, target, Read<int>(wanted, SyncFields.Position), ct) : null;
        }

        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (SyncKeys.IdOf(key, SyncKeys.Collection) is { } collectionId)
            {
                var row = await db.Collections.SingleOrDefaultAsync(c => c.SyncId == collectionId, ct);
                if (row is null)
                {
                    var now = Now;
                    row = db.Collections.Add(new CollectionNode { Name = "", SyncId = collectionId, CreatedUtc = now, UsedUtc = now }).Entity;
                }
                row.Name = Read<string>(wanted, SyncFields.Name);
                row.Description = ReadOrDefault<string>(wanted, SyncFields.Description);
                return await SaveAsync(db, ct);
            }
            if (SyncKeys.IdOf(key, SyncKeys.SavedSearch) is { } viewId)
            {
                var row = await db.SmartViews.SingleOrDefaultAsync(v => v.SyncId == viewId, ct);
                var definition = Read<string>(wanted, SyncFields.Definition);
                if (row is null)
                {
                    var now = Now;
                    row = db.SmartViews.Add(new SmartView { Name = "", Definition = definition, SyncId = viewId, CreatedUtc = now, UpdatedUtc = now }).Entity;
                }
                else if (row.Definition != definition) row.UpdatedUtc = Now;
                row.Name = Read<string>(wanted, SyncFields.Name);
                row.Definition = definition;
                return await SaveAsync(db, ct);
            }
            if (SyncKeys.IdOf(key, SyncKeys.Section) is { } sectionId)
            {
                if (await PackAsync(db, wanted, ct) is not { } packId) return null;
                var row = await db.SessionSections.SingleOrDefaultAsync(s => s.SyncId == sectionId, ct)
                    ?? db.SessionSections.Add(new SessionSection { Name = "", SyncId = sectionId }).Entity;
                row.PackId = packId;
                row.Name = Read<string>(wanted, SyncFields.Name);
                row.Position = Read<int>(wanted, SyncFields.Position);
                return await SaveAsync(db, ct);
            }
            if (SyncKeys.IdOf(key, SyncKeys.Item) is { } itemId)
            {
                if (await PackAsync(db, wanted, ct) is not { } packId || card(Read<string>(wanted, SyncFields.Card)) is not { } entryId) return null;
                var pages = ReadOrDefault<SyncedPages>(wanted, SyncFields.Pages);
                PageRange? range = null;
                if (pages is not null && (range = await RangeAsync(db, pages, ct)) is null) return null;
                var section = GuidOf(wanted, SyncFields.Section) is { } s
                    ? await db.SessionSections.Where(x => x.SyncId == s && x.PackId == packId).Select(x => (long?)x.Id).SingleOrDefaultAsync(ct) : null;
                var row = await db.SessionItems.Include(i => i.PageRef).SingleOrDefaultAsync(i => i.SyncId == itemId, ct)
                    ?? db.SessionItems.Add(new SessionItem { SyncId = itemId, AddedUtc = Now }).Entity;
                row.PackId = packId;
                row.SectionId = section;
                row.Position = Read<int>(wanted, SyncFields.Position);
                row.EntryId = entryId;
                row.Link = ReadOrDefault<string>(wanted, SyncFields.Link);
                row.Label = ReadOrDefault<string>(wanted, SyncFields.Label);
                row.Note = ReadOrDefault<string>(wanted, SyncFields.Note);
                var oldPages = SwapPages(db, row.PageRef, range, p => row.PageRef = p);
                var saved = await SaveAsync(db, ct);
                if (oldPages is not null) await db.PageRefs.Where(r => r.Id == oldPages.Id).ExecuteDeleteAsync(ct);
                return saved;
            }
            if (SyncKeys.IdOf(key, SyncKeys.PageNote) is { } noteId)
            {
                if (card(Read<string>(wanted, SyncFields.Card)) is not { } entryId || await RangeAsync(db, Read<SyncedPages>(wanted, SyncFields.Pages), ct) is not { } range)
                    return null;
                var row = await db.Notes.Include(n => n.PageRef).SingleOrDefaultAsync(n => n.SyncId == noteId, ct);
                var now = Now;
                row ??= db.Notes.Add(new Note { Text = "", SyncId = noteId, CreatedUtc = now, UpdatedUtc = now }).Entity;
                var text = Read<string>(wanted, SyncFields.Text);
                if (row.Text != text) row.UpdatedUtc = now;
                row.Text = text;
                row.EntryId = entryId;
                var oldPages = SwapPages(db, row.PageRef, range, p => row.PageRef = p);
                var saved = await SaveAsync(db, ct);
                // The note left its old pages first, so the cascade from them can't take it.
                if (oldPages is not null) await db.PageRefs.Where(r => r.Id == oldPages.Id).ExecuteDeleteAsync(ct);
                return saved;
            }
            if (SyncKeys.IdOf(key, SyncKeys.Binder) is { } binderId)
            {
                var row = await db.SessionPacks.SingleOrDefaultAsync(p => p.SyncId == binderId, ct);
                if (row is null)
                {
                    var now = Now;
                    row = db.SessionPacks.Add(new SessionPack { Title = "", SyncId = binderId, CreatedUtc = now, TouchedUtc = now }).Entity;
                }
                row.Title = Read<string>(wanted, SyncFields.Title);
                row.Date = ReadOrDefault<string>(wanted, SyncFields.Date) is { } date ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
                row.Notes = ReadOrDefault<string>(wanted, SyncFields.Notes);
                row.KeepAtHand = ReadOrDefault<bool>(wanted, SyncFields.AtHand);
                row.GameSystem = ReadOrDefault<string>(wanted, SyncFields.System);
                return await SaveAsync(db, ct);
            }
        }
        // A kind this Bibliotaph doesn't know, from a newer one: nothing to do with it here.
        return false;
    }

    static async Task<bool> SaveAsync(CatalogDbContext db, CancellationToken ct) => await db.SaveChangesAsync(ct) > 0;

    static Task<long?> PackAsync(CatalogDbContext db, IReadOnlyDictionary<string, string> fields, CancellationToken ct) =>
        GuidOf(fields, SyncFields.Binder) is { } binder
            ? db.SessionPacks.Where(p => p.SyncId == binder).Select(p => (long?)p.Id).SingleOrDefaultAsync(ct)
            : Task.FromResult<long?>(null);

    /// <summary>The pages in this catalog's file with their hash; null when that file isn't here.</summary>
    static async Task<PageRange?> RangeAsync(CatalogDbContext db, SyncedPages pages, CancellationToken ct) =>
        await db.Documents.Where(d => d.ContentHash == pages.Hash).Select(d => (long?)d.Id).SingleOrDefaultAsync(ct) is { } documentId
            ? new PageRange(documentId, pages.First, pages.Last, pages.FirstLabel, pages.LastLabel, pages.FirstFingerprint, pages.LastFingerprint)
            : null;

    /// <summary>
    /// Points an item or note at <paramref name="range"/> through a new page reference when its pages changed, and
    /// returns the old reference for the caller to delete once saved; null when they are the same pages.
    /// </summary>
    static PageRef? SwapPages(CatalogDbContext db, PageRef? old, PageRange? range, Action<PageRef?> set)
    {
        if (old is null && range is null) return null;
        if (old is not null && range is not null && old.DocumentId == range.DocumentId && old.FirstPdfPage == Math.Min(range.FirstPdfPage, range.LastPdfPage)
            && old.LastPdfPage == Math.Max(range.FirstPdfPage, range.LastPdfPage) && old.FirstLabel == range.FirstLabel && old.LastLabel == range.LastLabel
            && old.FirstFingerprint == range.FirstFingerprint && old.LastFingerprint == range.LastFingerprint) return null;
        set(range is null ? null : PageRefStore.ToPageRef(range));
        db.ChangeTracker.DetectChanges();
        return old;
    }

    async Task<bool> SetParentAsync(string key, IReadOnlyDictionary<string, string> wanted, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var id = SyncKeys.IdOf(key, SyncKeys.Collection)!.Value;
        var tree = await db.Collections.Select(c => new { c.Id, c.SyncId, c.ParentId }).ToListAsync(ct);
        if (tree.FirstOrDefault(c => c.SyncId == id) is not { } row) return false;
        long? parent = GuidOf(wanted, SyncFields.Parent) is { } p ? tree.FirstOrDefault(c => c.SyncId == p)?.Id : null;
        // Never inside itself: a parent that is the collection or one of its own sub-collections leaves it at the top.
        var parents = tree.ToDictionary(c => c.Id, c => c.ParentId);
        for (long? at = parent; at is { } a && parent is not null; at = parents.GetValueOrDefault(a))
            if (a == row.Id) parent = null;
        if (row.ParentId == parent) return false;
        await db.Collections.Where(c => c.Id == row.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, parent), ct);
        return true;
    }

    /// <summary>A card's own fields: its metadata decisions, heart, note, collections and pin. A book owned elsewhere that isn't here yet is made.</summary>
    async Task<bool?> UpsertCardAsync(string key, IReadOnlyDictionary<string, string> wanted, IReadOnlyDictionary<string, string>? current, Func<string, long?> card,
        Dictionary<string, long> created, CancellationToken ct)
    {
        var changed = false;
        if (card(key) is not { } entryId)
        {
            if (SyncKeys.IdOf(key, SyncKeys.Elsewhere) is not { } syncId) return null;
            await using var db = await contexts.CreateDbContextAsync(ct);
            var entry = db.Entries.Add(new Entry { Kind = EntryKind.Elsewhere, SyncId = syncId, CreatedUtc = Now }).Entity;
            await db.SaveChangesAsync(ct);
            created[key] = entryId = entry.Id;
            current = null;
            changed = true;
        }
        var id = new EntryId(entryId);
        bool Differs(string name) => current?.GetValueOrDefault(name) != wanted.GetValueOrDefault(name);

        foreach (var field in MetadataFields.All)
        {
            var name = SyncFields.MetadataPrefix + field.Key;
            if (!Differs(name)) continue;
            var value = ReadOrDefault<SyncedMetadata>(wanted, name) ?? new SyncedMetadata([], []);
            changed |= await metadata.ApplySyncedAsync(id, field, value.Values, value.Rejected, ct);
        }

        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (Differs(SyncFields.Favorite))
            {
                var favorite = await db.Favorites.FindAsync([entryId], ct);
                if (wanted.ContainsKey(SyncFields.Favorite) && favorite is null) db.Favorites.Add(new Favorite { EntryId = entryId, CreatedUtc = Now });
                else if (!wanted.ContainsKey(SyncFields.Favorite) && favorite is not null) db.Favorites.Remove(favorite);
            }
            if (Differs(SyncFields.Note))
            {
                var note = await db.Notes.SingleOrDefaultAsync(n => n.EntryId == entryId && n.PageRefId == null, ct);
                var text = ReadOrDefault<string>(wanted, SyncFields.Note);
                var now = Now;
                if (text is null && note is not null) db.Notes.Remove(note);
                else if (text is not null && note is null) db.Notes.Add(new Note { EntryId = entryId, Text = text, CreatedUtc = now, UpdatedUtc = now });
                else if (text is not null && note is not null && note.Text != text)
                {
                    note.Text = text;
                    note.UpdatedUtc = now;
                }
            }
            var memberships = wanted.Keys.Concat(current?.Keys ?? []).Where(k => k.StartsWith(SyncFields.InCollectionPrefix, StringComparison.Ordinal))
                .Distinct().Where(Differs).ToList();
            foreach (var name in memberships)
            {
                if (!Guid.TryParse(name.AsSpan(SyncFields.InCollectionPrefix.Length), out var collectionSyncId)) continue;
                // A collection deleted here meanwhile takes its memberships with it.
                if (await db.Collections.Where(c => c.SyncId == collectionSyncId).Select(c => (long?)c.Id).SingleOrDefaultAsync(ct) is not { } collectionId) continue;
                var item = await db.CollectionItems.FindAsync([collectionId, entryId], ct);
                if (wanted.ContainsKey(name) && item is null) db.CollectionItems.Add(new CollectionItem { CollectionId = collectionId, EntryId = entryId, AddedUtc = Now });
                else if (!wanted.ContainsKey(name) && item is not null) db.CollectionItems.Remove(item);
            }
            changed |= await SaveAsync(db, ct);
            if (Differs(SyncFields.PinPosition))
                changed |= ReadOrDefault<int?>(wanted, SyncFields.PinPosition) is { } position
                    ? await SetPinAsync(db, PinTarget.Entry(id), position, ct)
                    : await db.Pins.Where(PinStore.Matches(PinTarget.Entry(id))).ExecuteDeleteAsync(ct) > 0;
        }
        return changed;
    }

    /// <summary>Puts the target's pin at <paramref name="position"/>, as the other PC has it, without moving the others: gaps and ties are fine.</summary>
    static async Task<bool> SetPinAsync(CatalogDbContext db, PinTarget target, int position, CancellationToken ct)
    {
        if (await db.Pins.SingleOrDefaultAsync(PinStore.Matches(target), ct) is { } pin)
        {
            if (pin.Position == position) return false;
            pin.Position = position;
        }
        else
        {
            db.Pins.Add(new Pin
            {
                Kind = target.Kind,
                Position = position,
                EntryId = target.Kind == PinKind.Entry ? target.Id : null,
                CollectionId = target.Kind == PinKind.Collection ? target.Id : null,
                PackId = target.Kind == PinKind.Binder ? target.Id : null,
                SmartViewId = target.Kind == PinKind.SavedSearch ? target.Id : null,
            });
        }
        return await SaveAsync(db, ct);
    }

    /// <summary>What a pin key ("fav", "c:…", "b:…", "s:…") points at in this catalog; null when its target isn't here.</summary>
    static async Task<PinTarget?> PinTargetAsync(CatalogDbContext db, string target, CancellationToken ct)
    {
        if (target == SyncKeys.FavoritesPin[SyncKeys.Pin.Length..]) return PinTarget.Favorites;
        if (SyncKeys.IdOf(target, SyncKeys.Collection) is { } c)
            return await db.Collections.Where(x => x.SyncId == c).Select(x => (long?)x.Id).SingleOrDefaultAsync(ct) is { } id ? PinTarget.Collection(id) : null;
        if (SyncKeys.IdOf(target, SyncKeys.Binder) is { } b)
            return await db.SessionPacks.Where(x => x.SyncId == b).Select(x => (long?)x.Id).SingleOrDefaultAsync(ct) is { } id ? PinTarget.Binder(id) : null;
        if (SyncKeys.IdOf(target, SyncKeys.SavedSearch) is { } s)
            return await db.SmartViews.Where(x => x.SyncId == s).Select(x => (long?)x.Id).SingleOrDefaultAsync(ct) is { } id ? PinTarget.SavedSearch(id) : null;
        return null;
    }

    /// <summary>
    /// A term as the other PC has it: added as the user's own when it isn't here, otherwise renamed, with exactly its
    /// aliases (an alias another term of the vocabulary has here moves, as Settings > Vocabulary moves one). A term
    /// waiting here as a model's proposal keeps waiting: deciding it has its own consequences.
    /// </summary>
    async Task<bool?> UpsertTermAsync(string key, IReadOnlyDictionary<string, string> wanted, CancellationToken ct)
    {
        var parts = key[SyncKeys.Term.Length..].Split(':', 2);
        if (parts.Length != 2) return false;
        var (vocabulary, termKey) = (parts[0], parts[1]);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var term = await db.VocabularyTerms.Include(t => t.Aliases).SingleOrDefaultAsync(t => t.Vocabulary == vocabulary && t.Key == termKey, ct);
        if (term is null)
        {
            term = db.VocabularyTerms.Add(new VocabularyTerm
            {
                Vocabulary = vocabulary,
                Key = termKey,
                Label = "",
                Origin = TermOrigin.User,
                State = TermState.Active,
                CreatedUtc = Now,
            }).Entity;
        }
        else if (term.State != TermState.Active) return false;
        term.Label = Read<string>(wanted, SyncFields.Label);
        term.ShortLabel = ReadOrDefault<string>(wanted, SyncFields.ShortLabel);
        term.ParentKey = ReadOrDefault<string>(wanted, SyncFields.Parent);
        var names = ReadOrDefault<List<string>>(wanted, SyncFields.Aliases) ?? [];
        db.VocabularyAliases.RemoveRange(term.Aliases.Where(a => !names.Contains(a.Text, StringComparer.Ordinal)));
        var others = await db.VocabularyAliases.Where(a => a.Term.Vocabulary == vocabulary && a.Term.Key != termKey).ToListAsync(ct);
        foreach (var name in names.Where(n => term.Aliases.All(a => a.Text != n)))
        {
            var normalized = MetadataText.Normalize(name);
            db.VocabularyAliases.RemoveRange(others.Where(a => a.Normalized == normalized));
            term.Aliases.Add(new VocabularyAlias { Text = name, Normalized = normalized });
        }
        return await SaveAsync(db, ct);
    }

    async Task<bool> UpsertNotSameBookAsync(string key, CancellationToken ct)
    {
        var parts = key[SyncKeys.NotSameBook.Length..].Split(':');
        if (parts.Length != 2) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.CopyDecisions.AnyAsync(d => d.FirstHash == parts[0] && d.SecondHash == parts[1], ct)) return false;
        db.CopyDecisions.Add(new CopyDecision { FirstHash = parts[0], SecondHash = parts[1], Answer = CopyAnswer.NotSameBook, CreatedUtc = Now });
        return await SaveAsync(db, ct);
    }

    /// <summary>
    /// Deletes what another PC deleted, as this PC's own pages would: a collection's sub-collections move up a level,
    /// a binder's and an item's page references go with them, a book owned elsewhere goes with everything set on it.
    /// A card with files is never deleted this way. Returns whether anything was there to delete.
    /// </summary>
    async Task<bool> DeleteAsync(string key, Func<string, long?> card, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = false;
        if (key.StartsWith(SyncKeys.Pin, StringComparison.Ordinal))
        {
            if (await PinTargetAsync(db, key[SyncKeys.Pin.Length..], ct) is { } target)
                deleted = await db.Pins.Where(PinStore.Matches(target)).ExecuteDeleteAsync(ct) > 0;
        }
        else if (SyncKeys.IdOf(key, SyncKeys.Collection) is { } collectionId)
        {
            if (await db.Collections.SingleOrDefaultAsync(c => c.SyncId == collectionId, ct) is { } row)
            {
                await db.Collections.Where(c => c.ParentId == row.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, row.ParentId), ct);
                db.Collections.Remove(row);
                deleted = true;
            }
        }
        else if (SyncKeys.IdOf(key, SyncKeys.SavedSearch) is { } viewId)
            deleted = await db.SmartViews.Where(v => v.SyncId == viewId).ExecuteDeleteAsync(ct) > 0;
        else if (SyncKeys.IdOf(key, SyncKeys.Binder) is { } binderId)
        {
            if (await db.SessionPacks.SingleOrDefaultAsync(p => p.SyncId == binderId, ct) is { } row)
            {
                var pages = await db.SessionItems.Where(i => i.PackId == row.Id && i.PageRefId != null).Select(i => i.PageRefId!.Value).ToListAsync(ct);
                db.SessionPacks.Remove(row);
                await db.SaveChangesAsync(ct);
                await db.PageRefs.Where(r => pages.Contains(r.Id)).ExecuteDeleteAsync(ct);
                deleted = true;
            }
        }
        else if (SyncKeys.IdOf(key, SyncKeys.Section) is { } sectionId)
            deleted = await db.SessionSections.Where(s => s.SyncId == sectionId).ExecuteDeleteAsync(ct) > 0;
        else if (SyncKeys.IdOf(key, SyncKeys.Item) is { } itemId)
        {
            if (await db.SessionItems.SingleOrDefaultAsync(i => i.SyncId == itemId, ct) is { } row)
            {
                db.SessionItems.Remove(row);
                await db.SaveChangesAsync(ct);
                if (row.PageRefId is { } pages) await db.PageRefs.Where(r => r.Id == pages).ExecuteDeleteAsync(ct);
                deleted = true;
            }
        }
        else if (SyncKeys.IdOf(key, SyncKeys.PageNote) is { } noteId)
        {
            if (await db.Notes.SingleOrDefaultAsync(n => n.SyncId == noteId, ct) is { PageRefId: { } pages })
                deleted = await db.PageRefs.Where(r => r.Id == pages).ExecuteDeleteAsync(ct) > 0;
        }
        else if (SyncKeys.IdOf(key, SyncKeys.Elsewhere) is { } && card(key) is { } entryId)
        {
            // Only while it is still a book owned elsewhere: one that has met its file here stays, with its file.
            if (await db.Entries.SingleOrDefaultAsync(e => e.Id == entryId && e.Kind == EntryKind.Elsewhere && !e.Sources.Any(), ct) is { } row)
            {
                db.Entries.Remove(row);
                deleted = true;
            }
        }
        else if (key.StartsWith(SyncKeys.NotSameBook, StringComparison.Ordinal) && key[SyncKeys.NotSameBook.Length..].Split(':') is [var first, var second])
            deleted = await db.CopyDecisions.Where(d => d.FirstHash == first && d.SecondHash == second).ExecuteDeleteAsync(ct) > 0;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }
}
