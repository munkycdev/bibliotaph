using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.Data.Sqlite;

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
        SqliteConnection.ClearAllPools();
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
        var repacked = await _packs.RepackAsync(pack, Ct);
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

    /// <summary>Packs compare by value, members included.</summary>
    sealed class PackComparer : IEqualityComparer<EntryDocument>
    {
        public bool Equals(EntryDocument? x, EntryDocument? y) =>
            x is not null && y is not null && x with { Members = null } == y with { Members = null } && x.Members!.SequenceEqual(y.Members!);

        public int GetHashCode(EntryDocument obj) => obj.EntryId.GetHashCode();
    }
}
