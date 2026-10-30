using System.Globalization;
using System.Text.Json;
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

/// <summary>
/// What applying merged records did: the keys that couldn't be applied yet (their book isn't here, say), whether
/// anything changed, and what is left for the services outside catalog.db to do.
/// </summary>
public sealed record SyncApplyResult(IReadOnlySet<string> NotApplied, bool Changed, SyncFollowUps FollowUps);

/// <summary>
/// What a merge decided that needs more than catalog.db: the user's own tags to guess again across the library, by
/// name; cards whose packs changed, whose folder hints are stored again; books whose text the user forgot or read again
/// on another device; cover pages to draw, by document; and cover pictures chosen for books owned elsewhere, by their
/// entries' sync ids (null takes one away); and folders whose rules changed, whose files' hints are read again.
/// </summary>
public sealed class SyncFollowUps
{
    public HashSet<string> Tags { get; } = new(StringComparer.Ordinal);
    public List<EntryId> Packed { get; } = [];
    public HashSet<EntryId> Forget { get; } = [];
    public HashSet<EntryId> ReadAgain { get; } = [];
    public Dictionary<long, int> CoverPages { get; } = [];
    public Dictionary<Guid, byte[]?> ChosenCovers { get; } = [];
    public HashSet<FolderKey> Folders { get; } = [];
}

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
        var follow = new SyncFollowUps();
        long? Card(string key) => created.TryGetValue(key, out var id) ? id : findCard(key);
        var changed = false;

        // A term turned down goes after the others, which may have taken its name, as the other PC filed it under one of them.
        var upserts = changes.Where(c => c.Wanted is not null).OrderBy(c => Rank(c.Key)).ThenBy(c => c.Wanted!.ContainsKey(SyncFields.Rejected))
            .ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        foreach (var change in upserts)
        {
            try
            {
                var applied = await UpsertAsync(change.Key, change.Wanted!, change.Current, Card, created, follow, ct);
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
        changed |= await ApplyPacksAsync(upserts, Card, notApplied, follow.Packed, ct);
        // A collection's parent may have been made after it, so parents are set once every collection is there.
        foreach (var change in upserts.Where(c => c.Key.StartsWith(SyncKeys.Collection, StringComparison.Ordinal) && !notApplied.Contains(c.Key)))
            changed |= await SetParentAsync(change.Key, change.Wanted!, ct);

        foreach (var change in changes.Where(c => c.Wanted is null).OrderByDescending(c => Rank(c.Key)).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            try
            {
                changed |= await DeleteAsync(change.Key, Card, follow, ct);
            }
            catch (DbUpdateException ex)
            {
                _log.LogWarning(ex, "Library sync couldn't delete {Key}; it will be tried again", change.Key);
                notApplied.Add(change.Key);
            }
        }
        if (changed) vocabularies.Forget();
        return new SyncApplyResult(notApplied, changed, follow);
    }

    /// <summary>
    /// Packs, once every card is in place: files are put in the packs the other device has them in, or on cards of their
    /// own (<see cref="PackStore.ApplySyncedAsync"/>), then packs take the covers chosen there. A file that couldn't be
    /// put as it was there, or a pack whose cover file isn't in it here, waits.
    /// </summary>
    async Task<bool> ApplyPacksAsync(IReadOnlyList<SyncChange> upserts, Func<string, long?> card, HashSet<string> notApplied, List<EntryId> packed,
        CancellationToken ct)
    {
        static bool Differs(SyncChange c, string name) => c.Current?.GetValueOrDefault(name) != c.Wanted!.GetValueOrDefault(name);
        var wishes = new Dictionary<long, Guid?>();
        var keys = new Dictionary<long, string>();
        foreach (var change in upserts.Where(c => c.Key.StartsWith(SyncKeys.Book, StringComparison.Ordinal) && !notApplied.Contains(c.Key) && Differs(c, SyncFields.InPack)))
        {
            if (card(change.Key) is not { } entryId) continue;
            wishes[entryId] = ReadOrDefault<string>(change.Wanted, SyncFields.InPack) is { Length: > 0 } text && Guid.TryParse(text, out var pack) ? pack : null;
            keys[entryId] = change.Key;
        }
        var changed = false;
        if (wishes.Count > 0)
        {
            var (moved, unsettled) = await new PackStore(contexts, _clock).ApplySyncedAsync(wishes, ct);
            changed = moved.Count > 0;
            packed.AddRange(moved);
            foreach (var entryId in unsettled) notApplied.Add(keys[entryId]);
        }

        foreach (var change in upserts.Where(c => c.Key.StartsWith(SyncKeys.Pack, StringComparison.Ordinal) && !notApplied.Contains(c.Key) && Differs(c, SyncFields.Cover)))
        {
            if (card(change.Key) is not { } packId) continue;
            long? fileId = ReadOrDefault<string>(change.Wanted, SyncFields.Cover) is { } coverKey ? card(coverKey) : null;
            await using var db = await contexts.CreateDbContextAsync(ct);
            if (await db.Entries.SingleOrDefaultAsync(e => e.Id == packId && e.Kind == EntryKind.Pack, ct) is not { } pack) continue;
            if (fileId is not null && !await db.Entries.AnyAsync(e => e.Id == fileId && e.ParentEntryId == packId, ct))
            {
                notApplied.Add(change.Key);
                continue;
            }
            pack.CoverEntryId = fileId;
            changed |= await SaveAsync(db, ct);
        }
        return changed;
    }

    /// <summary>The order records are applied in: what others point at first.</summary>
    static int Rank(string key) => key switch
    {
        _ when key.StartsWith(SyncKeys.Term, StringComparison.Ordinal) => 0,
        _ when key.StartsWith(SyncKeys.Collection, StringComparison.Ordinal) => 1,
        _ when key.StartsWith(SyncKeys.SavedSearch, StringComparison.Ordinal) => 2,
        _ when key.StartsWith(SyncKeys.Binder, StringComparison.Ordinal) => 3,
        _ when key.StartsWith(SyncKeys.Section, StringComparison.Ordinal) => 4,
        // An adventure after the cards, as it is made in its file's, and after joins, which may bring its file.
        _ when key.StartsWith(SyncKeys.Adventure, StringComparison.Ordinal) => 7,
        _ when SyncKeys.IsCard(key) => 5,
        _ when key.StartsWith(SyncKeys.SameBook, StringComparison.Ordinal) => 6,
        _ when key.StartsWith(SyncKeys.Item, StringComparison.Ordinal) => 8,
        _ when key.StartsWith(SyncKeys.PageNote, StringComparison.Ordinal) => 9,
        _ when key.StartsWith(SyncKeys.Pin, StringComparison.Ordinal) => 10,
        _ => 11,
    };

    static T Read<T>(IReadOnlyDictionary<string, string> fields, string name) => SyncJson.Read<T>(fields[name])!;

    static T? ReadOrDefault<T>(IReadOnlyDictionary<string, string>? fields, string name) =>
        fields is not null && fields.TryGetValue(name, out var json) ? SyncJson.Read<T>(json) : default;

    static Guid? GuidOf(IReadOnlyDictionary<string, string> fields, string name) =>
        ReadOrDefault<string>(fields, name) is { } text && Guid.TryParse(text, out var id) ? id : null;

    /// <summary>Applies one record. Null when it can't be yet; otherwise whether anything changed.</summary>
    async Task<bool?> UpsertAsync(string key, IReadOnlyDictionary<string, string> wanted, IReadOnlyDictionary<string, string>? current, Func<string, long?> card,
        Dictionary<string, long> created, SyncFollowUps follow, CancellationToken ct)
    {
        if (SyncKeys.IsCard(key)) return await UpsertCardAsync(key, wanted, current, card, created, follow, ct);
        if (SyncKeys.KeptApartOf(key) is { } apart) return await UpsertKeptApartAsync(apart.Vocabulary, apart.First, apart.Second, ct);
        if (key.StartsWith(SyncKeys.UserTag, StringComparison.Ordinal)) return await UpsertUserTagAsync(key[SyncKeys.UserTag.Length..], wanted, follow.Tags, ct);
        if (key.StartsWith(SyncKeys.Term, StringComparison.Ordinal)) return await UpsertTermAsync(key, wanted, ct);
        if (key.StartsWith(SyncKeys.NotSameBook, StringComparison.Ordinal)) return await UpsertNotSameBookAsync(key, ct);
        if (SyncKeys.SameBookOf(key) is { } same) return await JoinAsync(same.First, same.Second, card, ct);
        if (key.StartsWith(SyncKeys.PartProposal, StringComparison.Ordinal))
            return await UpsertPartProposalAsync(key[SyncKeys.PartProposal.Length..], Enum.Parse<PartProposalAnswer>(Read<string>(wanted, SyncFields.Answer)), ct);
        if (SyncKeys.SeparateFromElsewhereOf(key) is { } separate) return await UpsertSeparateFromElsewhereAsync(separate.Elsewhere, separate.Hash, ct);
        if (SyncKeys.LookupRecordOf(key) is { } record)
            return lookups is not null && await lookups.ApplySyncedAsync(record.Hash, record.Source, record.RecordKey,
                Read<bool>(wanted, SyncFields.SameBook) ? LookupAnswer.SameBook : LookupAnswer.NotThisBook, ct);
        if (key.StartsWith(SyncKeys.CoverPage, StringComparison.Ordinal))
            return await SetCoverPageAsync(key[SyncKeys.CoverPage.Length..], Read<int>(wanted, SyncFields.Page), follow, ct);
        if (key.StartsWith(SyncKeys.Forgotten, StringComparison.Ordinal)) return await SetForgottenAsync(key[SyncKeys.Forgotten.Length..], true, follow, ct);
        if (key.StartsWith(SyncKeys.Reading, StringComparison.Ordinal))
            return await UpsertReadingAsync(key[SyncKeys.Reading.Length..], new SyncedReading(Read<int>(wanted, SyncFields.Page), Read<DateTime>(wanted, SyncFields.Opened)), ct);
        if (SyncKeys.AskedOf(key) is { } asked) return await UpsertAskedAsync(asked, wanted, card, ct);
        if (key.StartsWith(SyncKeys.FolderRule, StringComparison.Ordinal)) return await UpsertFolderRuleAsync(wanted, follow, ct);
        if (key.StartsWith(SyncKeys.FolderCollection, StringComparison.Ordinal)) return await UpsertFolderCollectionAsync(wanted, ct);
        if (key.StartsWith(SyncKeys.Setting, StringComparison.Ordinal)) return await SetSettingAsync(key[SyncKeys.Setting.Length..], Read<string>(wanted, SyncFields.Value), ct);
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

    /// <summary>
    /// A card's own fields: its metadata decisions, heart, note, collections (and those Needs review isn't to suggest),
    /// pin, an open "Cataloged wrong…", an adventure's reading place and a chosen cover. A book owned elsewhere that
    /// isn't here yet is made.
    /// </summary>
    async Task<bool?> UpsertCardAsync(string key, IReadOnlyDictionary<string, string> wanted, IReadOnlyDictionary<string, string>? current, Func<string, long?> card,
        Dictionary<string, long> created, SyncFollowUps follow, CancellationToken ct)
    {
        var changed = false;
        if (card(key) is null && SyncKeys.IdOf(key, SyncKeys.Adventure) is { } partSyncId)
        {
            if (await AddAdventureAsync(partSyncId, wanted, ct) is not { } partId) return null;
            created[key] = partId;
            current = null;
            changed = true;
        }
        else if (SyncKeys.IdOf(key, SyncKeys.Adventure) is not null && current?.GetValueOrDefault(SyncFields.Pages) != wanted.GetValueOrDefault(SyncFields.Pages))
        {
            if (await MoveAdventureAsync(card(key)!.Value, wanted, ct) is not { } moved) return null;
            changed |= moved;
        }
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
            if (Differs(SyncFields.Wrong))
            {
                var requests = await db.CorrectionRequests.Where(c => c.EntryId == entryId).ToListAsync(ct);
                if (wanted.ContainsKey(SyncFields.Wrong) && requests.Count == 0) db.CorrectionRequests.Add(new CorrectionRequest { EntryId = entryId, CreatedUtc = Now });
                else if (!wanted.ContainsKey(SyncFields.Wrong)) db.CorrectionRequests.RemoveRange(requests);
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
            var declines = wanted.Keys.Concat(current?.Keys ?? []).Where(k => k.StartsWith(SyncFields.DeclinedPrefix, StringComparison.Ordinal))
                .Distinct().Where(Differs).ToList();
            foreach (var name in declines)
            {
                if (!Guid.TryParse(name.AsSpan(SyncFields.DeclinedPrefix.Length), out var collectionSyncId)) continue;
                if (await db.Collections.Where(c => c.SyncId == collectionSyncId).Select(c => (long?)c.Id).SingleOrDefaultAsync(ct) is not { } collectionId) continue;
                var decline = await db.CollectionDeclines.FindAsync([collectionId, entryId], ct);
                if (wanted.ContainsKey(name) && decline is null) db.CollectionDeclines.Add(new CollectionDecline { CollectionId = collectionId, EntryId = entryId, CreatedUtc = Now });
                else if (!wanted.ContainsKey(name) && decline is not null) db.CollectionDeclines.Remove(decline);
            }
            changed |= await SaveAsync(db, ct);
            if (Differs(SyncFields.PinPosition))
                changed |= ReadOrDefault<int?>(wanted, SyncFields.PinPosition) is { } position
                    ? await SetPinAsync(db, PinTarget.Entry(id), position, ct)
                    : await db.Pins.Where(PinStore.Matches(PinTarget.Entry(id))).ExecuteDeleteAsync(ct) > 0;
            if (Differs(SyncFields.Read) && SyncKeys.IdOf(key, SyncKeys.Adventure) is not null)
                changed |= await SetAdventureReadingAsync(db, entryId, ReadOrDefault<SyncedReading>(wanted, SyncFields.Read), ct);
            if (Differs(SyncFields.Picture) && await db.Entries.Where(e => e.Id == entryId).Select(e => (Guid?)e.SyncId).SingleOrDefaultAsync(ct) is { } syncId)
            {
                follow.ChosenCovers[syncId] = ReadOrDefault<string>(wanted, SyncFields.Picture) is { } picture ? Convert.FromBase64String(picture) : null;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Where an adventure was left, as read on the device that read it last: in the copy it opens in here.</summary>
    static async Task<bool> SetAdventureReadingAsync(CatalogDbContext db, long partId, SyncedReading? reading, CancellationToken ct)
    {
        var row = await db.ReadingStates.FindAsync([partId], ct);
        if (reading is null)
        {
            if (row is null) return false;
            db.ReadingStates.Remove(row);
            return await SaveAsync(db, ct);
        }
        if (row is not null && row.OpenedUtc > reading.Opened) return false;
        if (await db.EntrySources.Where(s => s.EntryId == partId).OrderByDescending(s => s.IsCurrent).Select(s => (long?)s.DocumentId).FirstOrDefaultAsync(ct)
            is not { } documentId) return false;
        if (row is null) db.ReadingStates.Add(new ReadingState { EntryId = partId, DocumentId = documentId, PageIndex = reading.Page, OpenedUtc = reading.Opened });
        else (row.DocumentId, row.PageIndex, row.OpenedUtc) = (documentId, reading.Page, reading.Opened);
        return await SaveAsync(db, ct);
    }

    /// <summary>The page a file's cover shows, as chosen on another device; the cover is drawn again afterwards. Null while the file isn't here.</summary>
    async Task<bool?> SetCoverPageAsync(string hash, int page, SyncFollowUps follow, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Documents.SingleOrDefaultAsync(d => d.ContentHash == hash, ct) is not { } document) return null;
        int? chosen = page > 0 ? page : null;
        if (document.CoverPage == chosen) return false;
        document.CoverPage = chosen;
        await db.SaveChangesAsync(ct);
        follow.CoverPages[document.Id] = page;
        return true;
    }

    /// <summary>
    /// "Forget its text", or "Read it again", as chosen on another device: the mark is kept here at once, so nothing
    /// reads the file meanwhile, and what reading it made goes (or it is read again) afterwards. Null while a file to
    /// forget isn't here.
    /// </summary>
    async Task<bool?> SetForgottenAsync(string hash, bool forgotten, SyncFollowUps follow, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Documents.SingleOrDefaultAsync(d => d.ContentHash == hash, ct) is not { } document) return forgotten ? null : false;
        if (document.TextForgottenUtc is not null == forgotten) return false;
        document.TextForgottenUtc = forgotten ? Now : null;
        await db.SaveChangesAsync(ct);
        if (await db.EntrySources.Where(s => s.DocumentId == document.Id && s.FirstPdfPage == null).Select(s => (long?)s.EntryId).FirstOrDefaultAsync(ct) is { } entryId)
            (forgotten ? follow.Forget : follow.ReadAgain).Add(new EntryId(entryId));
        return true;
    }

    /// <summary>
    /// Where a book was left, in the file read there. A book read here since keeps this device's place, which the other
    /// device then takes. Null while that file isn't here.
    /// </summary>
    async Task<bool?> UpsertReadingAsync(string hash, SyncedReading reading, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var source = await db.EntrySources.Where(s => s.Document.ContentHash == hash && s.FirstPdfPage == null)
            .Select(s => new { s.EntryId, s.DocumentId }).FirstOrDefaultAsync(ct);
        if (source is null) return null;
        var opened = DateTime.SpecifyKind(reading.Opened, DateTimeKind.Utc);
        var row = await db.ReadingStates.FindAsync([source.EntryId], ct);
        if (row is null) db.ReadingStates.Add(new ReadingState { EntryId = source.EntryId, DocumentId = source.DocumentId, PageIndex = reading.Page, OpenedUtc = opened });
        else if (row.OpenedUtc > opened) return false;
        else (row.DocumentId, row.PageIndex, row.OpenedUtc) = (source.DocumentId, reading.Page, opened);
        return await SaveAsync(db, ct);
    }

    /// <summary>
    /// A library folder here by library sync's name for it (<see cref="SyncFields.Root"/>), and how its file locations
    /// spell a folder under it; null when this device hasn't that library folder.
    /// </summary>
    async Task<(long Id, Func<string, string> Spell)?> RootAsync(CatalogDbContext db, string key, CancellationToken ct)
    {
        foreach (var root in await db.SourceRoots.AsNoTracking().ToListAsync(ct))
        {
            if (RootKey(root) != key) continue;
            var sample = await db.FileLocations.Where(f => f.SourceRootId == root.Id && (f.RelativePath.Contains('\\') || f.RelativePath.Contains('/')))
                .Select(f => f.RelativePath).FirstOrDefaultAsync(ct);
            var separator = sample is null ? Path.DirectorySeparatorChar : sample.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
            return (root.Id, folder => folder.Replace('/', separator));
        }
        return null;
    }

    /// <summary>
    /// A folder rule as answered on another device: made here when the folder hasn't proposed it, or answered as there;
    /// turning on a rule for a field with one value turns off its folder's others, as answering here does. Null while
    /// the library folder isn't here.
    /// </summary>
    async Task<bool?> UpsertFolderRuleAsync(IReadOnlyDictionary<string, string> wanted, SyncFollowUps follow, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await RootAsync(db, Read<string>(wanted, SyncFields.Root), ct) is not { } root) return null;
        var folder = Read<string>(wanted, SyncFields.Folder);
        var (field, value) = (Read<string>(wanted, SyncFields.Field), Read<string>(wanted, SyncFields.Value));
        var state = Enum.Parse<FolderRuleState>(Read<string>(wanted, SyncFields.State));
        var rules = await db.FolderRules.Where(r => r.SourceRootId == root.Id && r.Field == field).ToListAsync(ct);
        var rule = rules.FirstOrDefault(r => r.Value == value && SyncedFolder(r.Folder) == folder);
        if (rule?.State == state) return false;
        var now = Now;
        if (rule is null)
        {
            rule = new FolderRule { SourceRootId = root.Id, Folder = root.Spell(folder), Field = field, Value = value, CreatedUtc = now };
            db.FolderRules.Add(rule);
        }
        (rule.State, rule.Books, rule.DecidedUtc) = (state, ReadOrDefault<int>(wanted, SyncFields.Books), now);
        if (state == FolderRuleState.On && MetadataFields.Find(field) is { Multiple: false })
            foreach (var other in rules.Where(r => r != rule && r.State == FolderRuleState.On && SyncedFolder(r.Folder) == folder))
                (other.State, other.DecidedUtc) = (FolderRuleState.Off, now);
        await db.SaveChangesAsync(ct);
        follow.Folders.Add(new FolderKey(root.Id, rule.Folder));
        return true;
    }

    /// <summary>A folder's answer about a collection from it, as given on another device. Null while the library folder isn't here.</summary>
    async Task<bool?> UpsertFolderCollectionAsync(IReadOnlyDictionary<string, string> wanted, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await RootAsync(db, Read<string>(wanted, SyncFields.Root), ct) is not { } root) return null;
        var folder = Read<string>(wanted, SyncFields.Folder);
        var state = Enum.Parse<FolderCollectionState>(Read<string>(wanted, SyncFields.State));
        long? collectionId = GuidOf(wanted, SyncFields.Collection) is { } syncId
            ? await db.Collections.Where(c => c.SyncId == syncId).Select(c => (long?)c.Id).SingleOrDefaultAsync(ct)
            : null;
        var answers = await db.CollectionFolders.Where(f => f.SourceRootId == root.Id || (collectionId != null && f.CollectionId == collectionId)).ToListAsync(ct);
        var answer = answers.FirstOrDefault(f => f.SourceRootId == root.Id && SyncedFolder(f.Folder) == folder);
        if (answer is not null && answer.State == state && answer.CollectionId == collectionId) return false;
        // A collection follows one folder: one that followed another here follows this one now, as it does there.
        foreach (var other in answers.Where(f => f != answer && collectionId != null && f.CollectionId == collectionId)) other.CollectionId = null;
        if (answer is null)
        {
            answer = new CollectionFolder { SourceRootId = root.Id, Folder = root.Spell(folder) };
            db.CollectionFolders.Add(answer);
        }
        (answer.State, answer.CollectionId, answer.DecidedUtc) = (state, collectionId, Now);
        return await SaveAsync(db, ct);
    }

    /// <summary>A setting about the library, as set on another device. One this device doesn't share is left alone.</summary>
    async Task<bool> SetSettingAsync(string key, string value, CancellationToken ct)
    {
        if (!SettingKeys.Library.Contains(key)) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Settings.FindAsync([key], ct);
        if (row?.Value == value) return false;
        if (row is null) db.Settings.Add(new Setting { Key = key, Value = value });
        else row.Value = value;
        return await SaveAsync(db, ct);
    }

    /// <summary>A question asked of a book on another device, with its answer. Null while the book isn't here.</summary>
    async Task<bool?> UpsertAskedAsync(DateTime asked, IReadOnlyDictionary<string, string> wanted, Func<string, long?> card, CancellationToken ct)
    {
        if (card(Read<string>(wanted, SyncFields.Card)) is not { } entryId) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var turn = await db.AskTurns.FirstOrDefaultAsync(t => t.AskedUtc == asked, ct)
            ?? db.AskTurns.Add(new AskTurn { AskedUtc = asked, Question = "", Answer = "", Parts = "", CitedPages = "", Model = "" }).Entity;
        turn.EntryId = entryId;
        turn.Question = Read<string>(wanted, SyncFields.Question);
        turn.Rewritten = ReadOrDefault<string>(wanted, SyncFields.Rewritten);
        turn.Answer = Read<string>(wanted, SyncFields.Answer);
        turn.Parts = Read<string>(wanted, SyncFields.Parts);
        turn.CitedPages = Read<string>(wanted, SyncFields.Cited);
        turn.Model = Read<string>(wanted, SyncFields.Model);
        turn.NotAnswered = ReadOrDefault<bool>(wanted, SyncFields.NotAnswered);
        return await SaveAsync(db, ct);
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
    /// aliases (an alias another term of the vocabulary has here moves, as Settings > Vocabulary moves one). A term the
    /// AI proposed here too is decided as it was there, so Needs review is answered once for every device: accepted,
    /// it joins the vocabulary and the values waiting on it become suggestions; one whose name the other PC filed under
    /// another term is filed the same way; turned down, it and its values are turned down. A term turned down there
    /// that never got here is kept as turned down, so the AI proposing it here asks nothing. A term in use here is
    /// never turned down by sync. One combined into another there is combined the same way here, when both are here; a
    /// term that never got here isn't made just to be combined.
    /// </summary>
    async Task<bool?> UpsertTermAsync(string key, IReadOnlyDictionary<string, string> wanted, CancellationToken ct)
    {
        var parts = key[SyncKeys.Term.Length..].Split(':', 2);
        if (parts.Length != 2) return false;
        var (vocabulary, termKey) = (parts[0], parts[1]);
        if (ReadOrDefault<string>(wanted, SyncFields.MergedInto) is { Length: > 0 } into)
        {
            if (await PendingTermAsync(vocabulary, termKey, ct) is { } waiting && await ActiveTermAsync(vocabulary, into, ct))
            {
                await vocabularies.MapPendingAsync(waiting, into, ct);
                return true;
            }
            return await vocabularies.MergeByKeyAsync(vocabulary, into, termKey, ct);
        }
        var label = Read<string>(wanted, SyncFields.Label);
        if (ReadOrDefault<bool>(wanted, SyncFields.Rejected)) return await RejectTermAsync(vocabulary, termKey, label, ct);

        await using var db = await contexts.CreateDbContextAsync(ct);
        var term = await db.VocabularyTerms.Include(t => t.Aliases).SingleOrDefaultAsync(t => t.Vocabulary == vocabulary && t.Key == termKey, ct);
        var changed = false;
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
        else if (term.State == TermState.Pending)
        {
            await vocabularies.AcceptPendingAsync(term.Id, ct);
            db.ChangeTracker.Clear();
            term = await db.VocabularyTerms.Include(t => t.Aliases).SingleAsync(t => t.Id == term.Id, ct);
            changed = true;
        }
        else if (term.State == TermState.Rejected)
        {
            term.State = TermState.Active;
            changed = true;
        }
        else if (term.State != TermState.Active) return false;
        term.Label = label;
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
        changed |= await SaveAsync(db, ct);

        // A proposal here with one of the term's names was filed under it there: it is filed under it here too.
        var named = names.Append(label).Append(term.ShortLabel ?? "").Where(n => n.Length > 0).Select(MetadataText.Normalize).ToHashSet(StringComparer.Ordinal);
        var proposals = await db.VocabularyTerms.AsNoTracking().Where(t => t.Vocabulary == vocabulary && t.State == TermState.Pending && t.Id != term.Id)
            .Select(t => new { t.Id, t.Label }).ToListAsync(ct);
        foreach (var proposal in proposals.Where(p => named.Contains(MetadataText.Normalize(p.Label))))
        {
            await vocabularies.MapPendingAsync(proposal.Id, termKey, ct);
            changed = true;
        }
        return changed;
    }

    /// <summary>A term turned down on another PC: turned down here if it waits here, kept as turned down if it was never here.</summary>
    async Task<bool> RejectTermAsync(string vocabulary, string termKey, string label, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var term = await db.VocabularyTerms.SingleOrDefaultAsync(t => t.Vocabulary == vocabulary && t.Key == termKey, ct);
        if (term is null)
        {
            db.VocabularyTerms.Add(new VocabularyTerm
            {
                Vocabulary = vocabulary,
                Key = termKey,
                Label = label,
                Origin = TermOrigin.Model,
                State = TermState.Rejected,
                CreatedUtc = Now,
            });
            return await SaveAsync(db, ct);
        }
        if (term.State != TermState.Pending) return false;
        // Turned down there because its name was filed under another term: filed the same way here.
        var name = MetadataText.Normalize(label);
        var filedUnder = (await db.VocabularyTerms.AsNoTracking().Include(t => t.Aliases).Where(t => t.Vocabulary == vocabulary && t.State == TermState.Active).ToListAsync(ct))
            .FirstOrDefault(t => MetadataText.Normalize(t.Label) == name || t.Aliases.Any(a => a.Normalized == name));
        if (filedUnder is not null) await vocabularies.MapPendingAsync(term.Id, filedUnder.Key, ct);
        else await vocabularies.RejectPendingAsync(term.Id, ct);
        return true;
    }

    async Task<long?> PendingTermAsync(string vocabulary, string termKey, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.VocabularyTerms.Where(t => t.Vocabulary == vocabulary && t.Key == termKey && t.State == TermState.Pending)
            .Select(t => (long?)t.Id).SingleOrDefaultAsync(ct);
    }

    async Task<bool> ActiveTermAsync(string vocabulary, string termKey, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.VocabularyTerms.AnyAsync(t => t.Vocabulary == vocabulary && t.Key == termKey && t.State == TermState.Active, ct);
    }

    async Task<bool> UpsertKeptApartAsync(string vocabulary, string first, string second, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.VocabularyKeptAparts.AnyAsync(k => k.Vocabulary == vocabulary && k.FirstKey == first && k.SecondKey == second, ct)) return false;
        db.VocabularyKeptAparts.Add(new VocabularyKeptApart { Vocabulary = vocabulary, FirstKey = first, SecondKey = second, CreatedUtc = Now });
        return await SaveAsync(db, ct);
    }

    /// <summary>One of the user's own tags as the other PC has it, by its name in comparison form; its name joins <paramref name="tags"/> when it changed here.</summary>
    async Task<bool> UpsertUserTagAsync(string normalized, IReadOnlyDictionary<string, string> wanted, HashSet<string> tags, CancellationToken ct)
    {
        var name = Read<string>(wanted, SyncFields.Name);
        var words = JsonSerializer.Serialize(ReadOrDefault<List<string>>(wanted, SyncFields.Words) ?? []);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.UserTags.SingleOrDefaultAsync(t => t.NormalizedName == normalized, ct);
        if (row is null)
            row = db.UserTags.Add(new UserTag { Name = name, NormalizedName = normalized, WordsJson = words, CreatedUtc = Now, UpdatedUtc = Now }).Entity;
        else if (row.Name == name && row.WordsJson == words) return false;
        else
        {
            row.Name = name;
            row.WordsJson = words;
            row.UpdatedUtc = Now;
        }
        await db.SaveChangesAsync(ct);
        tags.Add(name);
        return true;
    }

    /// <summary>
    /// "Not the same book" from another device: remembered, and when the two files are still one card here, the second
    /// leaves it, as Not the same book takes a copy out there.
    /// </summary>
    async Task<bool> UpsertNotSameBookAsync(string key, CancellationToken ct)
    {
        var parts = key[SyncKeys.NotSameBook.Length..].Split(':');
        if (parts.Length != 2) return false;
        var changed = false;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (!await db.CopyDecisions.AnyAsync(d => d.FirstHash == parts[0] && d.SecondHash == parts[1], ct))
            {
                db.CopyDecisions.Add(new CopyDecision { FirstHash = parts[0], SecondHash = parts[1], Answer = CopyAnswer.NotSameBook, CreatedUtc = Now });
                changed = await SaveAsync(db, ct);
            }
            var copies = await db.EntrySources.AsNoTracking().Where(s => s.FirstPdfPage == null && (s.Document.ContentHash == parts[0] || s.Document.ContentHash == parts[1])
                    && s.Entry.MergedIntoEntryId == null)
                .Select(s => new { s.EntryId, s.DocumentId, s.Document.ContentHash }).ToListAsync(ct);
            if (copies.Select(c => c.EntryId).Distinct().Count() != 1 || copies.FirstOrDefault(c => c.ContentHash == parts[1]) is not { } leaving
                || copies.All(c => c.ContentHash != parts[0])) return changed;
            return await new EntryStore(contexts, _clock).SplitCopyAsync(new EntryId(leaving.EntryId), leaving.DocumentId, remember: false, ct) is not null || changed;
        }
    }

    /// <summary>
    /// Two cards that are one book on another device (Same book, a new version, a file that met its book owned
    /// elsewhere) become one here, as Same book joins them. Cards the user said aren't the same book stay apart, as do
    /// packs, images in packs and adventures. Null while either isn't here.
    /// </summary>
    async Task<bool?> JoinAsync(string first, string second, Func<string, long?> card, CancellationToken ct)
    {
        static string KeyOf(string name) => Guid.TryParseExact(name, "D", out var id) ? SyncKeys.For(SyncKeys.Elsewhere, id) : SyncKeys.ForBook(name);
        if (card(KeyOf(first)) is not { } one || card(KeyOf(second)) is not { } other) return null;
        if (one == other) return false;
        await using var db = await contexts.CreateDbContextAsync(ct);
        List<long> group = [one, other];
        var cards = await db.Entries.AsNoTracking().Where(e => group.Contains(e.Id)).ToListAsync(ct);
        if (cards.Count != 2 || cards.Any(c => c.MergedIntoEntryId is not null || c.ParentEntryId is not null || c.Kind is not (EntryKind.Whole or EntryKind.Elsewhere)))
            return false;
        var copies = await db.EntrySources.AsNoTracking().Where(s => group.Contains(s.EntryId) && s.FirstPdfPage == null)
            .Select(s => new { s.EntryId, s.DocumentId, s.IsCurrent, s.Document.ContentHash }).ToListAsync(ct);
        var hashes = copies.Select(c => c.ContentHash).Distinct().ToList();
        if (await db.CopyDecisions.AnyAsync(d => d.Answer == CopyAnswer.NotSameBook && hashes.Contains(d.FirstHash) && hashes.Contains(d.SecondHash), ct)) return false;
        // The copy that opens is the one the older card opens, as joining copies keeps it.
        if (copies.OrderBy(c => c.EntryId).ThenByDescending(c => c.IsCurrent).Select(c => (long?)c.DocumentId).FirstOrDefault() is not { } opens) return false;
        return await new EntryStore(contexts, _clock).JoinAsOneBookAsync([new EntryId(one), new EntryId(other)], opens, ct) is not null;
    }

    async Task<bool> UpsertPartProposalAsync(string hash, PartProposalAnswer answer, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.PartProposals.SingleOrDefaultAsync(p => p.ContentHash == hash, ct);
        if (row?.Answer == answer) return false;
        if (row is null)
            row = db.PartProposals.Add(new PartProposal { ContentHash = hash, Source = PartProposalSource.Rules, PartsJson = "[]", CreatedUtc = Now }).Entity;
        row.Answer = answer;
        row.DecidedUtc = Now;
        return await SaveAsync(db, ct);
    }

    /// <summary>"Separate book" for a file and a book owned elsewhere: the pair isn't asked about here either, now or when the file arrives.</summary>
    async Task<bool?> UpsertSeparateFromElsewhereAsync(Guid elsewhere, string hash, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var entryId = await db.Entries.Where(e => e.SyncId == elsewhere).Select(e => (long?)e.Id).SingleOrDefaultAsync(ct);
        var documentId = await db.Documents.Where(d => d.ContentHash == hash).Select(d => (long?)d.Id).SingleOrDefaultAsync(ct);
        if (entryId is null || documentId is null) return null;
        var rows = await db.ElsewhereMatches.Where(m => m.EntryId == entryId && m.DocumentId == documentId).ToListAsync(ct);
        if (rows.Count == 0) db.ElsewhereMatches.Add(new ElsewhereMatch { EntryId = entryId.Value, DocumentId = documentId.Value, Answer = ElsewhereAnswer.SeparateBook, CreatedUtc = Now });
        foreach (var row in rows) row.Answer = ElsewhereAnswer.SeparateBook;
        return await SaveAsync(db, ct);
    }

    static async Task<List<ElsewhereMatch>> SeparateMatchesAsync(CatalogDbContext db, Guid elsewhere, string hash, CancellationToken ct) =>
        await db.ElsewhereMatches.Where(m => m.Answer == ElsewhereAnswer.SeparateBook
            && db.Entries.Any(e => e.Id == m.EntryId && e.SyncId == elsewhere) && db.Documents.Any(d => d.Id == m.DocumentId && d.ContentHash == hash)).ToListAsync(ct);

    /// <summary>
    /// An adventure another device marked, made here in the copy of its file with the same content, under the same
    /// sync id. Null when that copy isn't here, or the pages overlap an adventure marked here.
    /// </summary>
    async Task<long?> AddAdventureAsync(Guid syncId, IReadOnlyDictionary<string, string> wanted, CancellationToken ct)
    {
        if (ReadOrDefault<SyncedPages>(wanted, SyncFields.Pages) is not { } pages) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var copy = await db.EntrySources.AsNoTracking().Where(s => s.FirstPdfPage == null && s.Document.ContentHash == pages.Hash && s.Entry.MergedIntoEntryId == null)
            .Select(s => new { s.EntryId, s.DocumentId }).FirstOrDefaultAsync(ct);
        if (copy is null) return null;
        var title = ReadOrDefault<SyncedMetadata>(wanted, SyncFields.MetadataPrefix + MetadataFields.Title.Key)?.Values is [var first, ..] ? first : "";
        var source = new PartSource(copy.DocumentId, new PartPages(pages.First, pages.Last), pages.FirstFingerprint, pages.LastFingerprint);
        if (await new PartStore(contexts, _clock).AddAsync(new EntryId(copy.EntryId), source, title, ct) is not { } part) return null;
        var row = await db.Entries.SingleAsync(e => e.Id == part.Value, ct);
        row.SyncId = syncId;
        await db.SaveChangesAsync(ct);
        return part.Value;
    }

    /// <summary>An adventure whose pages the other device changed: its pages in that copy are changed here too. Null when they can't be.</summary>
    async Task<bool?> MoveAdventureAsync(long partId, IReadOnlyDictionary<string, string> wanted, CancellationToken ct)
    {
        if (ReadOrDefault<SyncedPages>(wanted, SyncFields.Pages) is not { } pages) return false;
        var parts = new PartStore(contexts, _clock);
        if (await parts.GetPartAsync(new EntryId(partId), ct) is not { } part) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Documents.Where(d => d.ContentHash == pages.Hash).Select(d => (long?)d.Id).SingleOrDefaultAsync(ct) is not { } documentId) return null;
        var moved = new PartSource(documentId, new PartPages(pages.First, pages.Last), pages.FirstFingerprint, pages.LastFingerprint);
        if (part.On(documentId) is { } there && there.Pages == moved.Pages) return false;
        var sources = part.Sources.Where(s => s.DocumentId != documentId).Append(moved).ToList();
        var current = part.Sources.FirstOrDefault(s => s.IsCurrent)?.DocumentId ?? documentId;
        return await parts.SetSourcesAsync(new EntryId(partId), sources, current, ct) ? true : null;
    }

    /// <summary>
    /// Deletes what another PC deleted, as this PC's own pages would: a collection's sub-collections move up a level,
    /// a binder's and an item's page references go with them, a book owned elsewhere goes with everything set on it.
    /// A card with files is never deleted this way. Returns whether anything was there to delete.
    /// </summary>
    async Task<bool> DeleteAsync(string key, Func<string, long?> card, SyncFollowUps follow, CancellationToken ct)
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
        else if (SyncKeys.KeptApartOf(key) is { } apart)
            deleted = await db.VocabularyKeptAparts.Where(k => k.Vocabulary == apart.Vocabulary && k.FirstKey == apart.First && k.SecondKey == apart.Second)
                .ExecuteDeleteAsync(ct) > 0;
        else if (key.StartsWith(SyncKeys.UserTag, StringComparison.Ordinal))
        {
            var normalized = key[SyncKeys.UserTag.Length..];
            if (await db.UserTags.SingleOrDefaultAsync(t => t.NormalizedName == normalized, ct) is { } row)
            {
                db.UserTags.Remove(row);
                follow.Tags.Add(row.Name);
                deleted = true;
            }
        }
        // Two files that stopped being one book are parted by Not the same book, which travels on its own.
        else if (SyncKeys.SameBookOf(key) is not null) { }
        else if (SyncKeys.IdOf(key, SyncKeys.Adventure) is { } && card(key) is { } partId)
        {
            await transaction.CommitAsync(ct);
            return await new PartStore(contexts, _clock).RemoveAsync(new EntryId(partId), ct) is not null;
        }
        else if (key.StartsWith(SyncKeys.PartProposal, StringComparison.Ordinal))
        {
            var hash = key[SyncKeys.PartProposal.Length..];
            if (await db.PartProposals.SingleOrDefaultAsync(p => p.ContentHash == hash && p.Answer != null, ct) is { } row)
            {
                // Asked about again, as Undo leaves it; one made only to hold the answer goes.
                if (row.PartsJson == "[]") db.PartProposals.Remove(row);
                else (row.Answer, row.DecidedUtc) = (null, null);
                deleted = true;
            }
        }
        else if (SyncKeys.SeparateFromElsewhereOf(key) is { } separate)
        {
            foreach (var row in await SeparateMatchesAsync(db, separate.Elsewhere, separate.Hash, ct))
            {
                row.Answer = null;
                deleted = true;
            }
        }
        else if (key.StartsWith(SyncKeys.CoverPage, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(ct);
            return await SetCoverPageAsync(key[SyncKeys.CoverPage.Length..], 0, follow, ct) is true;
        }
        else if (key.StartsWith(SyncKeys.Forgotten, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(ct);
            return await SetForgottenAsync(key[SyncKeys.Forgotten.Length..], false, follow, ct) is true;
        }
        else if (key.StartsWith(SyncKeys.Reading, StringComparison.Ordinal))
        {
            // Only the place in that file: a book read in another of its files since keeps its place there.
            var hash = key[SyncKeys.Reading.Length..];
            deleted = await db.ReadingStates
                .Where(r => db.EntrySources.Any(s => s.EntryId == r.EntryId && s.DocumentId == r.DocumentId && s.FirstPdfPage == null && s.Document.ContentHash == hash))
                .ExecuteDeleteAsync(ct) > 0;
        }
        else if (SyncKeys.AskedOf(key) is { } asked)
            deleted = await db.AskTurns.Where(t => t.AskedUtc == asked).ExecuteDeleteAsync(ct) > 0;
        else if (key.StartsWith(SyncKeys.FolderRule, StringComparison.Ordinal) || key.StartsWith(SyncKeys.FolderCollection, StringComparison.Ordinal))
        {
            // The key is a hash, so the row it named is found by working out each row's.
            var roots = (await db.SourceRoots.AsNoTracking().ToListAsync(ct)).Select(r => (r.Id, Key: RootKey(r))).Where(r => r.Key is not null)
                .ToDictionary(r => r.Id, r => r.Key!);
            if (key.StartsWith(SyncKeys.FolderRule, StringComparison.Ordinal))
            {
                foreach (var rule in (await db.FolderRules.Where(r => r.State != FolderRuleState.Proposed).ToListAsync(ct)).Where(r => roots.ContainsKey(r.SourceRootId)
                    && SyncKeys.ForHashed(SyncKeys.FolderRule, roots[r.SourceRootId], SyncedFolder(r.Folder), r.Field, r.Value) == key))
                {
                    db.FolderRules.Remove(rule);
                    follow.Folders.Add(new FolderKey(rule.SourceRootId, rule.Folder));
                    deleted = true;
                }
            }
            else
                foreach (var answer in (await db.CollectionFolders.ToListAsync(ct)).Where(f => roots.ContainsKey(f.SourceRootId)
                    && SyncKeys.ForHashed(SyncKeys.FolderCollection, roots[f.SourceRootId], SyncedFolder(f.Folder)) == key))
                {
                    db.CollectionFolders.Remove(answer);
                    deleted = true;
                }
        }
        else if (key.StartsWith(SyncKeys.Setting, StringComparison.Ordinal))
        {
            // Back to its default, as the other device has it.
            var name = key[SyncKeys.Setting.Length..];
            if (SettingKeys.Library.Contains(name)) deleted = await db.Settings.Where(s => s.Key == name).ExecuteDeleteAsync(ct) > 0;
        }
        else if (key.StartsWith(SyncKeys.NotSameBook, StringComparison.Ordinal) && key[SyncKeys.NotSameBook.Length..].Split(':') is [var first, var second])
            deleted = await db.CopyDecisions.Where(d => d.FirstHash == first && d.SecondHash == second).ExecuteDeleteAsync(ct) > 0;
        else if (SyncKeys.LookupRecordOf(key) is { } record && lookups is not null)
        {
            await transaction.CommitAsync(ct);
            return await lookups.ApplySyncedAsync(record.Hash, record.Source, record.RecordKey, null, ct);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }
}
