using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Image packs (F4a): which folders and ZIPs become one card, images joining later, and splitting.</summary>
public sealed class PackStoreTests : IAsyncLifetime
{
    static readonly DateTime Monday = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-packs-").FullName;
    readonly List<ScannedFile> _files = [];
    CatalogDatabase _database = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    PackStore _packs = null!;
    long _root;
    int _hashes;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        var contexts = new Factory(_database);
        _library = new LibraryStore(contexts);
        _entries = new EntryStore(contexts);
        _packs = new PackStore(contexts);
        _root = (await new SourceRootStore(contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    ContentHash NextHash() => ContentHash.Parse((++_hashes).ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0'));

    /// <summary>Scans files into the library and hashes each one as new content.</summary>
    async Task<Dictionary<string, EntryId>> AddAsync(params string[] paths)
    {
        _files.AddRange(paths.Select(p => new ScannedFile(p, 1000, Monday, OnlineOnly: false)));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var entries = new Dictionary<string, EntryId>();
        foreach (var file in await _library.NextUnhashedAsync(1000, includeOnlineOnly: true, Ct))
        {
            var document = (await _library.AttachHashAsync(file, NextHash(), Ct))!.Value.DocumentId;
            entries[Path.GetRelativePath(Path.Combine(_dir, "Library"), file.FullPath).Replace('\\', '/')] = (await _entries.GetEntryAsync(document, Ct))!.EntryId;
        }
        return entries;
    }

    static string[] Images(string folder, int count, int from = 1) =>
        [.. Enumerable.Range(from, count).Select(i => $"{folder}/Zombie {i}.png")];

    [Fact]
    public async Task Twenty_images_in_a_folder_become_one_card_named_after_it_and_later_images_join_it()
    {
        var images = await AddAsync([.. Images("Tokens/Undead", 20), "Tokens/Undead/Ghast.jpg", "cover.png"]);

        var changed = await _packs.PlanAsync(Ct);

        var cards = await _entries.GetCurrentAsync(ct: Ct);
        var pack = Assert.Single(cards, c => c.Kind == EntryKind.Pack);
        Assert.Equal("Undead", pack.Name);
        // In file name order, numbers as numbers.
        Assert.Equal(["Ghast.jpg", "Zombie 1.png", "Zombie 2.png", "Zombie 3.png"], pack.Members!.Take(4).Select(m => m.Name));
        Assert.Equal("Zombie 20.png", pack.Members![^1].Name);
        Assert.Equal(pack.Members[0].DocumentId, pack.DocumentId);
        // The images show only inside the pack; a file at the library folder's top level stays on its own.
        Assert.Equal([pack.EntryId, images["cover.png"]], cards.Select(c => c.EntryId).OrderBy(e => e == pack.EntryId ? 0 : 1));
        Assert.Equal(22, changed.Count);
        Assert.Contains(pack.EntryId, changed);
        Assert.Equal([pack], await _entries.GetShownByAsync(pack.Members[5].DocumentId, Ct), new PackComparer());
        // The library's folder scope finds the pack through its images.
        Assert.Contains(pack.EntryId, await _library.GetVisibleEntryIdsAsync(_root, Ct));

        // A new image joins; nothing else changes.
        var added = await AddAsync("Tokens/Undead/Zombie 21.png");
        Assert.Equal([added.Single().Value, pack.EntryId], await _packs.PlanAsync(Ct));
        Assert.Equal(22, Assert.Single(await _entries.GetCurrentAsync([pack.EntryId], Ct)).Members!.Count);
        Assert.Empty(await _packs.PlanAsync(Ct));

        var place = (await _packs.GetPlaceAsync(pack.EntryId, Ct))!;
        Assert.Equal((Path.Combine(_dir, "Library", "Tokens/Undead"), false, PackAnswer.Packed), (place.FullPath, place.IsArchive, place.Answer));
    }

    [Fact]
    public async Task Fewer_images_a_folder_with_a_pdf_and_a_library_folders_top_level_stay_separate()
    {
        await AddAsync([.. Images("Few", 19), .. Images("Adventure", 20), "Adventure/Adventure.pdf", .. Images("", 20).Select(p => p.TrimStart('/'))]);

        Assert.Empty(await _packs.PlanAsync(Ct));
        Assert.DoesNotContain(await _entries.GetCurrentAsync(ct: Ct), c => c.Kind == EntryKind.Pack);
    }

    [Fact]
    public async Task A_split_pack_gives_each_image_its_card_back_with_what_was_set_and_stays_split_until_undone()
    {
        var images = await AddAsync(Images("Maps", 20));
        var zombie = images["Maps/Zombie 7.png"];
        await new MetadataStore(new Factory(_database)).SetValuesAsync(zombie, MetadataFields.Title, ["Crypt map"], Ct);
        await _packs.PlanAsync(Ct);
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).EntryId;

        var changed = (await _packs.SplitAsync(pack, Ct))!;

        Assert.Equal(21, changed.Count);
        Assert.Equal(pack, changed[0]);
        var cards = await _entries.GetCurrentAsync(ct: Ct);
        Assert.Equal(20, cards.Count);
        Assert.All(cards, c => Assert.Equal(EntryKind.Whole, c.Kind));
        Assert.Equal("Crypt map", (await new MetadataStore(new Factory(_database)).GetAsync(zombie, Ct)).Compute()[MetadataFields.Title].First!.Value);
        Assert.Empty(await _packs.PlanAsync(Ct));
        Assert.Null(await _packs.SplitAsync(zombie, Ct));

        // Undo: the same pack card, with the same images.
        var repacked = await _packs.RepackAsync(pack, [.. changed.Skip(1)], Ct);
        Assert.Equal(pack, repacked[0]);
        var again = Assert.Single(await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal((pack, 20), (again.EntryId, again.Members!.Count));
    }

    [Fact]
    public async Task A_ZIP_of_images_is_one_pack_subfolders_included()
    {
        _files.Add(new ScannedFile("Downloads/Harbor Set.zip", 5000, Monday, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var zip = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.IsArchive);
        var members = Enumerable.Range(1, 20).Select(i => new ArchiveMember(i <= 10 ? $"Day/Harbor {i}.png" : $"Night/Harbor {i}.png", 500, Monday, (uint)i, null));
        foreach (var member in (await _library.ReconcileArchiveAsync(zip, [.. members], Ct))!)
            await _library.AttachHashAsync(member, NextHash(), Ct);
        await _library.AttachArchiveHashAsync(zip, NextHash(), Ct);

        await _packs.PlanAsync(Ct);

        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal((EntryKind.Pack, "Harbor Set", 20), (pack.Kind, pack.Name, pack.Members!.Count));
        Assert.True((await _packs.GetPlaceAsync(pack.EntryId, Ct))!.IsArchive);
    }

    [Fact]
    public async Task Smaller_folders_and_images_beside_PDFs_are_proposed_and_each_answer_can_be_undone()
    {
        await AddAsync([.. Images("Handouts", 12), .. Images("Adventure", 6), "Adventure/Adventure.pdf", .. Images("Few", 4)]);

        // Proposing changes no card.
        Assert.Empty(await _packs.PlanAsync(Ct));
        var proposals = await _packs.GetProposalsAsync(Ct);
        Assert.Equal([("Adventure", 6, true), ("Handouts", 12, false)], proposals.Select(p => (p.Name, p.Images.Count, p.BesidePdfs)));
        Assert.DoesNotContain(await _entries.GetCurrentAsync(ct: Ct), c => c.Kind == EntryKind.Pack);
        Assert.Empty(await _packs.PlanAsync(Ct));

        // Make a pack: the images become one card, and the question is answered.
        var handouts = proposals[1].PackId;
        var made = (await _packs.AnswerAsync(handouts, PackAnswer.Packed, PackAnswer.Proposed, Ct))!;
        Assert.Equal((PackAnswer.Proposed, 13), (made.Previous, made.Changed.Count));
        Assert.Equal(12, Assert.Single(await _entries.GetCurrentAsync([handouts], Ct)).Members!.Count);
        Assert.Null(await _packs.AnswerAsync(handouts, PackAnswer.Split, PackAnswer.Proposed, Ct));
        Assert.Single(await _packs.GetProposalsAsync(Ct));

        // Undo asks again, with every image on its own card.
        await _packs.AnswerAsync(handouts, made.Previous, ct: Ct);
        Assert.Empty(await _entries.GetCurrentAsync([handouts], Ct));
        Assert.Equal(2, (await _packs.GetProposalsAsync(Ct)).Count);

        // Keep separate is remembered.
        Assert.Equal(PackAnswer.Proposed, (await _packs.AnswerAsync(proposals[0].PackId, PackAnswer.Split, PackAnswer.Proposed, Ct))!.Previous);
        Assert.Empty(await _packs.PlanAsync(Ct));
        Assert.Equal([handouts], (await _packs.GetProposalsAsync(Ct)).Select(p => p.PackId));
    }

    [Fact]
    public async Task A_proposed_folder_that_grows_to_twenty_images_is_packed_by_itself()
    {
        await AddAsync(Images("Tokens/Undead", 8));
        await _packs.PlanAsync(Ct);
        var proposal = Assert.Single(await _packs.GetProposalsAsync(Ct));

        await AddAsync(Images("Tokens/Undead", 12, from: 9));

        Assert.Equal(21, (await _packs.PlanAsync(Ct)).Count);
        Assert.Empty(await _packs.GetProposalsAsync(Ct));
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal((proposal.PackId, 20), (pack.EntryId, pack.Members!.Count));
    }

    /// <summary>Packs compare by value, members included.</summary>
    [Fact]
    public async Task Pack_together_makes_a_pack_of_loose_images_that_a_rescan_leaves_alone_and_undo_takes_apart()
    {
        var images = await AddAsync("Handouts/Harbor.png", "Handouts/Crypt.png", "Maps/Keep.jpg", "Handouts/Guide.pdf");
        await _packs.PlanAsync(Ct);

        var together = await _packs.PackTogetherAsync([images["Maps/Keep.jpg"], images["Handouts/Harbor.png"], images["Handouts/Crypt.png"]], Ct);

        Assert.NotNull(together);
        Assert.True(together.Created);
        Assert.Empty(together.Merged);
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct), c => c.Kind == EntryKind.Pack);
        Assert.Equal((together.PackId, (string?)null), (pack.EntryId, pack.Name));
        Assert.Equal(["Crypt.png", "Harbor.png", "Keep.jpg"], pack.Members!.Select(m => m.Name));
        Assert.Equal([together.PackId, .. together.Images], together.Changed);
        Assert.Empty(await _packs.PlanAsync(Ct));
        Assert.Null(await _packs.GetPlaceAsync(together.PackId, Ct));
        var folders = await _packs.GetFoldersAsync(together.PackId, Ct);
        Assert.Equal([("Handouts", 2), ("Maps", 1)], folders.Select(f => (Path.GetFileName(f.FullPath), f.Images)));

        await _packs.UndoPackTogetherAsync(together, Ct);

        var cards = await _entries.GetCurrentAsync(ct: Ct);
        Assert.Equal(4, cards.Count);
        Assert.All(cards, c => Assert.Equal(EntryKind.Whole, c.Kind));
    }

    [Fact]
    public async Task Pack_together_adds_images_to_a_folder_pack_which_still_grows_and_a_split_frees_them_all()
    {
        var images = await AddAsync([.. Images("Tokens/Undead", 20), "Extras/Lich.png"]);
        await _packs.PlanAsync(Ct);
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct), c => c.Kind == EntryKind.Pack).EntryId;

        var together = await _packs.PackTogetherAsync([images["Extras/Lich.png"], pack], Ct);

        Assert.NotNull(together);
        Assert.Equal((pack, false), (together.PackId, together.Created));
        Assert.Equal([images["Extras/Lich.png"]], together.Images);
        Assert.Equal(21, Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).Members!.Count);
        var folders = await _packs.GetFoldersAsync(pack, Ct);
        Assert.Equal([("Undead", 20), ("Extras", 1)], folders.Select(f => (Path.GetFileName(f.FullPath), f.Images)));

        // Its folder still fills it.
        await AddAsync("Tokens/Undead/Ghoul.png");
        await _packs.PlanAsync(Ct);
        Assert.Equal(22, Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).Members!.Count);

        var freed = (await _packs.SplitAsync(pack, Ct))!;
        Assert.Equal(22, (await _entries.GetCurrentAsync(ct: Ct)).Count);
        await _packs.RepackAsync(pack, [.. freed.Skip(1)], Ct);
        Assert.Equal(22, Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).Members!.Count);
    }

    [Fact]
    public async Task Pack_together_merges_packs_into_the_oldest_which_takes_their_folders_images_and_undo_brings_them_back()
    {
        var metadata = new MetadataStore(new Factory(_database));
        await AddAsync([.. Images("Tokens/Undead", 20), .. Images("Tokens/Beasts", 20)]);
        await _packs.PlanAsync(Ct);
        var packs = (await _entries.GetCurrentAsync(ct: Ct)).Select(c => c.EntryId).OrderBy(e => e.Value).ToList();
        Assert.Equal(2, packs.Count);
        var (older, newer) = (packs[0], packs[1]);
        await metadata.SetValuesAsync(newer, MetadataFields.Tags, ["Night"], Ct);

        var together = await _packs.PackTogetherAsync([newer, older], Ct);

        Assert.NotNull(together);
        Assert.Equal((older, false), (together.PackId, together.Created));
        Assert.Equal([newer], together.Merged);
        Assert.Equal(22, together.Changed.Count);
        var card = Assert.Single(await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal((older, 40), (card.EntryId, card.Members!.Count));
        Assert.Equal(["Night"], (await metadata.GetAsync(older, Ct)).Compute()[MetadataFields.Tags].Values.Select(v => v.Value));

        // The merged pack's folders fill the pack it merged into.
        await AddAsync("Tokens/Beasts/Owlbear.png", "Tokens/Undead/Ghoul.png");
        await _packs.PlanAsync(Ct);
        Assert.Equal(42, Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).Members!.Count);

        await _packs.UndoPackTogetherAsync(together, Ct);

        var cards = await _entries.GetCurrentAsync(ct: Ct);
        Assert.Equal([(older, 22), (newer, 20)], cards.OrderBy(c => c.EntryId.Value).Select(c => (c.EntryId, c.Members!.Count)));
        Assert.Equal(["Night"], (await metadata.GetAsync(newer, Ct)).Compute()[MetadataFields.Tags].Values.Select(v => v.Value));
    }

    [Fact]
    public async Task A_split_of_a_merged_pack_keeps_every_folder_behind_it_from_packing_again()
    {
        await AddAsync([.. Images("Tokens/Undead", 20), .. Images("Tokens/Beasts", 20)]);
        await _packs.PlanAsync(Ct);
        var packs = (await _entries.GetCurrentAsync(ct: Ct)).Select(c => c.EntryId).OrderBy(e => e.Value).ToList();
        await _packs.PackTogetherAsync(packs, Ct);

        var freed = (await _packs.SplitAsync(packs[0], Ct))!;

        Assert.Equal(41, freed.Count);
        Assert.Empty(await _packs.PlanAsync(Ct));
        Assert.Equal(40, (await _entries.GetCurrentAsync(ct: Ct)).Count);
        Assert.Null(await _packs.SplitAsync(packs[1], Ct));
    }

    [Fact]
    public async Task Pack_together_refuses_books_single_cards_and_merged_packs()
    {
        var images = await AddAsync([.. Images("Tokens/Undead", 20), .. Images("Tokens/Beasts", 20), "Extras/Lich.png", "Extras/Guide.pdf"]);
        await _packs.PlanAsync(Ct);
        var packs = (await _entries.GetCurrentAsync(ct: Ct)).Where(c => c.Kind == EntryKind.Pack).Select(c => c.EntryId).OrderBy(e => e.Value).ToList();

        Assert.Null(await _packs.PackTogetherAsync([images["Extras/Lich.png"]], Ct));
        Assert.Null(await _packs.PackTogetherAsync([images["Extras/Lich.png"], images["Extras/Guide.pdf"]], Ct));
        Assert.Null(await _packs.PackTogetherAsync([images["Tokens/Undead/Zombie 1.png"], images["Extras/Lich.png"]], Ct));
        await _packs.PackTogetherAsync(packs, Ct);
        Assert.Null(await _packs.PackTogetherAsync([packs[1], images["Extras/Lich.png"]], Ct));
    }

    /// <summary>
    /// Moves a folder or ZIP as Explorer would on a drive without file IDs: its files keep their names, sizes and dates,
    /// which is how the scan follows them.
    /// </summary>
    async Task<ReconcileResult> MoveAsync(string from, string to)
    {
        for (var i = 0; i < _files.Count; i++)
            if (_files[i].RelativePath == from || _files[i].RelativePath.StartsWith(from + "/", StringComparison.Ordinal))
                _files[i] = _files[i] with { RelativePath = to + _files[i].RelativePath[from.Length..] };
        return await _library.ReconcileRootAsync(_root, _files, ct: Ct);
    }

    [Fact]
    public async Task A_packed_folder_that_moved_keeps_its_pack_card_and_new_images_there_still_join_it()
    {
        await AddAsync(Images("Tokens/Undead", 20));
        await _packs.PlanAsync(Ct);
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).EntryId;

        Assert.Equal(20, (await MoveAsync("Tokens/Undead", "Monsters/Undead Horde")).Moved);
        Assert.Contains(pack, await _packs.PlanAsync(Ct));

        var card = Assert.Single(await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal((pack, "Undead Horde", 20), (card.EntryId, card.Name, card.Members!.Count));
        var place = Assert.Single(await _packs.GetPlacesAsync(ct: Ct));
        Assert.Equal((pack, Path.Combine(_dir, "Library", "Monsters/Undead Horde")), (place.Key, place.Value.FullPath));
        // A new image there joins the same pack.
        var added = await AddAsync("Monsters/Undead Horde/Zombie 21.png");
        Assert.Equal([added.Single().Value, pack], await _packs.PlanAsync(Ct));
        Assert.Equal(21, Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).Members!.Count);
        Assert.Single(await _packs.GetPlacesAsync(ct: Ct));
    }

    [Fact]
    public async Task A_copy_of_a_packed_folder_makes_no_pack_and_the_pack_follows_it_once_the_original_has_gone()
    {
        await AddAsync(Images("Tokens/Undead", 20));
        await _packs.PlanAsync(Ct);
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).EntryId;
        Dictionary<string, ContentHash> hashes;
        await using (var db = new Factory(_database).CreateDbContext())
            hashes = (await db.FileLocations.AsNoTracking().Where(f => f.ContentHash != null).ToListAsync(Ct))
                .ToDictionary(f => PackStore.FileName(f.RelativePath), f => ContentHash.Parse(f.ContentHash!));

        // Copied to another drive's folder, with new dates, so the copies are read and found to be the same images.
        _files.AddRange(Images("Backup/Undead", 20).Select(p => new ScannedFile(p, 1000, Monday.AddDays(1), OnlineOnly: false)));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        foreach (var file in await _library.NextUnhashedAsync(100, includeOnlineOnly: true, Ct))
            await _library.AttachHashAsync(file, hashes[Path.GetFileName(file.FullPath)], Ct);
        Assert.Empty(await _packs.PlanAsync(Ct));
        Assert.Equal(Path.Combine(_dir, "Library", "Tokens/Undead"), Assert.Single(await _packs.GetPlacesAsync(ct: Ct)).Value.FullPath);

        // The original gone, as after moving a folder to another drive: the pack is the copy's now.
        _files.RemoveAll(f => f.RelativePath.StartsWith("Tokens/", StringComparison.Ordinal));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        Assert.Contains(pack, await _packs.PlanAsync(Ct));
        var place = Assert.Single(await _packs.GetPlacesAsync(ct: Ct));
        Assert.Equal((pack, Path.Combine(_dir, "Library", "Backup/Undead")), (place.Key, place.Value.FullPath));
        var card = Assert.Single(await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal((pack, 20), (card.EntryId, card.Members!.Count));
    }

    [Fact]
    public async Task A_split_folder_or_a_proposal_that_moved_keeps_its_answer_and_its_place_in_Needs_review()
    {
        await AddAsync([.. Images("Maps", 20), .. Enumerable.Range(1, 8).Select(i => $"Handouts/Handout {i}.png")]);
        await _packs.PlanAsync(Ct);
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct), c => c.Kind == EntryKind.Pack).EntryId;
        await _packs.SplitAsync(pack, Ct);
        var proposal = Assert.Single(await _packs.GetProposalsAsync(Ct));

        Assert.Equal(20, (await MoveAsync("Maps", "Old/Maps")).Moved);
        Assert.Equal(8, (await MoveAsync("Handouts", "Session 1/Handouts")).Moved);

        Assert.Empty(await _packs.PlanAsync(Ct));
        Assert.DoesNotContain(await _entries.GetCurrentAsync(ct: Ct), c => c.Kind == EntryKind.Pack);
        var split = (await _packs.GetPlaceAsync(pack, Ct))!;
        Assert.Equal((Path.Combine(_dir, "Library", "Old/Maps"), PackAnswer.Split), (split.FullPath, split.Answer));
        var moved = Assert.Single(await _packs.GetProposalsAsync(Ct));
        Assert.Equal((proposal.PackId, Path.Combine(_dir, "Library", "Session 1/Handouts"), 8),
            (moved.PackId, moved.Place.FullPath, moved.Images.Count));
        Assert.Equal(2, (await _packs.GetPlacesAsync(ct: Ct)).Count);
    }

    [Fact]
    public async Task A_ZIP_pack_moved_to_another_folder_keeps_its_pack_without_the_ZIP_being_read_again()
    {
        _files.Add(new ScannedFile("Downloads/Harbor Set.zip", 5000, Monday, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var zip = Assert.Single(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        var members = Enumerable.Range(1, 20).Select(i => new ArchiveMember($"Harbor {i}.png", 500, Monday, (uint)i, null));
        foreach (var member in (await _library.ReconcileArchiveAsync(zip, [.. members], Ct))!)
            await _library.AttachHashAsync(member, NextHash(), Ct);
        await _library.AttachArchiveHashAsync(zip, NextHash(), Ct);
        await _packs.PlanAsync(Ct);
        var pack = Assert.Single(await _entries.GetCurrentAsync(ct: Ct)).EntryId;

        Assert.Equal(1, (await MoveAsync("Downloads/Harbor Set.zip", "Maps/Harbor Set.zip")).Moved);

        Assert.Empty(await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct));
        Assert.Contains(pack, await _packs.PlanAsync(Ct));
        var card = Assert.Single(await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal((pack, 20), (card.EntryId, card.Members!.Count));
        var place = (await _packs.GetPlaceAsync(pack, Ct))!;
        Assert.Equal((Path.Combine(_dir, "Library", "Maps/Harbor Set.zip"), true), (place.FullPath, place.IsArchive));
    }

    sealed class PackComparer : IEqualityComparer<EntryDocument>
    {
        public bool Equals(EntryDocument? x, EntryDocument? y) =>
            x is not null && y is not null && x with { Members = null } == y with { Members = null } && x.Members!.SequenceEqual(y.Members!);

        public int GetHashCode(EntryDocument obj) => obj.EntryId.GetHashCode();
    }
}
