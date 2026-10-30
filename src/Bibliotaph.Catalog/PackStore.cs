using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

/// <summary>Where a pack's images are: a folder in a library folder, or a ZIP.</summary>
public sealed record PackPlace(string RootPath, string FolderPath, bool IsArchive, PackAnswer Answer)
{
    public string FullPath => Path.Combine(RootPath, FolderPath);
}

/// <summary>
/// A smaller or mixed folder or ZIP that Needs review asks about: "Make these 12 images one card?".
/// <see cref="Images"/> are its images' documents in file name order, and <see cref="BesidePdfs"/> says PDFs share it.
/// </summary>
public sealed record PackProposal(EntryId PackId, PackPlace Place, string Name, IReadOnlyList<long> Images, bool BesidePdfs);

/// <summary>What answering about a folder changed: the cards to project, and the answer it had before, which undoes it.</summary>
public sealed record PackChange(IReadOnlyList<EntryId> Changed, PackAnswer Previous);

/// <summary>
/// What Pack together did, so Undo can put it back: the pack the cards went into, whether it was made for them, the
/// files (images and PDFs) that joined it, and the packs merged into it. <see cref="Changed"/> lists every card that
/// changed, the pack's first, to project.
/// </summary>
public sealed record PackTogether(EntryId PackId, bool Created, IReadOnlyList<EntryId> Files, IReadOnlyList<EntryId> Merged, IReadOnlyList<EntryId> Changed);

/// <summary>A file the scan found at a new place, by its file ID or as a likely move, for the packs that follow it.</summary>
internal readonly record struct PlaceMove(long FromRootId, string FromPath, long ToRootId, string ToPath);

/// <summary>A folder or ZIP some of a pack's files are in, and how many, for the details panel.</summary>
public sealed record PackFolder(string FullPath, bool IsArchive, int Files);

/// <summary>
/// Packs in catalog.db: a folder or ZIP of many images becomes one card. Each file keeps its own entry, which points
/// at the pack's while it is in it, so splitting gives every file its card back with anything set on it. The
/// folders' decisions are remembered by path, so a rescan doesn't undo them. Only images are packed by folder; Pack
/// together makes packs by hand of images and PDFs alike (character sheets, pregens), which have no folder, and merges
/// packs: a merged pack keeps its entry, marked as merged, and its folder's new images join the pack it merged into.
/// </summary>
public sealed class PackStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>For moving what a merged pack brings, as a copy's card is moved when it joins another.</summary>
    readonly EntryStore _entries = new(contexts, clock);

    /// <summary>A folder or ZIP with at least this many images and no PDF becomes a pack by itself.</summary>
    public const int AutomaticMinimum = 20;

    /// <summary>
    /// A folder or ZIP with at least this many images that isn't packed by itself is proposed as a pack.
    /// Fewer never ask, so a book's two or three handouts don't produce a card.
    /// </summary>
    public const int ProposalMinimum = 5;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Packs what should be packed: each folder (the images directly in it, never a library folder's top level) or
    /// ZIP (subfolders included) with <see cref="AutomaticMinimum"/> images and no PDF, unless it was split, becomes a
    /// pack named after it; images new to a packed folder or ZIP join its pack. A folder or ZIP with
    /// <see cref="ProposalMinimum"/> images or more that isn't packed by itself is proposed instead, and is packed by
    /// itself if it grows past the minimum while it waits. A packed folder or ZIP that moved, whose images are all in
    /// its pack and none left where it was, takes its decision along, so the pack keeps its card and everything on it
    /// and its new images still join it; a copy of one gets no pack of its own. Runs after hashing, so a library
    /// indexed before packs existed is packed at its first start. Only images are packed this way: a PDF goes in a pack
    /// only by Pack together. Returns the entries whose cards changed: new packs, packs that gained images or moved,
    /// and those images.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> PlanAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var groups = await LocateAsync(db, ct);
        var decisions = (await db.PackDecisions.ToListAsync(ct)).ToDictionary(d => Key(d.SourceRootId, d.FolderPath));
        var merged = await MergedAsync(db, ct);
        var packed = await PackedAlreadyAsync(db, [.. groups.Values.Where(g => !decisions.ContainsKey(Key(g.RootId, g.Folder)))], merged, ct);

        var changed = new List<EntryId>();
        foreach (var group in groups.Values)
        {
            var automatic = group.Images.Count >= AutomaticMinimum && !group.HasPdf;
            if (!decisions.TryGetValue(Key(group.RootId, group.Folder), out var decision) && packed.TryGetValue(Key(group.RootId, group.Folder), out var already))
            {
                // Its images are one pack's card already: the pack's folder or ZIP moved here, so its decision follows,
                // or this is a copy of it, which needs no card of its own.
                if (Moved(group, already) is not { } followed) continue;
                decisions.Remove(Key(followed.SourceRootId, followed.FolderPath));
                followed.SourceRootId = group.RootId;
                followed.FolderPath = group.Folder;
                decisions[Key(group.RootId, group.Folder)] = decision = followed;
                changed.Add(new EntryId(followed.EntryId));
                if (already != followed.EntryId) changed.Add(new EntryId(already));
            }
            if (decision is null)
            {
                if (!automatic && group.Images.Count < ProposalMinimum) continue;
                decision = await AddDecisionAsync(db, group, automatic ? PackAnswer.Packed : PackAnswer.Proposed, ct);
                decisions[Key(group.RootId, group.Folder)] = decision;
                if (!automatic) continue;
                changed.Add(new EntryId(decision.EntryId));
            }
            else if (decision.Answer == PackAnswer.Proposed && automatic) decision.Answer = PackAnswer.Packed;
            if (decision.Answer != PackAnswer.Packed) continue;

            // A folder whose pack merged into another fills that one.
            var packId = Resolve(merged, decision.EntryId);
            var joining = await JoinAsync(db, packId, [.. group.Images.Select(i => i.DocumentId)], ct);
            if (joining.Count == 0) continue;
            changed.AddRange(joining);
            if (!changed.Contains(new EntryId(packId))) changed.Add(new EntryId(packId));
        }
        await db.SaveChangesAsync(ct);
        return changed;

        // The packed decision behind this pack whose folder or ZIP no longer holds any image: the place it moved from.
        // When there are several, as for a pack others merged into, the one with the same name; none when that doesn't settle it.
        PackDecision? Moved(FolderGroup group, long packId)
        {
            var left = decisions.Values.Where(d => d.Answer == PackAnswer.Packed && d.IsArchive == group.IsArchive && Resolve(merged, d.EntryId) == packId
                && !(groups.TryGetValue(Key(d.SourceRootId, d.FolderPath), out var there) && there.Images.Count > 0)).ToList();
            if (left.Count > 1)
                left = [.. left.Where(d => string.Equals(FileName(d.FolderPath), FileName(group.Folder), StringComparison.OrdinalIgnoreCase))];
            return left.Count == 1 ? left[0] : null;
        }
    }

    /// <summary>
    /// The folders and ZIPs among <paramref name="groups"/> whose images are all in one pack already, and that pack
    /// (the one it merged into, if it did). Such images moved there from the pack's own folder or ZIP, or are copies.
    /// </summary>
    static async Task<Dictionary<(long, string), long>> PackedAlreadyAsync(CatalogDbContext db, List<FolderGroup> groups, Dictionary<long, long> merged,
        CancellationToken ct)
    {
        var documentIds = groups.SelectMany(g => g.Images.Select(i => i.DocumentId)).Distinct().ToList();
        if (documentIds.Count == 0) return [];
        var parents = (await db.EntrySources.AsNoTracking()
                .Where(s => s.FirstPdfPage == null && documentIds.Contains(s.DocumentId) && s.Entry.Kind == EntryKind.Whole && s.Entry.MergedIntoEntryId == null)
                .Select(s => new { s.DocumentId, s.Entry.ParentEntryId })
                .ToListAsync(ct))
            .ToLookup(s => s.DocumentId, s => s.ParentEntryId is { } parent ? Resolve(merged, parent) : (long?)null);
        var packed = new Dictionary<(long, string), long>();
        foreach (var group in groups.Where(g => g.Images.Count > 0))
        {
            var packs = group.Images.SelectMany(i => parents[i.DocumentId].DefaultIfEmpty()).Distinct().Take(2).ToList();
            if (packs is [{ } pack]) packed[Key(group.RootId, group.Folder)] = pack;
        }
        return packed;
    }

    /// <summary>
    /// Split and proposed decisions follow their folder or ZIP when the scan finds it moved, so a moved folder that was
    /// split isn't packed again, nor one already asked about asked again. A ZIP's decision follows the ZIP; a folder's
    /// follows its images when they all went to one other folder and none is left behind, and no decision is there
    /// already. A packed decision is followed in <see cref="PlanAsync"/> by its pack's images, which also catches a
    /// move found only by hashing, and refreshes the card.
    /// </summary>
    internal static async Task FollowMovesAsync(CatalogDbContext db, IReadOnlyList<PlaceMove> moves, CancellationToken ct)
    {
        if (moves.Count == 0 || !await db.PackDecisions.AnyAsync(d => d.Answer != PackAnswer.Packed, ct)) return;
        // A ZIP moves as one file; a folder by the images that were directly in it.
        var places = moves.Where(m => SourceFormats.IsArchive(m.FromPath) && SourceFormats.IsArchive(m.ToPath)).Select(m => (Move: m, IsArchive: true)).ToList();
        foreach (var folder in moves.Where(m => IsImage(m.FromPath) && IsImage(m.ToPath)).GroupBy(m => Key(m.FromRootId, Parent(m.FromPath))))
        {
            var to = folder.Select(m => (m.ToRootId, Folder: Parent(m.ToPath))).DistinctBy(t => Key(t.ToRootId, t.Folder)).ToList();
            if (to is not [var only] || only.Folder.Length == 0) continue;
            var first = folder.First();
            places.Add((new PlaceMove(first.FromRootId, Parent(first.FromPath), only.ToRootId, only.Folder), false));
        }
        if (places.Count == 0) return;

        var decisions = (await db.PackDecisions.ToListAsync(ct)).ToDictionary(d => Key(d.SourceRootId, d.FolderPath));
        foreach (var (move, isArchive) in places)
        {
            if (!decisions.TryGetValue(Key(move.FromRootId, move.FromPath), out var decision) || decision.Answer == PackAnswer.Packed
                || decision.IsArchive != isArchive || decisions.ContainsKey(Key(move.ToRootId, move.ToPath))
                || await HoldsImagesAsync(db, move.FromRootId, move.FromPath, isArchive, ct)) continue;
            decisions.Remove(Key(move.FromRootId, move.FromPath));
            decision.SourceRootId = move.ToRootId;
            decision.FolderPath = move.ToPath;
            decisions[Key(move.ToRootId, move.ToPath)] = decision;
        }
    }

    static bool IsImage(string path) => SourceFormats.FromFileName(path) is { } format && SourceFormats.IsImage(format);

    /// <summary>Whether a folder (the images directly in it) or ZIP still holds an image that isn't missing.</summary>
    static async Task<bool> HoldsImagesAsync(CatalogDbContext db, long rootId, string folder, bool isArchive, CancellationToken ct)
    {
        var present = db.FileLocations.Where(f => f.SourceRootId == rootId && f.State != FileLocationState.Missing);
        present = isArchive
            ? present.Where(f => f.Container != null && f.Container.RelativePath == folder)
            : present.Where(f => f.ContainerId == null);
        var paths = await present.Select(f => f.RelativePath).ToListAsync(ct);
        return paths.Any(p => IsImage(p) && (isArchive || string.Equals(Parent(p), folder, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>A new pack entry for a folder or ZIP, and the decision that remembers it.</summary>
    async Task<PackDecision> AddDecisionAsync(CatalogDbContext db, FolderGroup group, PackAnswer answer, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var pack = db.Entries.Add(new Entry { Kind = EntryKind.Pack, CreatedUtc = now }).Entity;
        await db.SaveChangesAsync(ct);
        return db.PackDecisions.Add(new PackDecision
        {
            SourceRootId = group.RootId,
            FolderPath = group.Folder,
            IsArchive = group.IsArchive,
            Answer = answer,
            EntryId = pack.Id,
            CreatedUtc = now,
        }).Entity;
    }

    /// <summary>The images' own entries that are on no other card yet, now in the pack.</summary>
    static async Task<List<EntryId>> JoinAsync(CatalogDbContext db, long packId, List<long> documentIds, CancellationToken ct)
    {
        var entries = await db.Entries
            .Where(e => e.Kind == EntryKind.Whole && e.ParentEntryId == null && e.MergedIntoEntryId == null
                && e.Sources.Any(s => s.FirstPdfPage == null && documentIds.Contains(s.DocumentId)))
            .ToListAsync(ct);
        foreach (var entry in entries) entry.ParentEntryId = packId;
        return [.. entries.Select(e => new EntryId(e.Id))];
    }

    /// <summary>
    /// Answers about a pack's folder or ZIP: Packed makes its images one card again, Split gives each its card back
    /// ("Split into separate images", "Keep separate"), Proposed asks again (undoing an answer to a proposal). Null
    /// for an entry that isn't a pack, or whose answer isn't <paramref name="from"/> when that is given.
    /// </summary>
    public async Task<PackChange?> AnswerAsync(EntryId packId, PackAnswer answer, PackAnswer? from = null, CancellationToken ct = default)
    {
        PackAnswer previous;
        List<EntryId> changed = [packId];
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (await db.PackDecisions.SingleOrDefaultAsync(d => d.EntryId == packId.Value, ct) is not { } decision) return null;
            if (from is not null && decision.Answer != from) return null;
            previous = decision.Answer;
            decision.Answer = answer;
            if (answer != PackAnswer.Packed)
                foreach (var member in await db.Entries.Where(e => e.ParentEntryId == packId.Value).ToListAsync(ct))
                {
                    member.ParentEntryId = null;
                    changed.Add(new EntryId(member.Id));
                }
            await db.SaveChangesAsync(ct);
        }
        if (answer == PackAnswer.Packed) changed.AddRange((await PlanAsync(ct)).Where(e => e != packId));
        return new PackChange(changed, previous);
    }

    /// <summary>
    /// "Split into separate images" (or files): every file gets its card back, and no folder or ZIP the pack was made
    /// from is packed again, its own or those of packs merged into it; a pack made by hand has none. Returns the
    /// entries whose cards changed, the pack's first and then its files, or null for an entry that isn't a pack.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>?> SplitAsync(EntryId packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (!await db.Entries.AnyAsync(e => e.Id == packId.Value && e.Kind == EntryKind.Pack && e.MergedIntoEntryId == null, ct)) return null;
        foreach (var decision in await DecisionsOfAsync(db, packId.Value, ct)) decision.Answer = PackAnswer.Split;
        List<EntryId> changed = [packId];
        foreach (var member in await db.Entries.Where(e => e.ParentEntryId == packId.Value).OrderBy(e => e.Id).ToListAsync(ct))
        {
            member.ParentEntryId = null;
            changed.Add(new EntryId(member.Id));
        }
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    /// "Use as the pack's cover": the card shows <paramref name="fileId"/>'s cover in place of the mosaic, or the mosaic
    /// again for null. False when the entry isn't a pack or the file isn't in it.
    /// </summary>
    public async Task<bool> SetCoverAsync(EntryId packId, EntryId? fileId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var pack = await db.Entries.SingleOrDefaultAsync(e => e.Id == packId.Value && e.Kind == EntryKind.Pack && e.MergedIntoEntryId == null, ct);
        if (pack is null) return false;
        if (fileId is { } file && !await db.Entries.AnyAsync(e => e.Id == file.Value && e.ParentEntryId == packId.Value, ct)) return false;
        pack.CoverEntryId = fileId?.Value;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Undoes <see cref="SplitAsync"/>: its folders and ZIPs are packed again, with the images in them now, and
    /// <paramref name="files"/> (what the split freed) are back in the pack unless they have joined another card since.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> RepackAsync(EntryId packId, IReadOnlyCollection<EntryId> files, CancellationToken ct = default)
    {
        List<EntryId> changed = [packId];
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            foreach (var decision in await DecisionsOfAsync(db, packId.Value, ct)) decision.Answer = PackAnswer.Packed;
            var ids = files.Select(i => i.Value).ToList();
            foreach (var file in await db.Entries.Where(e => ids.Contains(e.Id) && e.ParentEntryId == null && e.MergedIntoEntryId == null).ToListAsync(ct))
            {
                file.ParentEntryId = packId.Value;
                changed.Add(new EntryId(file.Id));
            }
            await db.SaveChangesAsync(ct);
        }
        changed.AddRange((await PlanAsync(ct)).Where(e => !changed.Contains(e)));
        return changed;
    }

    /// <summary>
    /// Pack together: two or more cards for single files (images or PDFs) or packs become one pack. Files only make a
    /// new pack, with no folder, that doesn't grow by itself; with a pack among them, the files join it; with several,
    /// the newer packs merge into the oldest with their files, and what each brings (favourites, collections, binder
    /// items, notes, values) moves as a copy's card's does. Null when the cards can't go together: fewer than two, a
    /// book owned elsewhere, an adventure or a file with adventures marked in it, a card in a pack or merged already.
    /// </summary>
    public async Task<PackTogether?> PackTogetherAsync(IReadOnlyCollection<EntryId> entryIds, CancellationToken ct = default)
    {
        var ids = entryIds.Select(e => e.Value).Distinct().ToList();
        if (ids.Count < 2) return null;
        await using var db = await contexts.CreateDbContextAsync(ct);
        var cards = await db.Entries.Where(e => ids.Contains(e.Id) && e.ParentEntryId == null && e.MergedIntoEntryId == null).ToListAsync(ct);
        if (cards.Count != ids.Count) return null;
        var packs = cards.Where(c => c.Kind == EntryKind.Pack).OrderBy(c => c.Id).ToList();
        var files = cards.Where(c => c.Kind != EntryKind.Pack).OrderBy(c => c.Id).ToList();
        var fileIds = files.Select(i => i.Id).ToList();
        var sources = await db.EntrySources.AsNoTracking().Where(s => fileIds.Contains(s.EntryId))
            .Select(s => new { s.EntryId, s.FirstPdfPage, s.Document.Format }).ToListAsync(ct);
        if (files.Any(i => i.Kind != EntryKind.Whole || sources.All(s => s.EntryId != i.Id))
            || sources.Any(s => s.FirstPdfPage is not null || !SourceFormats.IsImage(s.Format) && s.Format != SourceFormats.Pdf)) return null;
        // A file's adventures show as cards of their own that name it, so a file that has them keeps its own card.
        if (await db.Entries.AnyAsync(e => e.Kind == EntryKind.Part && e.MergedIntoEntryId == null && e.ParentEntryId != null
            && fileIds.Contains(e.ParentEntryId.Value), ct)) return null;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var created = packs.Count == 0;
        var pack = packs.FirstOrDefault() ?? db.Entries.Add(new Entry { Kind = EntryKind.Pack, CreatedUtc = _clock.GetUtcNow().UtcDateTime }).Entity;
        if (created) await db.SaveChangesAsync(ct);
        foreach (var file in files) file.ParentEntryId = pack.Id;
        List<EntryId> changed = [new(pack.Id), .. files.Select(i => new EntryId(i.Id))];
        foreach (var other in packs.Skip(1))
        {
            // Its files move with everything else it brings.
            var moved = await _entries.MoveAsync(db, other.Id, pack.Id, ct);
            _entries.AddJoin(db, pack.Id, other.Id, null, null, moved);
            // Saved each time, so the next pack's values are compared with this one's too.
            await db.SaveChangesAsync(ct);
            changed.Add(new EntryId(other.Id));
            changed.AddRange(moved.Members.Select(id => new EntryId(id)));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new PackTogether(new EntryId(pack.Id), created, [.. files.Select(i => new EntryId(i.Id))], [.. packs.Skip(1).Select(p => new EntryId(p.Id))], changed);
    }

    /// <summary>
    /// Undoes <see cref="PackTogetherAsync"/>: each merged pack is back with its files and what it brought, and the
    /// files that joined have their own cards again. A pack made for them is left with none, so it shows nowhere.
    /// Returns the cards to project.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> UndoPackTogetherAsync(PackTogether together, CancellationToken ct = default)
    {
        var packId = together.PackId.Value;
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var merged in together.Merged.Reverse())
        {
            var record = await db.EntryJoins.Where(j => j.EntryId == packId && j.JoinedEntryId == merged.Value)
                .OrderByDescending(j => j.Id).FirstOrDefaultAsync(ct);
            if (record is null) continue;
            await EntryStore.RestoreAsync(db, packId, record, [], ct);
            await db.SaveChangesAsync(ct);
        }
        var files = together.Files.Select(i => i.Value).ToList();
        foreach (var file in await db.Entries.Where(e => files.Contains(e.Id) && e.ParentEntryId == packId).ToListAsync(ct))
            file.ParentEntryId = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return together.Changed;
    }

    /// <summary>
    /// Makes this catalog's packs agree with another device's, from what each file there says (<paramref name="wishes"/>,
    /// by the file's own entry): the sync id of the pack it is in, or null for a file on a card of its own. A pack each of
    /// whose files is told to stand alone is split, as Split would; a pack whose files are told they belong to a pack of
    /// another id takes that id, so two devices that packed the same folder agree, or merges into the pack here that has
    /// it; files that belong to a pack are put in it, made if it isn't here yet. A folder or ZIP waiting in Needs review
    /// is answered as the other device answered it. Returns the cards that changed and the files that couldn't be put as
    /// they were told (an adventure's file, say), which wait.
    /// </summary>
    public async Task<(IReadOnlyList<EntryId> Changed, IReadOnlySet<long> Unsettled)> ApplySyncedAsync(IReadOnlyDictionary<long, Guid?> wishes,
        CancellationToken ct = default)
    {
        var changed = new HashSet<EntryId>();
        if (wishes.Count == 0) return ([], new HashSet<long>());
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var ids = wishes.Keys.ToList();
        var files = (await db.Entries.Where(e => ids.Contains(e.Id) && e.Kind == EntryKind.Whole && e.MergedIntoEntryId == null).ToListAsync(ct))
            .ToDictionary(e => e.Id);
        var packs = await db.Entries.Where(e => e.Kind == EntryKind.Pack && e.MergedIntoEntryId == null).ToListAsync(ct);
        // A whole file with a parent is in a pack: an adventure's parent is its file, and it is a part.
        var members = await MembersAsync(db, ct);
        Guid? Wish(Entry file, Guid own) => wishes.TryGetValue(file.Id, out var wish) ? wish : own;

        // A folder or ZIP waiting in Needs review whose images the other device packed, or kept separate, is answered so.
        var proposed = await db.PackDecisions.Where(d => d.Answer == PackAnswer.Proposed).ToListAsync(ct);
        if (proposed.Count > 0)
        {
            var groups = await LocateAsync(db, ct);
            var byDocument = (await db.EntrySources.AsNoTracking().Where(s => s.FirstPdfPage == null && s.Entry.Kind == EntryKind.Whole && s.Entry.MergedIntoEntryId == null)
                .Select(s => new { s.DocumentId, s.EntryId }).ToListAsync(ct)).ToLookup(s => s.DocumentId, s => s.EntryId);
            foreach (var decision in proposed)
            {
                if (!groups.TryGetValue(Key(decision.SourceRootId, decision.FolderPath), out var group)) continue;
                var told = group.Images.SelectMany(i => byDocument[i.DocumentId]).Distinct().Where(wishes.ContainsKey).Select(e => wishes[e]).Distinct().ToList();
                if (told is not [var answer]) continue;
                decision.Answer = answer is null ? PackAnswer.Split : PackAnswer.Packed;
                if (answer is not { } id || packs.FirstOrDefault(p => p.Id == decision.EntryId) is not { } pack) continue;
                changed.UnionWith(await JoinAsync(db, pack.Id, [.. group.Images.Select(i => i.DocumentId)], ct));
                changed.Add(new EntryId(pack.Id));
                await db.SaveChangesAsync(ct);
                members = await MembersAsync(db, ct);
                await TakeIdAsync(db, pack, id, ct);
            }
        }

        // Each pack here: split when every file is told to stand alone; otherwise it goes by what most of its files are
        // told, and the files told otherwise leave it.
        var merges = new List<(Entry From, Entry Into)>();
        foreach (var pack in packs.Where(p => members[p.Id].Any()).ToList())
        {
            var inPack = members[pack.Id].ToList();
            var votes = inPack.Select(m => (Member: m, Wish: Wish(m, pack.SyncId))).ToList();
            if (votes.All(v => v.Wish is null))
            {
                foreach (var decision in await DecisionsOfAsync(db, pack.Id, ct)) decision.Answer = PackAnswer.Split;
                foreach (var member in inPack) member.ParentEntryId = null;
                changed.Add(new EntryId(pack.Id));
                changed.UnionWith(inPack.Select(m => new EntryId(m.Id)));
                continue;
            }
            var chosen = votes.Where(v => v.Wish is not null).GroupBy(v => v.Wish!.Value)
                .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key == pack.SyncId).ThenBy(g => g.Key).First().Key;
            foreach (var (member, _) in votes.Where(v => v.Wish != chosen))
            {
                member.ParentEntryId = null;
                changed.Add(new EntryId(member.Id));
            }
            if (chosen == pack.SyncId) continue;
            changed.Add(new EntryId(pack.Id));
            if (packs.FirstOrDefault(p => p.SyncId == chosen && p.Id != pack.Id && members[p.Id].Any()) is { } other) merges.Add((pack, other));
            else await TakeIdAsync(db, pack, chosen, ct);
        }
        await db.SaveChangesAsync(ct);

        // Two packs here that are one pack there: the one that took the other's id merges into it, bringing what it has.
        foreach (var (from, into) in merges)
        {
            var moved = await _entries.MoveAsync(db, from.Id, into.Id, ct);
            _entries.AddJoin(db, into.Id, from.Id, null, null, moved);
            await db.SaveChangesAsync(ct);
            changed.UnionWith(moved.Members.Select(id => new EntryId(id)));
            changed.Add(new EntryId(into.Id));
        }

        // Files that belong to a pack go into it, made here when this catalog hasn't got it.
        foreach (var group in wishes.Where(w => w.Value is not null && files.ContainsKey(w.Key)).GroupBy(w => w.Value!.Value))
        {
            var loose = group.Select(w => files[w.Key]).Where(f => f.ParentEntryId is null).ToList();
            if (loose.Count == 0) continue;
            // A file whose adventures are marked keeps its own card, as Pack together keeps it.
            var looseIds = loose.Select(f => f.Id).ToList();
            var withParts = await db.Entries.Where(e => e.Kind == EntryKind.Part && e.MergedIntoEntryId == null && e.ParentEntryId != null
                && looseIds.Contains(e.ParentEntryId.Value)).Select(e => e.ParentEntryId!.Value).ToListAsync(ct);
            loose = [.. loose.Where(f => !withParts.Contains(f.Id))];
            if (loose.Count == 0) continue;
            var pack = packs.FirstOrDefault(p => p.SyncId == group.Key && p.MergedIntoEntryId == null);
            if (pack is null)
            {
                pack = db.Entries.Add(new Entry { Kind = EntryKind.Pack, CreatedUtc = now }).Entity;
                await db.SaveChangesAsync(ct);
                await TakeIdAsync(db, pack, group.Key, ct);
                packs.Add(pack);
            }
            foreach (var file in loose) file.ParentEntryId = pack.Id;
            changed.Add(new EntryId(pack.Id));
            changed.UnionWith(loose.Select(f => new EntryId(f.Id)));
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        var packIds = await db.Entries.AsNoTracking().Where(e => e.Kind == EntryKind.Pack && e.MergedIntoEntryId == null).ToDictionaryAsync(e => e.Id, e => e.SyncId, ct);
        var unsettled = new HashSet<long>();
        foreach (var (fileId, wish) in wishes)
        {
            if (!files.TryGetValue(fileId, out var file)) continue;
            Guid? inPack = file.ParentEntryId is { } parent && packIds.TryGetValue(parent, out var id) ? id : null;
            if (inPack != wish) unsettled.Add(fileId);
        }
        return ([.. changed], unsettled);
    }

    static async Task<ILookup<long, Entry>> MembersAsync(CatalogDbContext db, CancellationToken ct) =>
        (await db.Entries.Where(e => e.Kind == EntryKind.Whole && e.MergedIntoEntryId == null && e.ParentEntryId != null).ToListAsync(ct))
            .ToLookup(e => e.ParentEntryId!.Value);

    /// <summary>Gives a pack the sync id another device knows it by; an entry here that had that id gets a new one.</summary>
    static async Task TakeIdAsync(CatalogDbContext db, Entry pack, Guid id, CancellationToken ct)
    {
        if (pack.SyncId == id) return;
        if (await db.Entries.SingleOrDefaultAsync(e => e.SyncId == id && e.Id != pack.Id, ct) is { } holder)
        {
            holder.SyncId = Guid.NewGuid();
            await db.SaveChangesAsync(ct);
        }
        pack.SyncId = id;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The images on cards of their own in folders and ZIPs the user kept separate or split, by their entries: what
    /// another device is told to keep apart too.
    /// </summary>
    internal static async Task<IReadOnlySet<long>> KeptSeparateAsync(CatalogDbContext db, CancellationToken ct)
    {
        var split = await db.PackDecisions.AsNoTracking().Where(d => d.Answer == PackAnswer.Split).Select(d => new { d.SourceRootId, d.FolderPath }).ToListAsync(ct);
        if (split.Count == 0) return new HashSet<long>();
        var folders = split.Select(d => Key(d.SourceRootId, d.FolderPath)).ToHashSet();
        var groups = await LocateAsync(db, ct);
        var documents = groups.Where(g => folders.Contains(g.Key)).SelectMany(g => g.Value.Images.Select(i => i.DocumentId)).ToHashSet();
        if (documents.Count == 0) return new HashSet<long>();
        var entries = await db.EntrySources.AsNoTracking()
            .Where(s => s.FirstPdfPage == null && s.Entry.Kind == EntryKind.Whole && s.Entry.ParentEntryId == null && s.Entry.MergedIntoEntryId == null)
            .Select(s => new { s.DocumentId, s.EntryId }).ToListAsync(ct);
        return entries.Where(e => documents.Contains(e.DocumentId)).Select(e => e.EntryId).ToHashSet();
    }

    /// <summary>The folders and ZIPs a pack's files are in, with how many of them each holds, most first.</summary>
    public async Task<IReadOnlyList<PackFolder>> GetFoldersAsync(EntryId packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var located = await db.FileLocations.AsNoTracking()
            .Where(l => l.DocumentId != null && l.State != FileLocationState.Missing
                && db.EntrySources.Any(s => s.DocumentId == l.DocumentId && s.Entry.ParentEntryId == packId.Value))
            .Select(l => new { RootPath = l.SourceRoot.Path, l.RelativePath, Archive = l.Container == null ? null : l.Container.RelativePath, l.DocumentId })
            .ToListAsync(ct);
        return [.. located
            .Select(l => (Path: Path.Combine(l.RootPath, l.Archive ?? Parent(l.RelativePath)), IsArchive: l.Archive is not null, l.DocumentId))
            .GroupBy(l => (l.Path.ToUpperInvariant(), l.IsArchive))
            .Select(g => new PackFolder(g.First().Path, g.Key.IsArchive, g.Select(l => l.DocumentId).Distinct().Count()))
            .OrderByDescending(f => f.Files).ThenBy(f => f.FullPath, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Each pack merged into another, and the pack it merged into.</summary>
    static async Task<Dictionary<long, long>> MergedAsync(CatalogDbContext db, CancellationToken ct) =>
        await db.Entries.AsNoTracking().Where(e => e.Kind == EntryKind.Pack && e.MergedIntoEntryId != null)
            .ToDictionaryAsync(e => e.Id, e => e.MergedIntoEntryId!.Value, ct);

    /// <summary>The pack a pack's images are in now: itself, or the pack it merged into, however many merges on.</summary>
    static long Resolve(Dictionary<long, long> merged, long packId)
    {
        for (var hops = 0; merged.TryGetValue(packId, out var into) && hops < merged.Count; hops++) packId = into;
        return packId;
    }

    /// <summary>The folder and ZIP decisions behind a pack: its own, and those of packs merged into it.</summary>
    static async Task<List<PackDecision>> DecisionsOfAsync(CatalogDbContext db, long packId, CancellationToken ct)
    {
        var merged = await MergedAsync(db, ct);
        return [.. (await db.PackDecisions.ToListAsync(ct)).Where(d => Resolve(merged, d.EntryId) == packId)];
    }

    /// <summary>
    /// The proposals waiting in Needs review, by path: folders and ZIPs proposed that still have
    /// <see cref="ProposalMinimum"/> images or more.
    /// </summary>
    public async Task<IReadOnlyList<PackProposal>> GetProposalsAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var proposed = await db.PackDecisions.AsNoTracking().Where(d => d.Answer == PackAnswer.Proposed)
            .Join(db.SourceRoots, d => d.SourceRootId, r => r.Id, (d, r) => new { Decision = d, RootPath = r.Path })
            .ToListAsync(ct);
        if (proposed.Count == 0) return [];
        var groups = await LocateAsync(db, ct);
        return [.. proposed
            .Select(p => (p.Decision, p.RootPath, Group: groups.GetValueOrDefault(Key(p.Decision.SourceRootId, p.Decision.FolderPath))))
            .Where(p => p.Group is { Images.Count: >= ProposalMinimum })
            .Select(p => new PackProposal(new EntryId(p.Decision.EntryId), new PackPlace(p.RootPath, p.Decision.FolderPath, p.Decision.IsArchive, p.Decision.Answer),
                Name(p.Decision.FolderPath, p.Decision.IsArchive), [.. p.Group!.Images.Select(i => i.DocumentId)], p.Group.HasPdf))
            .OrderBy(p => p.Place.FullPath, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Where each pack's images are, for hints from its folder names; every pack's, or only <paramref name="packIds"/>'.</summary>
    public async Task<IReadOnlyDictionary<EntryId, PackPlace>> GetPlacesAsync(IReadOnlyCollection<EntryId>? packIds = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var decisions = db.PackDecisions.AsNoTracking();
        if (packIds is not null)
        {
            var ids = packIds.Select(p => p.Value).ToList();
            decisions = decisions.Where(d => ids.Contains(d.EntryId));
        }
        return await decisions
            .Join(db.SourceRoots, d => d.SourceRootId, r => r.Id, (d, r) => new { d.EntryId, Place = new PackPlace(r.Path, d.FolderPath, d.IsArchive, d.Answer) })
            .ToDictionaryAsync(d => new EntryId(d.EntryId), d => d.Place, ct);
    }

    /// <summary>
    /// The library's files grouped by the folder or ZIP a pack would hold: a ZIP member by its ZIP, a loose file by
    /// its folder, leaving out each library folder's top level. Images are in file name order.
    /// </summary>
    static async Task<Dictionary<(long, string), FolderGroup>> LocateAsync(CatalogDbContext db, CancellationToken ct)
    {
        var located = await db.FileLocations.AsNoTracking()
            .Where(l => l.DocumentId != null && l.State != FileLocationState.Missing && l.SourceRoot.Availability != SourceRootAvailability.RemovedByUser)
            .Select(l => new
            {
                l.SourceRootId,
                l.RelativePath,
                Archive = l.Container == null ? null : l.Container.RelativePath,
                DocumentId = l.DocumentId!.Value,
                l.Document!.Format,
            })
            .ToListAsync(ct);
        return located
            .Select(l => (l.SourceRootId, Folder: l.Archive ?? Parent(l.RelativePath), IsArchive: l.Archive is not null, l.DocumentId, l.Format, Name: FileName(l.RelativePath)))
            .Where(l => l.Folder.Length > 0)
            .GroupBy(l => Key(l.SourceRootId, l.Folder))
            .ToDictionary(g => g.Key, g =>
            {
                var (rootId, folder, isArchive, _, _, _) = g.First();
                var images = g.Where(l => SourceFormats.IsImage(l.Format)).DistinctBy(l => l.DocumentId)
                    .OrderBy(l => l.Name, NaturalOrder.Instance).Select(l => (l.DocumentId, l.Name)).ToList();
                return new FolderGroup(rootId, folder, isArchive, images, g.Any(l => SourceFormats.HasPages(l.Format)));
            });
    }

    static (long, string) Key(long rootId, string folder) => (rootId, folder.ToUpperInvariant());

    /// <summary>The files a pack would hold: its images, and whether books (PDFs or e-books) sit beside them.</summary>
    sealed record FolderGroup(long RootId, string Folder, bool IsArchive, List<(long DocumentId, string Name)> Images, bool HasPdf);

    /// <summary>Where a pack's images are, for the inspector; null for an entry that isn't a pack.</summary>
    public async Task<PackPlace?> GetPlaceAsync(EntryId packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await db.PackDecisions.AsNoTracking().Where(d => d.EntryId == packId.Value)
            .Join(db.SourceRoots, d => d.SourceRootId, r => r.Id, (d, r) => new PackPlace(r.Path, d.FolderPath, d.IsArchive, d.Answer))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>A pack's name: its folder's, or its ZIP's without ".zip".</summary>
    public static string Name(string folderPath, bool isArchive)
    {
        var name = FileName(folderPath);
        return isArchive ? DisplayTitle.FromFileName(name) : DisplayTitle.FromFolderName(name);
    }

    /// <summary>The last part of a path in a root, whichever separator it uses.</summary>
    public static string FileName(string relativePath) => relativePath[(relativePath.LastIndexOfAny(Separators) + 1)..];

    /// <summary>The folder a file is in, within its root; empty for one at the root's top level.</summary>
    static string Parent(string relativePath)
    {
        var last = relativePath.LastIndexOfAny(Separators);
        return last < 0 ? "" : relativePath[..last];
    }

    static readonly char[] Separators = ['\\', '/'];
}
