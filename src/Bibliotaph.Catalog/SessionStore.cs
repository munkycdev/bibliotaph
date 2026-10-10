using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>
/// Pages of one document, as a session item points at them: zero-based PDF pages, inclusive, with the printed labels
/// and fingerprints of the first and last pages (slice 3 plan, choice 14).
/// </summary>
public sealed record PageRange(long DocumentId, int FirstPdfPage, int LastPdfPage, string? FirstLabel = null, string? LastLabel = null,
    string? FirstFingerprint = null, string? LastFingerprint = null)
{
    public bool IsSinglePage => FirstPdfPage == LastPdfPage;
}

/// <summary>A pack as the Sessions page shows it: how many items it has, and the books of its first two, for covers.</summary>
public sealed record SessionPackInfo(long Id, string Title, DateOnly? Date, string? Notes, DateTime TouchedUtc, int ItemCount, IReadOnlyList<EntryId> FirstEntries);

public sealed record SessionSectionInfo(long Id, string Name, int Position);

public sealed record SessionItemInfo(long Id, long PackId, long? SectionId, int Position, EntryId EntryId, PageRange? Range, string? Label, string? Note, DateTime AddedUtc);

/// <summary>A pack with its sections in order, and its items in the order they show: those before any section, then each section's.</summary>
public sealed record SessionPackContents(SessionPackInfo Pack, IReadOnlyList<SessionSectionInfo> Sections, IReadOnlyList<SessionItemInfo> Items);

/// <summary>Something to add to a pack: a whole book, or a page range of it, with a label and note if given.</summary>
public sealed record NewSessionItem(EntryId EntryId, PageRange? Range = null, string? Label = null, string? Note = null);

/// <summary>Everything in a pack, for Undo after deleting it: <see cref="SessionStore.RestoreAsync"/> makes it again.</summary>
public sealed record SessionPackSnapshot(string Title, DateOnly? Date, string? Notes, DateTime CreatedUtc, IReadOnlyList<SessionSectionInfo> Sections,
    IReadOnlyList<SessionItemInfo> Items);

/// <summary>
/// Session packs (slice 3 plan, choices 11 to 15): ordered items, each a book or a page range of one, in optional
/// sections. Items before the first section come first; each section's follow in the sections' order. Every change
/// touches the pack, so the pack worked on last is the current one.
/// </summary>
public sealed class SessionStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>For <see cref="AddItemsAsync"/>: the items before the first section.</summary>
    public const long NoSection = 0;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>Every pack, the current one (touched last) first.</summary>
    public async Task<IReadOnlyList<SessionPackInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var packs = await db.SessionPacks.AsNoTracking().OrderByDescending(p => p.TouchedUtc).ThenByDescending(p => p.Id).ToListAsync(ct);
        var sections = await db.SessionSections.AsNoTracking().ToListAsync(ct);
        var items = await db.SessionItems.AsNoTracking().Select(i => new { i.Id, i.PackId, i.SectionId, i.Position, i.EntryId }).ToListAsync(ct);
        return [.. packs.Select(p =>
        {
            var order = SectionOrder(sections.Where(s => s.PackId == p.Id));
            var own = items.Where(i => i.PackId == p.Id).OrderBy(i => order[i.SectionId ?? NoSection]).ThenBy(i => i.Position).ThenBy(i => i.Id).ToList();
            return new SessionPackInfo(p.Id, p.Title, p.Date, p.Notes, p.TouchedUtc, own.Count, [.. own.Select(i => new EntryId(i.EntryId)).Distinct().Take(2)]);
        })];
    }

    public async Task<SessionPackContents?> GetAsync(long packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await ContentsAsync(db, packId, ct);
    }

    public async Task<SessionPackInfo> CreateAsync(string title, DateOnly? date = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var now = Now;
        var pack = new SessionPack { Title = Title(title), Date = date, CreatedUtc = now, TouchedUtc = now };
        db.SessionPacks.Add(pack);
        await db.SaveChangesAsync(ct);
        return new SessionPackInfo(pack.Id, pack.Title, pack.Date, null, now, 0, []);
    }

    /// <summary>Changes a pack's title and date. Returns false if it doesn't exist.</summary>
    public async Task<bool> UpdateAsync(long packId, string title, DateOnly? date, CancellationToken ct = default)
    {
        var name = Title(title);
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SessionPacks.Where(p => p.Id == packId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Title, name).SetProperty(p => p.Date, date).SetProperty(p => p.TouchedUtc, Now), ct) > 0;
    }

    public async Task<bool> SetNotesAsync(long packId, string? notes, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(notes) ? null : notes;
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SessionPacks.Where(p => p.Id == packId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, text).SetProperty(p => p.TouchedUtc, Now), ct) > 0;
    }

    /// <summary>Opening a pack makes it the current one (choice 13).</summary>
    public async Task<bool> TouchAsync(long packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.SessionPacks.Where(p => p.Id == packId).ExecuteUpdateAsync(s => s.SetProperty(p => p.TouchedUtc, Now), ct) > 0;
    }

    /// <summary>Deletes a pack, never its books. Returns what Undo needs, or null if it doesn't exist.</summary>
    public async Task<SessionPackSnapshot?> DeleteAsync(long packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await ContentsAsync(db, packId, ct) is not { } contents) return null;
        var pack = await db.SessionPacks.SingleAsync(p => p.Id == packId, ct);
        var pageRefs = await db.SessionItems.Where(i => i.PackId == packId && i.PageRefId != null).Select(i => i.PageRefId!.Value).ToListAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.SessionPacks.Remove(pack);
        await db.SaveChangesAsync(ct);
        await db.PageRefs.Where(r => pageRefs.Contains(r.Id)).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return new SessionPackSnapshot(pack.Title, pack.Date, pack.Notes, pack.CreatedUtc, contents.Sections, contents.Items);
    }

    /// <summary>Undoes <see cref="DeleteAsync"/>: the pack comes back with its sections and the items whose books are still in the catalog.</summary>
    public async Task<SessionPackInfo> RestoreAsync(SessionPackSnapshot snapshot, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var pack = await CopyAsync(db, snapshot, snapshot.Title, snapshot.Date, snapshot.CreatedUtc, ct);
        return (await ContentsAsync(db, pack.Id, ct))!.Pack;
    }

    /// <summary>
    /// Duplicate (choice 11): a new pack with the same sections, items, labels and notes, each page range a new
    /// reference to the same pages. It has no date, since it is usually for another session.
    /// </summary>
    public async Task<SessionPackInfo?> DuplicateAsync(long packId, string title, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await ContentsAsync(db, packId, ct) is not { } contents) return null;
        var snapshot = new SessionPackSnapshot(contents.Pack.Title, null, contents.Pack.Notes, Now, contents.Sections, contents.Items);
        var pack = await CopyAsync(db, snapshot, Title(title), null, Now, ct);
        return (await ContentsAsync(db, pack.Id, ct))!.Pack;
    }

    async Task<SessionPack> CopyAsync(CatalogDbContext db, SessionPackSnapshot snapshot, string title, DateOnly? date, DateTime created, CancellationToken ct)
    {
        var entryIds = snapshot.Items.Select(i => i.EntryId.Value).Distinct().ToList();
        var known = (await db.Entries.Where(e => entryIds.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var documentIds = snapshot.Items.Where(i => i.Range is not null).Select(i => i.Range!.DocumentId).Distinct().ToList();
        var documents = (await db.Documents.Where(d => documentIds.Contains(d.Id)).Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var pack = new SessionPack { Title = title, Date = date, Notes = snapshot.Notes, CreatedUtc = created, TouchedUtc = Now };
        db.SessionPacks.Add(pack);
        await db.SaveChangesAsync(ct);
        var sections = snapshot.Sections.ToDictionary(s => s.Id, s => new SessionSection { PackId = pack.Id, Name = s.Name, Position = s.Position });
        db.SessionSections.AddRange(sections.Values);
        await db.SaveChangesAsync(ct);
        foreach (var item in snapshot.Items.Where(i => known.Contains(i.EntryId.Value)))
        {
            db.SessionItems.Add(new SessionItem
            {
                PackId = pack.Id,
                SectionId = item.SectionId is { } s && sections.TryGetValue(s, out var section) ? section.Id : null,
                Position = item.Position,
                EntryId = item.EntryId.Value,
                PageRef = item.Range is { } range && documents.Contains(range.DocumentId) ? ToPageRef(range) : null,
                Label = item.Label,
                Note = item.Note,
                AddedUtc = item.AddedUtc,
            });
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return pack;
    }

    /// <summary>
    /// Adds items to a pack, after the others of <paramref name="sectionId"/>: <see cref="NoSection"/> for those before
    /// the first section, or null for the end of the pack (its last section, if it has any). Returns the new items' ids.
    /// </summary>
    public async Task<IReadOnlyList<long>> AddItemsAsync(long packId, IReadOnlyList<NewSessionItem> items, long? sectionId = null, CancellationToken ct = default)
    {
        if (items.Count == 0) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.SessionPacks.AnyAsync(p => p.Id == packId, ct)) throw new InvalidOperationException($"Session pack {packId} doesn't exist.");
        var sections = await db.SessionSections.Where(s => s.PackId == packId).ToListAsync(ct);
        long? target = sectionId switch
        {
            null => sections.OrderBy(s => s.Position).ThenBy(s => s.Id).LastOrDefault()?.Id,
            NoSection => null,
            { } id when sections.Any(s => s.Id == id) => id,
            _ => throw new InvalidOperationException($"Section {sectionId} isn't in session pack {packId}."),
        };
        var position = await db.SessionItems.Where(i => i.PackId == packId && i.SectionId == target).MaxAsync(i => (int?)i.Position, ct) ?? -1;
        var now = Now;
        var added = items.Select(item => new SessionItem
        {
            PackId = packId,
            SectionId = target,
            Position = ++position,
            EntryId = item.EntryId.Value,
            PageRef = item.Range is { } range ? ToPageRef(range) : null,
            Label = Blank(item.Label),
            Note = Blank(item.Note),
            AddedUtc = now,
        }).ToList();
        db.SessionItems.AddRange(added);
        await Touch(db, packId, ct);
        await db.SaveChangesAsync(ct);
        return [.. added.Select(i => i.Id)];
    }

    /// <summary>Takes items out of their packs. Returns them as they were, for Undo.</summary>
    public async Task<IReadOnlyList<SessionItemInfo>> RemoveItemsAsync(IReadOnlyCollection<long> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        var items = await db.SessionItems.Include(i => i.PageRef).Where(i => itemIds.Contains(i.Id)).ToListAsync(ct);
        if (items.Count == 0) return [];
        var removed = items.Select(ToInfo).ToList();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.SessionItems.RemoveRange(items);
        db.PageRefs.RemoveRange(items.Where(i => i.PageRef is not null).Select(i => i.PageRef!));
        foreach (var packId in items.Select(i => i.PackId).Distinct()) await Touch(db, packId, ct);
        await db.SaveChangesAsync(ct);
        foreach (var packId in items.Select(i => i.PackId).Distinct()) await RenumberAsync(db, packId, ct);
        await transaction.CommitAsync(ct);
        return removed;
    }

    /// <summary>
    /// Undoes <see cref="RemoveItemsAsync"/>: each item goes back to its place, in its section if that is still there,
    /// if its pack and book are. Returns the new items' ids.
    /// </summary>
    public async Task<IReadOnlyList<long>> RestoreItemsAsync(IReadOnlyList<SessionItemInfo> removed, CancellationToken ct = default)
    {
        if (removed.Count == 0) return [];
        await using var db = await contexts.CreateDbContextAsync(ct);
        var restored = new List<long>();
        foreach (var group in removed.GroupBy(i => i.PackId))
        {
            if (!await db.SessionPacks.AnyAsync(p => p.Id == group.Key, ct)) continue;
            var sections = (await db.SessionSections.Where(s => s.PackId == group.Key).Select(s => s.Id).ToListAsync(ct)).ToHashSet();
            var items = await db.SessionItems.Where(i => i.PackId == group.Key).ToListAsync(ct);
            var rows = new List<SessionItem>();
            foreach (var item in group.OrderBy(i => i.Position))
            {
                if (!await db.Entries.AnyAsync(e => e.Id == item.EntryId.Value, ct)) continue;
                var section = item.SectionId is { } s && sections.Contains(s) ? item.SectionId : null;
                var siblings = items.Where(i => i.SectionId == section).OrderBy(i => i.Position).ThenBy(i => i.Id).ToList();
                var row = new SessionItem
                {
                    PackId = group.Key,
                    SectionId = section,
                    EntryId = item.EntryId.Value,
                    PageRef = item.Range is { } range && await db.Documents.AnyAsync(d => d.Id == range.DocumentId, ct) ? ToPageRef(range) : null,
                    Label = item.Label,
                    Note = item.Note,
                    AddedUtc = item.AddedUtc,
                };
                siblings.Insert(Math.Clamp(item.Position, 0, siblings.Count), row);
                for (var i = 0; i < siblings.Count; i++) siblings[i].Position = i;
                db.SessionItems.Add(row);
                items.Add(row);
                rows.Add(row);
            }
            await Touch(db, group.Key, ct);
            await db.SaveChangesAsync(ct);
            restored.AddRange(rows.Select(r => r.Id));
        }
        return restored;
    }

    /// <summary>Changes an item's label and note; a blank label shows the book's title again.</summary>
    public async Task<bool> UpdateItemAsync(long itemId, string? label, string? note, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.SessionItems.FindAsync([itemId], ct) is not { } item) return false;
        item.Label = Blank(label);
        item.Note = Blank(note);
        await Touch(db, item.PackId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>"Use this page" (choice 14): the item points at these pages from now on.</summary>
    public async Task<bool> RepointAsync(long itemId, PageRange range, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.SessionItems.Include(i => i.PageRef).SingleOrDefaultAsync(i => i.Id == itemId, ct) is not { } item) return false;
        if (item.PageRef is { } old) db.PageRefs.Remove(old);
        item.PageRef = ToPageRef(range);
        await Touch(db, item.PackId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Move up (<paramref name="by"/> −1) or Move down (+1), as Alt+Up and Alt+Down do (choice 12): past the next item
    /// in its section, or over a section's heading into the section above or below. Returns false at either end.
    /// </summary>
    public async Task<bool> MoveItemAsync(long itemId, int by, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.SessionItems.FindAsync([itemId], ct) is not { } item) return false;
        var groups = await GroupsAsync(db, item.PackId, ct);
        var group = groups.FindIndex(g => g.SectionId == item.SectionId);
        var index = groups[group].Items.IndexOf(item);
        if (by < 0)
        {
            if (index > 0) (groups[group].Items[index - 1], groups[group].Items[index]) = (groups[group].Items[index], groups[group].Items[index - 1]);
            else if (group > 0) Move(groups, item, group - 1, groups[group - 1].Items.Count);
            else return false;
        }
        else
        {
            if (index < groups[group].Items.Count - 1) (groups[group].Items[index + 1], groups[group].Items[index]) = (groups[group].Items[index], groups[group].Items[index + 1]);
            else if (group < groups.Count - 1) Move(groups, item, group + 1, 0);
            else return false;
        }
        Number(groups);
        await Touch(db, item.PackId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A drag (choice 12): puts an item at <paramref name="index"/> among the items of a section, or of <see cref="NoSection"/>.</summary>
    public async Task<bool> MoveItemToAsync(long itemId, long sectionId, int index, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.SessionItems.FindAsync([itemId], ct) is not { } item) return false;
        var groups = await GroupsAsync(db, item.PackId, ct);
        long? target = sectionId == NoSection ? null : sectionId;
        var group = groups.FindIndex(g => g.SectionId == target);
        if (group < 0) return false;
        Move(groups, item, group, index);
        Number(groups);
        await Touch(db, item.PackId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Add section (choice 11): a heading at the end of the pack, for the items added after it.</summary>
    public async Task<SessionSectionInfo> AddSectionAsync(long packId, string name, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.SessionPacks.AnyAsync(p => p.Id == packId, ct)) throw new InvalidOperationException($"Session pack {packId} doesn't exist.");
        var position = await db.SessionSections.Where(s => s.PackId == packId).MaxAsync(s => (int?)s.Position, ct) ?? -1;
        var section = new SessionSection { PackId = packId, Name = Title(name), Position = position + 1 };
        db.SessionSections.Add(section);
        await Touch(db, packId, ct);
        await db.SaveChangesAsync(ct);
        return new SessionSectionInfo(section.Id, section.Name, section.Position);
    }

    public async Task<bool> RenameSectionAsync(long sectionId, string name, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.SessionSections.FindAsync([sectionId], ct) is not { } section) return false;
        section.Name = Title(name);
        await Touch(db, section.PackId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Deletes a heading, never its items: they join the end of the section above, or the items before the first section.</summary>
    public async Task<bool> DeleteSectionAsync(long sectionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.SessionSections.FindAsync([sectionId], ct) is not { } section) return false;
        var groups = await GroupsAsync(db, section.PackId, ct);
        var index = groups.FindIndex(g => g.SectionId == sectionId);
        var above = groups[index - 1];
        foreach (var item in groups[index].Items)
        {
            item.SectionId = above.SectionId;
            above.Items.Add(item);
        }
        groups.RemoveAt(index);
        Number(groups);
        db.SessionSections.Remove(section);
        var sections = await db.SessionSections.Where(s => s.PackId == section.PackId && s.Id != sectionId).OrderBy(s => s.Position).ThenBy(s => s.Id).ToListAsync(ct);
        for (var i = 0; i < sections.Count; i++) sections[i].Position = i;
        await Touch(db, section.PackId, ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The books in each pack, as entry_scope groups ("session:3"), for <paramref name="entryIds"/> or every entry.</summary>
    public async Task<IReadOnlyList<(EntryId EntryId, string Scope)>> GetScopesAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var items = db.SessionItems.AsNoTracking();
        if (entryIds is not null)
        {
            var ids = entryIds.Select(e => e.Value).ToList();
            items = items.Where(i => ids.Contains(i.EntryId));
        }
        var rows = await items.Select(i => new { i.EntryId, i.PackId }).Distinct().ToListAsync(ct);
        return [.. rows.Select(r => (new EntryId(r.EntryId), ScopeKeys.Session(r.PackId)))];
    }

    /// <summary>The entries in a pack, for projecting their marks after it changes.</summary>
    public async Task<IReadOnlyList<EntryId>> GetEntriesAsync(long packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return [.. (await db.SessionItems.Where(i => i.PackId == packId).Select(i => i.EntryId).Distinct().ToListAsync(ct)).Select(e => new EntryId(e))];
    }

    static async Task<SessionPackContents?> ContentsAsync(CatalogDbContext db, long packId, CancellationToken ct)
    {
        if (await db.SessionPacks.AsNoTracking().SingleOrDefaultAsync(p => p.Id == packId, ct) is not { } pack) return null;
        var sections = await db.SessionSections.AsNoTracking().Where(s => s.PackId == packId).OrderBy(s => s.Position).ThenBy(s => s.Id).ToListAsync(ct);
        var order = SectionOrder(sections);
        var items = (await db.SessionItems.AsNoTracking().Include(i => i.PageRef).Where(i => i.PackId == packId).ToListAsync(ct))
            .OrderBy(i => order.GetValueOrDefault(i.SectionId ?? NoSection)).ThenBy(i => i.Position).ThenBy(i => i.Id)
            .Select(ToInfo).ToList();
        var info = new SessionPackInfo(pack.Id, pack.Title, pack.Date, pack.Notes, pack.TouchedUtc, items.Count, [.. items.Select(i => i.EntryId).Distinct().Take(2)]);
        return new SessionPackContents(info, [.. sections.Select((s, i) => new SessionSectionInfo(s.Id, s.Name, i))], items);
    }

    /// <summary>Where each section's items come: the items before any section (<see cref="NoSection"/>) first.</summary>
    static Dictionary<long, int> SectionOrder(IEnumerable<SessionSection> sections)
    {
        var order = new Dictionary<long, int> { [NoSection] = -1 };
        foreach (var (section, index) in sections.OrderBy(s => s.Position).ThenBy(s => s.Id).Select((s, i) => (s, i))) order[section.Id] = index;
        return order;
    }

    sealed record Group(long? SectionId, List<SessionItem> Items);

    /// <summary>A pack's items, tracked, in their groups: those before any section, then each section's, in order.</summary>
    static async Task<List<Group>> GroupsAsync(CatalogDbContext db, long packId, CancellationToken ct)
    {
        var sections = await db.SessionSections.Where(s => s.PackId == packId).OrderBy(s => s.Position).ThenBy(s => s.Id).Select(s => s.Id).ToListAsync(ct);
        var items = await db.SessionItems.Where(i => i.PackId == packId).ToListAsync(ct);
        return [.. new long?[] { null }.Concat(sections.Select(s => (long?)s))
            .Select(s => new Group(s, [.. items.Where(i => i.SectionId == s).OrderBy(i => i.Position).ThenBy(i => i.Id)]))];
    }

    static void Move(List<Group> groups, SessionItem item, int group, int index)
    {
        foreach (var g in groups) g.Items.Remove(item);
        var items = groups[group].Items;
        items.Insert(Math.Clamp(index, 0, items.Count), item);
        item.SectionId = groups[group].SectionId;
    }

    static void Number(List<Group> groups)
    {
        foreach (var group in groups)
            for (var i = 0; i < group.Items.Count; i++) group.Items[i].Position = i;
    }

    static async Task RenumberAsync(CatalogDbContext db, long packId, CancellationToken ct)
    {
        var groups = await GroupsAsync(db, packId, ct);
        Number(groups);
        await db.SaveChangesAsync(ct);
    }

    async Task Touch(CatalogDbContext db, long packId, CancellationToken ct)
    {
        if (await db.SessionPacks.FindAsync([packId], ct) is { } pack) pack.TouchedUtc = Now;
    }

    static SessionItemInfo ToInfo(SessionItem i) => new(i.Id, i.PackId, i.SectionId, i.Position, new EntryId(i.EntryId),
        i.PageRef is { } r ? new PageRange(r.DocumentId, r.FirstPdfPage, r.LastPdfPage, r.FirstLabel, r.LastLabel, r.FirstFingerprint, r.LastFingerprint) : null,
        i.Label, i.Note, i.AddedUtc);

    static PageRef ToPageRef(PageRange range) => new()
    {
        DocumentId = range.DocumentId,
        FirstPdfPage = Math.Min(range.FirstPdfPage, range.LastPdfPage),
        LastPdfPage = Math.Max(range.FirstPdfPage, range.LastPdfPage),
        FirstLabel = range.FirstLabel,
        LastLabel = range.LastLabel,
        FirstFingerprint = range.FirstFingerprint,
        LastFingerprint = range.LastFingerprint,
    };

    static string Title(string title) =>
        string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("A name can't be blank.", nameof(title)) : title.Trim();

    static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
