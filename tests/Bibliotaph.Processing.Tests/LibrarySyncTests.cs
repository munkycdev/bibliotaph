using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Tests;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Processing.Tests;

/// <summary>
/// Library sync end to end: two catalogs, as on two PCs, each writing its sync file to one place and merging the
/// other's, as the web library would carry them.
/// </summary>
public sealed class LibrarySyncTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-sync-").FullName;
    Pc _desk = null!;
    Pc _laptop = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly Hub _hub = new();

    /// <summary>Where the PCs' sync files meet, as the web library keeps them, and which can be out of reach.</summary>
    sealed class Hub
    {
        readonly Dictionary<string, byte[]> _files = [];

        public bool Offline { get; set; }

        public bool Has(string machine) => _files.ContainsKey(machine);

        public string Text(string machine) => System.Text.Encoding.UTF8.GetString(_files[machine]);

        public async Task<SyncChannelRead> ReadAsync(string machine, CancellationToken ct)
        {
            if (Offline) return new SyncChannelRead([], "The web library can't be reached.");
            var peers = new List<SyncFileContent>();
            foreach (var bytes in _files.Where(f => f.Key != machine).Select(f => f.Value).ToList())
            {
                using var stream = new MemoryStream(bytes);
                peers.Add(await SyncFile.ReadAsync(stream, ct));
            }
            return new SyncChannelRead(peers, null);
        }

        public async Task<string?> WriteAsync(string machine, Func<Stream, CancellationToken, Task> write, CancellationToken ct)
        {
            if (Offline) return "The web library can't be reached.";
            using var stream = new MemoryStream();
            await write(stream, ct);
            _files[machine] = stream.ToArray();
            return null;
        }
    }

    /// <summary>One PC's way to the hub.</summary>
    sealed class Channel(Hub hub, string machine) : ISyncChannel
    {
        public bool IsOn => true;

        public Task<SyncChannelRead> ReadPeersAsync(CancellationToken ct) => hub.ReadAsync(machine, ct);

        public Task<string?> WriteOwnAsync(Func<Stream, CancellationToken, Task> write, CancellationToken ct) => hub.WriteAsync(machine, write, ct);

        public Task<bool> HasChangesAsync(CancellationToken ct) => Task.FromResult(false);

        public event EventHandler? Changed { add { } remove { } }
    }

    /// <summary>A clock the test moves on, so which change is newer is the test's to say.</summary>
    sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class Factory(CatalogDatabase database) : IDbContextFactory<CatalogDbContext>
    {
        public CatalogDbContext CreateDbContext() => database.CreateContext();
    }

    /// <summary>One PC: its own data folder, catalog and stores, and its library sync.</summary>
    sealed class Pc(string name, AppPaths paths, CatalogDatabase catalog, Clock clock, Hub hub) : IDisposable
    {
        public string Name => name;
        public AppPaths Paths => paths;
        public CatalogDatabase Catalog => catalog;
        public Clock Clock => clock;
        public Factory Contexts { get; } = new(catalog);
        public LibrarySync Sync { get; private set; } = null!;
        public MetadataStore Metadata => new(Contexts, clock);
        public CollectionStore Collections => new(Contexts, clock);
        public VocabularyStore Vocabulary => new(Contexts, clock);

        /// <summary>A type the AI proposed for the book, as a pending term with the book's value waiting on it.</summary>
        public async Task ProposeTypeAsync(EntryId book, char hash, string label)
        {
            var (term, _) = await Vocabulary.ProposeTermAsync("type", label, Ct);
            await Metadata.AddSuggestionsAsync(book, new string(hash, 64), [new MetadataProposal(MetadataFields.Types, term.Key, AssertionOrigin.Ai)], Ct);
        }

        public async Task<IReadOnlyList<string>> TypesAsync(char hash) =>
            [.. (await Metadata.GetAsync(await BookAsync(hash), Ct)).Compute()[MetadataFields.Types].Values.Select(v => v.Value)];

        public void Start() => Sync = new LibrarySync(new LibrarySyncStore(Contexts, Metadata, new VocabularyStore(Contexts), clock), new SettingsStore(Contexts),
            catalog, null, clock, null, name, [new Channel(hub, name)]);

        /// <summary>Adds a file's document and its card, as indexing would: content hash <paramref name="hash"/> (64 of that character).</summary>
        public async Task<EntryId> AddBookAsync(char hash)
        {
            await using var db = catalog.CreateContext();
            var document = db.Documents.Add(new Document { ContentHash = new string(hash, 64), Format = "pdf", CreatedUtc = clock.GetUtcNow().UtcDateTime }).Entity;
            var entry = db.Entries.Add(new Entry { Kind = EntryKind.Whole, CreatedUtc = clock.GetUtcNow().UtcDateTime }).Entity;
            db.EntrySources.Add(new EntrySource { Entry = entry, Document = document, IsCurrent = true });
            await db.SaveChangesAsync(Ct);
            return new EntryId(entry.Id);
        }

        public async Task<EntryId> BookAsync(char hash)
        {
            await using var db = catalog.CreateContext();
            var hex = new string(hash, 64);
            return new EntryId(await db.EntrySources.Where(s => s.Document.ContentHash == hex && s.FirstPdfPage == null).Select(s => s.EntryId).SingleAsync(Ct));
        }

        public async Task<string?> TitleAsync(char hash)
        {
            var values = (await Metadata.GetAsync(await BookAsync(hash), Ct)).Compute()[MetadataFields.Title].Values;
            return values.Count > 0 ? values[0].Value : null;
        }

        public void Later(int minutes) => clock.Now = clock.Now.AddMinutes(minutes);

        public PackStore Packs => new(Contexts, clock);

        readonly List<ScannedFile> _files = [];
        long? _root;

        /// <summary>
        /// Scans files into a library folder and hashes them, each by its path, so the same path on both PCs is the
        /// same file. Returns each file's entry by its path.
        /// </summary>
        public async Task<Dictionary<string, EntryId>> AddFilesAsync(params string[] files)
        {
            var library = new LibraryStore(Contexts);
            var folder = Path.Combine(paths.Root, "Library");
            _root ??= (await new SourceRootStore(Contexts).AddAsync(folder, Ct)).Id;
            _files.AddRange(files.Select(f => new ScannedFile(f, 1000, clock.GetUtcNow().UtcDateTime, OnlineOnly: false)));
            await library.ReconcileRootAsync(_root.Value, _files, ct: Ct);
            var entries = new Dictionary<string, EntryId>();
            foreach (var file in await library.NextUnhashedAsync(1000, includeOnlineOnly: true, Ct))
            {
                var path = Path.GetRelativePath(folder, file.FullPath).Replace('\\', '/');
                var hash = ContentHash.Parse(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))));
                var document = (await library.AttachHashAsync(file, hash, Ct))!.Value.DocumentId;
                entries[path] = (await new EntryStore(Contexts).GetEntryAsync(document, Ct))!.EntryId;
            }
            return entries;
        }

        public async Task<IReadOnlyList<EntryDocument>> PackCardsAsync() =>
            [.. (await new EntryStore(Contexts).GetCurrentAsync(ct: Ct)).Where(c => c.Kind == EntryKind.Pack)];

        public void Dispose()
        {
            Sync.Dispose();
            catalog.ClearPool();
        }
    }

    async Task<Pc> PcAsync(string name, DateTimeOffset start)
    {
        var paths = new AppPaths(Path.Combine(_dir, name));
        foreach (var directory in paths.Directories) Directory.CreateDirectory(directory);
        var clock = new Clock(start);
        var catalog = new CatalogDatabase(paths.CatalogDatabase, paths.Backups, clock);
        await catalog.MigrateFromTemplateAsync(Ct);
        var pc = new Pc(name, paths, catalog, clock, _hub);
        pc.Start();
        return pc;
    }

    public async ValueTask InitializeAsync()
    {
        var nine = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        _desk = await PcAsync("DESKTOP-1", nine);
        _laptop = await PcAsync("LAPTOP-2", nine.AddSeconds(30));
    }

    public ValueTask DisposeAsync()
    {
        _desk.Dispose();
        _laptop.Dispose();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>The desk writes and merges, then the laptop, then the desk again: what a round of the web library carrying both files does.</summary>
    async Task RoundAsync()
    {
        await _desk.Sync.SyncNowAsync(Ct);
        await _laptop.Sync.SyncNowAsync(Ct);
        await _desk.Sync.SyncNowAsync(Ct);
    }

    [Fact]
    public async Task Each_PC_writes_its_own_file_and_merges_the_others()
    {
        var abbey = await _desk.AddBookAsync('a');
        await _laptop.AddBookAsync('a');
        await _desk.Metadata.SetValuesAsync(abbey, MetadataFields.Title, ["The Drowned Abbey"], Ct);
        await new FavoriteStore(_desk.Contexts).SetAsync([abbey], true, Ct);
        var campaign = await _desk.Collections.CreateAsync("Spring campaign", ct: Ct);
        await _desk.Collections.AddAsync(campaign.Id, [abbey], Ct);
        await new NoteStore(_desk.Contexts).SetEntryNoteAsync(abbey, "Run the bell tower first.", Ct);
        var binder = await new SessionStore(_desk.Contexts).CreateAsync("Session 4", ct: Ct);
        await new SessionStore(_desk.Contexts).AddItemsAsync(binder.Id, [new NewSessionItem(abbey, Label: "Bell tower")], ct: Ct);
        await new PinStore(_desk.Contexts).PinAsync(PinTarget.Collection(campaign.Id), Ct);

        await RoundAsync();

        Assert.True(_hub.Has("DESKTOP-1"));
        Assert.True(_hub.Has("LAPTOP-2"));
        var book = await _laptop.BookAsync('a');
        Assert.Equal("The Drowned Abbey", await _laptop.TitleAsync('a'));
        Assert.True(await new FavoriteStore(_laptop.Contexts).IsFavoriteAsync(book, Ct));
        Assert.Equal("Run the bell tower first.", await new NoteStore(_laptop.Contexts).GetEntryNoteAsync(book, Ct));
        var collection = Assert.Single(await _laptop.Collections.ListAsync(Ct));
        Assert.Equal("Spring campaign", collection.Name);
        Assert.True(collection.Pinned);
        Assert.Equal([collection.Id], await _laptop.Collections.GetForEntryAsync(book, Ct));
        var pack = Assert.Single(await new SessionStore(_laptop.Contexts).ListAsync(Ct));
        var contents = await new SessionStore(_laptop.Contexts).GetAsync(pack.Id, Ct);
        Assert.Equal("Bell tower", Assert.Single(contents!.Items).Label);
        Assert.Equal("Last merged from DESKTOP-1 at 09:00.", LibrarySync.DescribeWeb(_laptop.Sync.Status, _laptop.Clock.Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task The_newer_change_to_a_field_wins_and_changes_to_different_fields_both_survive()
    {
        var deskBook = await _desk.AddBookAsync('a');
        var laptopBook = await _laptop.AddBookAsync('a');
        await _desk.Metadata.SetValuesAsync(deskBook, MetadataFields.Title, ["Abbey (desk)"], Ct);
        await _desk.Sync.SyncNowAsync(Ct);
        await _laptop.Sync.SyncNowAsync(Ct);

        // Later, each PC changes the book while the other's file is out of date: the title on both, a different field on each.
        _desk.Later(5);
        await _desk.Metadata.SetValuesAsync(deskBook, MetadataFields.Title, ["Abbey, edited on the desk"], Ct);
        await _desk.Metadata.SetValuesAsync(deskBook, MetadataFields.Types, ["adventure"], Ct);
        await _desk.Sync.WriteNowAsync(Ct);
        _laptop.Later(10);
        await _laptop.Metadata.SetValuesAsync(laptopBook, MetadataFields.Title, ["Abbey, edited on the laptop"], Ct);
        await new FavoriteStore(_laptop.Contexts).SetAsync([laptopBook], true, Ct);

        await RoundAsync();

        foreach (var pc in new[] { _desk, _laptop })
        {
            var book = await pc.BookAsync('a');
            Assert.Equal("Abbey, edited on the laptop", await pc.TitleAsync('a'));
            Assert.Equal(["adventure"], (await pc.Metadata.GetAsync(book, Ct)).Compute()[MetadataFields.Types].Values.Select(v => v.Value));
            Assert.True(await new FavoriteStore(pc.Contexts).IsFavoriteAsync(book, Ct), $"{pc.Name} lost the heart.");
        }
    }

    [Fact]
    public async Task A_deletion_reaches_the_other_PC_and_nothing_comes_back()
    {
        var deskBook = await _desk.AddBookAsync('a');
        await _laptop.AddBookAsync('a');
        var campaign = await _desk.Collections.CreateAsync("Spring campaign", ct: Ct);
        await _desk.Collections.AddAsync(campaign.Id, [deskBook], Ct);
        var view = await new SmartViewStore(_desk.Contexts).CreateAsync("Haunted places", "{}", Ct);
        await new FavoriteStore(_desk.Contexts).SetAsync([deskBook], true, Ct);
        await RoundAsync();
        Assert.Single(await _laptop.Collections.ListAsync(Ct));

        _laptop.Later(5);
        var laptopCampaign = (await _laptop.Collections.ListAsync(Ct)).Single();
        await _laptop.Collections.DeleteAsync(laptopCampaign.Id, Ct);
        await new SmartViewStore(_laptop.Contexts).DeleteAsync((await new SmartViewStore(_laptop.Contexts).ListAsync(Ct)).Single().Id, Ct);
        await new FavoriteStore(_laptop.Contexts).SetAsync([await _laptop.BookAsync('a')], false, Ct);
        await _laptop.Sync.SyncNowAsync(Ct);
        await RoundAsync();
        await RoundAsync();

        Assert.Empty(await _desk.Collections.ListAsync(Ct));
        Assert.Empty(await _laptop.Collections.ListAsync(Ct));
        Assert.Null(await new SmartViewStore(_desk.Contexts).GetAsync(view.Id, Ct));
        Assert.False(await new FavoriteStore(_desk.Contexts).IsFavoriteAsync(deskBook, Ct));
    }

    [Fact]
    public async Task Work_on_a_book_only_the_other_PC_has_is_kept_and_applied_when_its_file_arrives()
    {
        var crypt = await _desk.AddBookAsync('c');
        await _desk.Metadata.SetValuesAsync(crypt, MetadataFields.Title, ["Crypt of the Ash King"], Ct);
        await new NoteStore(_desk.Contexts).SetEntryNoteAsync(crypt, "Players never found the side door.", Ct);
        var campaign = await _desk.Collections.CreateAsync("Spring campaign", ct: Ct);
        await _desk.Collections.AddAsync(campaign.Id, [crypt], Ct);
        await RoundAsync();

        // The laptop has the collection, but not the book yet; nor does its file say anything of the book.
        Assert.Single(await _laptop.Collections.ListAsync(Ct));
        var laptopFile = _hub.Text("LAPTOP-2");
        Assert.DoesNotContain(new string('c', 64), laptopFile, StringComparison.Ordinal);

        // The file arrives, and the user hearts it before the next merge: both the desk's work and the heart stay.
        _laptop.Later(60);
        var book = await _laptop.AddBookAsync('c');
        await new FavoriteStore(_laptop.Contexts).SetAsync([book], true, Ct);
        await _laptop.Sync.SyncNowAsync(Ct);

        Assert.Equal("Crypt of the Ash King", await _laptop.TitleAsync('c'));
        Assert.Equal("Players never found the side door.", await new NoteStore(_laptop.Contexts).GetEntryNoteAsync(book, Ct));
        Assert.Single(await _laptop.Collections.GetForEntryAsync(book, Ct));
        Assert.True(await new FavoriteStore(_laptop.Contexts).IsFavoriteAsync(book, Ct));

        await _desk.Sync.SyncNowAsync(Ct);
        Assert.True(await new FavoriteStore(_desk.Contexts).IsFavoriteAsync(crypt, Ct));
        Assert.Equal("Players never found the side door.", await new NoteStore(_desk.Contexts).GetEntryNoteAsync(crypt, Ct));
    }

    [Fact]
    public async Task Merging_the_same_files_again_changes_nothing()
    {
        var abbey = await _desk.AddBookAsync('a');
        await _laptop.AddBookAsync('a');
        await _desk.Metadata.SetValuesAsync(abbey, MetadataFields.Tags, ["Friday game", "Spooky"], Ct);
        await _desk.Collections.CreateAsync("Spring campaign", ct: Ct);
        await RoundAsync();
        await _laptop.Sync.SyncNowAsync(Ct);
        var deskFile = _hub.Text("DESKTOP-1");
        var laptopFile = _hub.Text("LAPTOP-2");

        _desk.Later(5);
        _laptop.Later(5);
        var desk = await _desk.Sync.SyncNowAsync(Ct);
        var laptop = await _laptop.Sync.SyncNowAsync(Ct);

        Assert.False(desk.Changed);
        Assert.False(laptop.Changed);
        Assert.False(desk.Wrote);
        Assert.False(laptop.Wrote);
        Assert.Equal(deskFile, _hub.Text("DESKTOP-1"));
        Assert.Equal(laptopFile, _hub.Text("LAPTOP-2"));
    }

    [Fact]
    public async Task A_book_owned_elsewhere_travels_with_its_values_and_so_does_its_removal()
    {
        var entries = new EntryStore(_desk.Contexts);
        var vellum = await entries.AddElsewhereAsync(Ct);
        await _desk.Metadata.SetValuesAsync(vellum, MetadataFields.Title, ["Vellum Atlas"], Ct);
        await RoundAsync();

        var onLaptop = (await new EntryStore(_laptop.Contexts).GetCurrentAsync(ct: Ct)).Single(e => e.Kind == EntryKind.Elsewhere);
        Assert.Equal("Vellum Atlas", (await _laptop.Metadata.GetAsync(onLaptop.EntryId, Ct)).Compute()[MetadataFields.Title].Values.Single().Value);

        _desk.Later(5);
        await entries.RemoveElsewhereAsync(vellum, Ct);
        await RoundAsync();

        Assert.DoesNotContain(await new EntryStore(_laptop.Contexts).GetCurrentAsync(ct: Ct), e => e.Kind == EntryKind.Elsewhere);
    }

    [Fact]
    public async Task A_change_on_the_other_PC_while_this_one_is_offline_is_merged_when_it_is_back()
    {
        var abbey = await _desk.AddBookAsync('a');
        await _laptop.AddBookAsync('a');
        await RoundAsync();
        await _desk.Metadata.SetValuesAsync(abbey, MetadataFields.Title, ["The Drowned Abbey"], Ct);
        await _desk.Sync.SyncNowAsync(Ct);

        _hub.Offline = true;
        var offline = await _laptop.Sync.SyncNowAsync(Ct);
        Assert.Empty(offline.MergedFrom);
        Assert.NotNull(_laptop.Sync.Status.Problem);
        _hub.Offline = false;
        await _laptop.Sync.SyncNowAsync(Ct);

        Assert.Equal("The Drowned Abbey", await _laptop.TitleAsync('a'));
    }

    [Fact]
    public async Task A_term_the_AI_proposed_on_both_PCs_and_accepted_on_one_is_accepted_on_the_other()
    {
        var deskBook = await _desk.AddBookAsync('a');
        var laptopBook = await _laptop.AddBookAsync('a');
        await _desk.ProposeTypeAsync(deskBook, 'a', "Heist Kit");
        await _laptop.ProposeTypeAsync(laptopBook, 'a', "Heist Kit");
        await RoundAsync();
        Assert.Single(await _laptop.Vocabulary.GetPendingAsync(Ct));

        _desk.Later(5);
        await _desk.Vocabulary.AcceptPendingAsync(Assert.Single(await _desk.Vocabulary.GetPendingAsync(Ct)).Id, Ct);
        await RoundAsync();

        Assert.Empty(await _laptop.Vocabulary.GetPendingAsync(Ct));
        Assert.Equal("Heist Kit", (await _laptop.Vocabulary.GetAsync(Ct)).Resolve("type", "heist kit")?.Label);
        Assert.Equal(["heist-kit"], await _laptop.TypesAsync('a'));
    }

    [Fact]
    public async Task A_term_turned_down_on_one_PC_is_turned_down_on_the_other_and_never_asked_about_there()
    {
        var deskBook = await _desk.AddBookAsync('a');
        var laptopBook = await _laptop.AddBookAsync('a');
        await _desk.ProposeTypeAsync(deskBook, 'a', "Nonsense");
        await _desk.ProposeTypeAsync(deskBook, 'a', "Gibberish");
        await _laptop.ProposeTypeAsync(laptopBook, 'a', "Nonsense");
        foreach (var pending in await _desk.Vocabulary.GetPendingAsync(Ct)) await _desk.Vocabulary.RejectPendingAsync(pending.Id, Ct);

        await RoundAsync();

        Assert.Empty(await _laptop.Vocabulary.GetPendingAsync(Ct));
        Assert.Empty(await _laptop.TypesAsync('a'));
        // One the laptop's AI hadn't proposed yet is turned down before it is.
        Assert.Equal(TermState.Rejected, (await _laptop.Vocabulary.ProposeTermAsync("type", "Gibberish", Ct)).State);
    }

    [Fact]
    public async Task A_term_filed_under_another_on_one_PC_is_filed_the_same_way_on_the_other()
    {
        await _desk.Vocabulary.SeedAsync(Ct);
        await _laptop.Vocabulary.SeedAsync(Ct);
        var deskBook = await _desk.AddBookAsync('a');
        var laptopBook = await _laptop.AddBookAsync('a');
        await _desk.ProposeTypeAsync(deskBook, 'a', "Scenario Pack");
        await _laptop.ProposeTypeAsync(laptopBook, 'a', "Scenario Pack");
        await RoundAsync();

        _desk.Later(5);
        await _desk.Vocabulary.MapPendingAsync(Assert.Single(await _desk.Vocabulary.GetPendingAsync(Ct)).Id, "adventure", Ct);
        await RoundAsync();

        Assert.Empty(await _laptop.Vocabulary.GetPendingAsync(Ct));
        Assert.Equal("adventure", (await _laptop.Vocabulary.GetAsync(Ct)).Resolve("type", "scenario pack")?.Key);
        Assert.Equal(["adventure"], await _laptop.TypesAsync('a'));
    }

    [Fact]
    public async Task Terms_kept_apart_and_the_users_own_tags_travel_and_so_does_their_removal()
    {
        await _desk.Vocabulary.SeedAsync(Ct);
        await _laptop.Vocabulary.SeedAsync(Ct);
        var apart = await _desk.Vocabulary.KeepApartAsync("type", ["adventure", "campaign"], Ct);
        var tags = new UserTagStore(_desk.Contexts, _desk.Clock);
        var undead = (await tags.SaveAsync(null, "Undead", "zombie, lich", Ct)).Tag!;
        await RoundAsync();

        Assert.Single(await _laptop.Vocabulary.GetKeptApartAsync("type", Ct));
        var onLaptop = Assert.Single(await new UserTagStore(_laptop.Contexts).ListAsync(Ct));
        Assert.Equal("Undead", onLaptop.Name);
        Assert.Equal(["zombie", "lich"], onLaptop.Words);

        _desk.Later(5);
        await _desk.Vocabulary.ForgetKeptApartAsync(apart, Ct);
        await tags.DeleteAsync(undead.Id, Ct);
        await RoundAsync();

        Assert.Empty(await _laptop.Vocabulary.GetKeptApartAsync("type", Ct));
        Assert.Empty(await new UserTagStore(_laptop.Contexts).ListAsync(Ct));
    }

    static string[] Images(string folder, int count) => [.. Enumerable.Range(1, count).Select(i => $"{folder}/Token {i}.png")];

    [Fact]
    public async Task Files_packed_together_on_one_PC_are_one_pack_on_the_other_and_splitting_it_there_splits_it_here()
    {
        var files = await _desk.AddFilesAsync("Handouts/Letter.png", "Handouts/Map.png", "Handouts/Seal.png");
        await _laptop.AddFilesAsync("Handouts/Letter.png", "Handouts/Map.png", "Handouts/Seal.png");
        var together = (await _desk.Packs.PackTogetherAsync([.. files.Values], Ct))!;
        await _desk.Metadata.SetValuesAsync(together.PackId, MetadataFields.Title, ["Party handouts"], Ct);

        await RoundAsync();
        await RoundAsync();

        var pack = Assert.Single(await _laptop.PackCardsAsync());
        Assert.Equal(3, pack.Members!.Count);
        Assert.Equal("Party handouts", (await _laptop.Metadata.GetAsync(pack.EntryId, Ct)).Compute()[MetadataFields.Title].First?.Value);

        _desk.Later(5);
        await _desk.Packs.SplitAsync(together.PackId, Ct);
        await RoundAsync();

        Assert.Empty(await _laptop.PackCardsAsync());
        Assert.Empty(await _desk.PackCardsAsync());
    }

    [Fact]
    public async Task A_folder_both_PCs_packed_by_themselves_ends_as_the_same_pack_and_stays_settled()
    {
        await _desk.AddFilesAsync(Images("Tokens/Undead", 20));
        await _laptop.AddFilesAsync(Images("Tokens/Undead", 20));
        await _desk.Packs.PlanAsync(Ct);
        await _laptop.Packs.PlanAsync(Ct);

        await RoundAsync();
        await RoundAsync();

        var desk = Assert.Single(await _desk.PackCardsAsync());
        var laptop = Assert.Single(await _laptop.PackCardsAsync());
        Assert.Equal(20, laptop.Members!.Count);
        await using (var deskDb = _desk.Catalog.CreateContext())
        await using (var laptopDb = _laptop.Catalog.CreateContext())
            Assert.Equal((await deskDb.Entries.SingleAsync(e => e.Id == desk.EntryId.Value, Ct)).SyncId,
                (await laptopDb.Entries.SingleAsync(e => e.Id == laptop.EntryId.Value, Ct)).SyncId);

        _desk.Later(5);
        _laptop.Later(5);
        Assert.False((await _desk.Sync.SyncNowAsync(Ct)).Changed);
        Assert.False((await _laptop.Sync.SyncNowAsync(Ct)).Changed);
    }

    [Fact]
    public async Task A_folder_proposed_on_both_PCs_and_answered_on_one_is_answered_on_the_other()
    {
        await _desk.AddFilesAsync([.. Images("Maps/Harbor", 6), .. Images("Maps/Docks", 6)]);
        await _laptop.AddFilesAsync([.. Images("Maps/Harbor", 6), .. Images("Maps/Docks", 6)]);
        await _desk.Packs.PlanAsync(Ct);
        await _laptop.Packs.PlanAsync(Ct);
        Assert.Equal(2, (await _laptop.Packs.GetProposalsAsync(Ct)).Count);

        var proposals = await _desk.Packs.GetProposalsAsync(Ct);
        await _desk.Packs.AnswerAsync(proposals.Single(p => p.Name == "Harbor").PackId, PackAnswer.Packed, PackAnswer.Proposed, Ct);
        await _desk.Packs.AnswerAsync(proposals.Single(p => p.Name == "Docks").PackId, PackAnswer.Split, PackAnswer.Proposed, Ct);
        await RoundAsync();

        Assert.Empty(await _laptop.Packs.GetProposalsAsync(Ct));
        var pack = Assert.Single(await _laptop.PackCardsAsync());
        Assert.Equal(6, pack.Members!.Count);
    }

    [Fact]
    public async Task Two_files_joined_as_one_book_on_one_PC_are_joined_on_the_other_until_Not_the_same_book_parts_them()
    {
        var deskAbbey = await _desk.AddBookAsync('a');
        await _desk.AddBookAsync('b');
        await _laptop.AddBookAsync('a');
        await _laptop.AddBookAsync('b');
        await using (var db = _desk.Catalog.CreateContext())
        {
            var opens = await db.EntrySources.Where(s => s.EntryId == deskAbbey.Value).Select(s => s.DocumentId).SingleAsync(Ct);
            await new EntryStore(_desk.Contexts).JoinAsOneBookAsync([deskAbbey, await _desk.BookAsync('b')], opens, Ct);
        }

        await RoundAsync();

        Assert.Equal(await _laptop.BookAsync('a'), await _laptop.BookAsync('b'));

        _laptop.Later(5);
        var book = await _laptop.BookAsync('a');
        await using (var db = _laptop.Catalog.CreateContext())
        {
            var leaving = await db.EntrySources.Where(s => s.EntryId == book.Value && s.Document.ContentHash == new string('b', 64)).Select(s => s.DocumentId).SingleAsync(Ct);
            await new EntryStore(_laptop.Contexts).SplitCopyAsync(book, leaving, ct: Ct);
        }
        await RoundAsync();
        await RoundAsync();

        Assert.NotEqual(await _desk.BookAsync('a'), await _desk.BookAsync('b'));
        Assert.NotEqual(await _laptop.BookAsync('a'), await _laptop.BookAsync('b'));
    }

    [Fact]
    public async Task An_adventure_marked_on_one_PC_is_marked_on_the_other_and_removing_it_removes_it_there()
    {
        var deskBook = await _desk.AddBookAsync('a');
        await _laptop.AddBookAsync('a');
        long document;
        await using (var db = _desk.Catalog.CreateContext())
            document = await db.EntrySources.Where(s => s.EntryId == deskBook.Value).Select(s => s.DocumentId).SingleAsync(Ct);
        var parts = new PartStore(_desk.Contexts, _desk.Clock);
        var part = (await parts.AddAsync(deskBook, new PartSource(document, new PartPages(4, 19)), "The Sunken Bell", Ct))!.Value;
        await _desk.Metadata.SetValuesAsync(part, MetadataFields.Levels, ["3-5"], Ct);

        await RoundAsync();

        var onLaptop = Assert.Single(await new PartStore(_laptop.Contexts).GetPartsAsync(await _laptop.BookAsync('a'), Ct));
        Assert.Equal(new PartPages(4, 19), onLaptop.Sources.Single().Pages);
        var values = (await _laptop.Metadata.GetAsync(onLaptop.EntryId, Ct)).Compute();
        Assert.Equal("The Sunken Bell", values[MetadataFields.Title].First?.Value);
        Assert.Equal("3-5", values[MetadataFields.Levels].First?.Value);

        _desk.Later(5);
        await parts.RemoveAsync(part, Ct);
        await RoundAsync();

        Assert.Empty(await new PartStore(_laptop.Contexts).GetPartsAsync(await _laptop.BookAsync('a'), Ct));
    }

    [Fact]
    public async Task Not_split_for_a_files_adventures_is_answered_on_the_other_PC_too()
    {
        var deskBook = await _desk.AddBookAsync('a');
        await _laptop.AddBookAsync('a');
        await new PartProposalStore(_desk.Contexts).AnswerAsync(deskBook, PartProposalAnswer.NotSplit, Ct);

        await RoundAsync();

        Assert.True(await new PartProposalStore(_laptop.Contexts).IsAnsweredAsync(new string('a', 64), Ct));
    }
}
