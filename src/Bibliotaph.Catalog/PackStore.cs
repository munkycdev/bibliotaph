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
/// A smaller or mixed folder or ZIP that Needs review asks about (F4 plan, choice 3): "Make these 12 images one card?".
/// <see cref="Images"/> are its images' documents in file name order, and <see cref="BesidePdfs"/> says PDFs share it.
/// </summary>
public sealed record PackProposal(EntryId PackId, PackPlace Place, string Name, IReadOnlyList<long> Images, bool BesidePdfs);

/// <summary>What answering about a folder changed: the cards to project, and the answer it had before, which undoes it.</summary>
public sealed record PackChange(IReadOnlyList<EntryId> Changed, PackAnswer Previous);

/// <summary>
/// Image packs in catalog.db (F4 plan, choices 1 to 5): a folder or ZIP of many images becomes one card. Each image
/// keeps its own entry, which points at the pack's while it is in it, so splitting gives every image its card back
/// with anything set on it. The folders' decisions are remembered by path, so a rescan doesn't undo them.
/// </summary>
public sealed class PackStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>A folder or ZIP with at least this many images and no PDF becomes a pack by itself (choice 2).</summary>
    public const int AutomaticMinimum = 20;

    /// <summary>
    /// A folder or ZIP with at least this many images that isn't packed by itself is proposed as a pack (choice 3).
    /// Fewer never ask, so a book's two or three handouts don't produce a card.
    /// </summary>
    public const int ProposalMinimum = 5;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Packs what should be packed: each folder (the images directly in it, never a library folder's top level) or
    /// ZIP (subfolders included) with <see cref="AutomaticMinimum"/> images and no PDF, unless it was split, becomes a
    /// pack named after it; images new to a packed folder or ZIP join its pack. A folder or ZIP with
    /// <see cref="ProposalMinimum"/> images or more that isn't packed by itself is proposed instead, and is packed by
    /// itself if it grows past the minimum while it waits. Runs after hashing, so a library indexed before packs
    /// existed is packed at its first start. Returns the entries whose cards changed: new packs, packs that gained
    /// images, and those images.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> PlanAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var groups = await LocateAsync(db, ct);
        var decisions = (await db.PackDecisions.ToListAsync(ct)).ToDictionary(d => Key(d.SourceRootId, d.FolderPath));

        var changed = new List<EntryId>();
        foreach (var group in groups.Values)
        {
            var automatic = group.Images.Count >= AutomaticMinimum && !group.HasPdf;
            if (!decisions.TryGetValue(Key(group.RootId, group.Folder), out var decision))
            {
                if (!automatic && group.Images.Count < ProposalMinimum) continue;
                decision = await AddDecisionAsync(db, group, automatic ? PackAnswer.Packed : PackAnswer.Proposed, ct);
                decisions[Key(group.RootId, group.Folder)] = decision;
                if (!automatic) continue;
                changed.Add(new EntryId(decision.EntryId));
            }
            else if (decision.Answer == PackAnswer.Proposed && automatic) decision.Answer = PackAnswer.Packed;
            if (decision.Answer != PackAnswer.Packed) continue;

            var joining = await JoinAsync(db, decision.EntryId, [.. group.Images.Select(i => i.DocumentId)], ct);
            if (joining.Count == 0) continue;
            changed.AddRange(joining);
            if (!changed.Contains(new EntryId(decision.EntryId))) changed.Add(new EntryId(decision.EntryId));
        }
        await db.SaveChangesAsync(ct);
        return changed;
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
    /// "Split into separate images" (choice 4): every image gets its card back, and the folder or ZIP isn't packed
    /// again. Returns the entries whose cards changed, the pack's first, or null for an entry that isn't a pack.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>?> SplitAsync(EntryId packId, CancellationToken ct = default) =>
        (await AnswerAsync(packId, PackAnswer.Split, ct: ct))?.Changed;

    /// <summary>Undoes <see cref="SplitAsync"/>: the folder or ZIP is a pack again, with the images in it now.</summary>
    public async Task<IReadOnlyList<EntryId>> RepackAsync(EntryId packId, CancellationToken ct = default) =>
        (await AnswerAsync(packId, PackAnswer.Packed, ct: ct))?.Changed ?? [];

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
                return new FolderGroup(rootId, folder, isArchive, images, g.Any(l => l.Format == SourceFormats.Pdf));
            });
    }

    static (long, string) Key(long rootId, string folder) => (rootId, folder.ToUpperInvariant());

    /// <summary>The files a pack would hold: its images, and whether PDFs sit beside them.</summary>
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
