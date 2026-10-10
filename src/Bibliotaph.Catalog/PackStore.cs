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
/// Image packs in catalog.db (F4 plan, choices 1, 2, 4 and 5): a folder or ZIP of many images becomes one card. Each
/// image keeps its own entry, which points at the pack's while it is in it, so splitting gives every image its card
/// back with anything set on it. The folders' decisions are remembered by path, so a rescan doesn't undo them.
/// </summary>
public sealed class PackStore(IDbContextFactory<CatalogDbContext> contexts, TimeProvider? clock = null)
{
    /// <summary>A folder or ZIP with at least this many images and no PDF becomes a pack by itself (choice 2).</summary>
    public const int AutomaticMinimum = 20;

    readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Packs what should be packed: each folder (the images directly in it, never a library folder's top level) or
    /// ZIP (subfolders included) with <see cref="AutomaticMinimum"/> images and no PDF, unless it was split, becomes a
    /// pack named after it; images new to a packed folder or ZIP join its pack. Runs after hashing, so a library
    /// indexed before packs existed is packed at its first start. Returns the entries whose cards changed: new packs,
    /// packs that gained images, and those images.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>> PlanAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
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
        var groups = located
            .Select(l => (l.SourceRootId, Folder: l.Archive ?? Parent(l.RelativePath), IsArchive: l.Archive is not null, l.DocumentId, l.Format))
            .Where(l => l.Folder.Length > 0)
            .GroupBy(l => (l.SourceRootId, Key: l.Folder.ToUpperInvariant()));
        var decisions = (await db.PackDecisions.ToListAsync(ct)).ToDictionary(d => (d.SourceRootId, d.FolderPath.ToUpperInvariant()));

        var changed = new List<EntryId>();
        foreach (var group in groups)
        {
            var images = group.Where(l => SourceFormats.IsImage(l.Format)).Select(l => l.DocumentId).Distinct().ToList();
            decisions.TryGetValue(group.Key, out var decision);
            if (decision?.Answer == PackAnswer.Split) continue;
            if (decision is null)
            {
                if (images.Count < AutomaticMinimum || group.Any(l => l.Format == SourceFormats.Pdf)) continue;
                var (rootId, folder, isArchive, _, _) = group.First();
                var now = _clock.GetUtcNow().UtcDateTime;
                var pack = db.Entries.Add(new Entry { Kind = EntryKind.Pack, CreatedUtc = now }).Entity;
                await db.SaveChangesAsync(ct);
                decision = db.PackDecisions.Add(new PackDecision
                {
                    SourceRootId = rootId,
                    FolderPath = folder,
                    IsArchive = isArchive,
                    Answer = PackAnswer.Packed,
                    EntryId = pack.Id,
                    CreatedUtc = now,
                }).Entity;
                decisions[group.Key] = decision;
                changed.Add(new EntryId(pack.Id));
            }

            var joining = await JoinAsync(db, decision.EntryId, images, ct);
            if (joining.Count == 0) continue;
            changed.AddRange(joining);
            if (!changed.Contains(new EntryId(decision.EntryId))) changed.Add(new EntryId(decision.EntryId));
        }
        await db.SaveChangesAsync(ct);
        return changed;
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
    /// "Split into separate images" (choice 4): every image gets its card back, and the folder or ZIP isn't packed
    /// again. Returns the entries whose cards changed, the pack's first, or null for an entry that isn't a pack.
    /// </summary>
    public async Task<IReadOnlyList<EntryId>?> SplitAsync(EntryId packId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.PackDecisions.SingleOrDefaultAsync(d => d.EntryId == packId.Value, ct) is not { } decision) return null;
        decision.Answer = PackAnswer.Split;
        var members = await db.Entries.Where(e => e.ParentEntryId == packId.Value).ToListAsync(ct);
        foreach (var member in members) member.ParentEntryId = null;
        await db.SaveChangesAsync(ct);
        return [packId, .. members.Select(m => new EntryId(m.Id))];
    }

    /// <summary>Undoes <see cref="SplitAsync"/>: the folder or ZIP is a pack again, with the images in it now.</summary>
    public async Task<IReadOnlyList<EntryId>> RepackAsync(EntryId packId, CancellationToken ct = default)
    {
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            if (await db.PackDecisions.SingleOrDefaultAsync(d => d.EntryId == packId.Value, ct) is not { } decision) return [];
            decision.Answer = PackAnswer.Packed;
            await db.SaveChangesAsync(ct);
        }
        return [packId, .. await PlanAsync(ct)];
    }

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
