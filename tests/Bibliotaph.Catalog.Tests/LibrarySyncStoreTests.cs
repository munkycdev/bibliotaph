using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Library sync's side of catalog.db: the user's work read as sync records, and merged records applied back.</summary>
public sealed class LibrarySyncStoreTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-sync-store-").FullName;
    CatalogDatabase _first = null!;
    CatalogDatabase _second = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _first = new CatalogDatabase(Path.Combine(_dir, "first.db"), Path.Combine(_dir, "backups"));
        _second = new CatalogDatabase(Path.Combine(_dir, "second.db"), Path.Combine(_dir, "backups"));
        await _first.MigrateFromTemplateAsync(Ct);
        await _second.MigrateFromTemplateAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        _first.ClearPool();
        _second.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static LibrarySyncStore Store(CatalogDatabase database) =>
        new(new Factory(database), new MetadataStore(new Factory(database)), new VocabularyStore(new Factory(database)));

    static string Hash(char c) => new(c, 64);

    [Fact]
    public async Task A_book_is_named_by_the_lowest_hash_of_its_copies_and_answers_to_the_others()
    {
        var book = await TestEntries.AddAsync(_first, 'c');
        await using (var db = _first.CreateContext())
        {
            var copy = db.Documents.Add(new Document { ContentHash = Hash('a'), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity;
            await db.SaveChangesAsync(Ct);
            db.EntrySources.Add(new EntrySource { EntryId = book.Value, DocumentId = copy.Id });
            await db.SaveChangesAsync(Ct);
        }
        await new FavoriteStore(new Factory(_first)).SetAsync([book], true, Ct);

        var snapshot = await Store(_first).ReadAsync(Ct);

        var key = SyncKeys.ForBook(Hash('a'));
        Assert.Equal(SyncJson.Value(true), snapshot.Records[key][SyncFields.Favorite]);
        Assert.Equal([SyncKeys.ForBook(Hash('c'))], snapshot.Aliases[key]);
        Assert.Equal(book.Value, snapshot.FindCard(SyncKeys.ForBook(Hash('c'))));
        Assert.Equal(key, snapshot.LocalKey(SyncKeys.ForBook(Hash('c'))));
    }

    [Fact]
    public async Task Everything_the_user_did_on_one_PC_applies_on_another_and_reads_back_the_same()
    {
        var first = await FillAsync(_first);
        var otherBook = await TestEntries.AddAsync(_second, 'b');
        await TestEntries.AddAsync(_second, 'a');
        // The second PC had a suggestion with the same text, which is confirmed rather than doubled.
        await new MetadataStore(new Factory(_second)).AddSuggestionsAsync(otherBook, Hash('b'),
            [new MetadataProposal(MetadataFields.Types, "adventure", AssertionOrigin.Ai)], Ct);
        var before = await Store(_second).ReadAsync(Ct);

        var changes = first.Records.Select(r => new SyncChange(r.Key, r.Value, before.Records.GetValueOrDefault(r.Key))).ToList();
        var result = await Store(_second).ApplyAsync(changes, before.FindCard, Ct);

        Assert.Empty(result.NotApplied);
        Assert.True(result.Changed);
        var after = await Store(_second).ReadAsync(Ct);
        foreach (var (key, fields) in first.Records)
        {
            Assert.True(after.Records.TryGetValue(key, out var back), $"{key} wasn't applied.");
            Assert.Equal(fields.OrderBy(f => f.Key), back.OrderBy(f => f.Key));
        }
        await using var db = _second.CreateContext();
        Assert.Equal(1, await db.Assertions.CountAsync(a => a.EntryId == otherBook.Value && a.Field == "type", Ct));
        Assert.Equal(AssertionState.Confirmed, (await db.Assertions.SingleAsync(a => a.EntryId == otherBook.Value && a.Field == "type", Ct)).State);
    }

    /// <summary>Two books, metadata decisions, marks, notes, nested collections, a saved search, a binder with pages and an online page, pins, a term and a "not the same book".</summary>
    static async Task<SyncSnapshot> FillAsync(CatalogDatabase database)
    {
        var contexts = new Factory(database);
        var abbey = await TestEntries.AddAsync(database, 'a');
        var bridge = await TestEntries.AddAsync(database, 'b');
        var metadata = new MetadataStore(contexts);
        await metadata.SetValuesAsync(abbey, MetadataFields.Title, ["The Drowned Abbey"], Ct);
        await metadata.SetValuesAsync(abbey, MetadataFields.Tags, ["Friday game", "Spooky"], Ct);
        await metadata.SetValuesAsync(bridge, MetadataFields.Types, ["adventure"], Ct);
        await metadata.RejectAsync(bridge, MetadataFields.System, "pathfinder", Ct);
        await new FavoriteStore(contexts).SetAsync([abbey], true, Ct);
        var notes = new NoteStore(contexts);
        await notes.SetEntryNoteAsync(abbey, "Run the bell tower first.", Ct);
        var abbeyDocument = await new EntryStore(contexts).GetCurrentDocumentAsync(abbey, Ct);
        await notes.AddPageNoteAsync(abbey, new PageRange(abbeyDocument!.Value, 3, 4, "1", "2", "fp-3", "fp-4"), "Read aloud", Ct);
        var collections = new CollectionStore(contexts);
        var campaign = await collections.CreateAsync("Spring campaign", description: "Tuesdays", ct: Ct);
        var maps = await collections.CreateAsync("Maps", campaign.Id, ct: Ct);
        await collections.AddAsync(maps.Id, [abbey, bridge], Ct);
        var view = await new SmartViewStore(contexts).CreateAsync("Haunted places", """{"version":1,"text":"ghost"}""", Ct);
        var sessions = new SessionStore(contexts);
        var binder = await sessions.CreateAsync("Session 4", new DateOnly(2026, 10, 14), keepAtHand: true, gameSystem: "dnd-5e", Ct);
        await sessions.SetNotesAsync(binder.Id, "Bring the dice", Ct);
        var section = await sessions.AddSectionAsync(binder.Id, "Maps", Ct);
        await sessions.AddItemsAsync(binder.Id, [new NewSessionItem(abbey, new PageRange(abbeyDocument.Value, 7, 7, "5", "5"), "Bell tower", "Start here")], section.Id, Ct);
        await sessions.AddItemsAsync(binder.Id, [new NewSessionItem(bridge)], ct: Ct);
        // A page of a book owned online, added with Add this page.
        await sessions.AddItemsAsync(binder.Id, [new NewSessionItem(bridge, Label: "Goblin lair", Link: "https://example.com/sources/bridge/goblins#lair")], ct: Ct);
        var pins = new PinStore(contexts);
        await pins.PinAsync(PinTarget.Collection(campaign.Id), Ct);
        await pins.PinAsync(PinTarget.Entry(abbey), Ct);
        await pins.PinAsync(PinTarget.Binder(binder.Id), Ct);
        await new VocabularyStore(contexts).AddTermAsync("system", "Mothership", Ct);
        await new EntryStore(contexts).RememberNotSameBookAsync(Hash('a'), Hash('b'), Ct);
        var snapshot = await Store(database).ReadAsync(Ct);
        await using var db = database.CreateContext();
        Assert.Contains(SyncKeys.For(SyncKeys.SavedSearch, (await db.SmartViews.SingleAsync(v => v.Id == view.Id, Ct)).SyncId), snapshot.Records.Keys);
        Assert.Contains(snapshot.Records.Values, r => r.GetValueOrDefault(SyncFields.Link) is { } link && link.Contains("goblins#lair", StringComparison.Ordinal));
        return snapshot;
    }

    [Fact]
    public async Task What_another_PC_deleted_goes_here_too_and_sub_collections_move_up()
    {
        var first = await FillAsync(_first);
        await TestEntries.AddAsync(_second, 'a');
        await TestEntries.AddAsync(_second, 'b');
        var before = await Store(_second).ReadAsync(Ct);
        await Store(_second).ApplyAsync([.. first.Records.Select(r => new SyncChange(r.Key, r.Value, before.Records.GetValueOrDefault(r.Key)))], before.FindCard, Ct);
        var applied = await Store(_second).ReadAsync(Ct);
        var campaign = applied.Records.Single(r => r.Key.StartsWith(SyncKeys.Collection, StringComparison.Ordinal) && !r.Value.ContainsKey(SyncFields.Parent)).Key;
        var binder = applied.Records.Single(r => r.Key.StartsWith(SyncKeys.Binder, StringComparison.Ordinal)).Key;
        var note = applied.Records.Single(r => r.Key.StartsWith(SyncKeys.PageNote, StringComparison.Ordinal)).Key;

        var result = await Store(_second).ApplyAsync(
            [new SyncChange(campaign, null, applied.Records[campaign]), new SyncChange(binder, null, applied.Records[binder]), new SyncChange(note, null, applied.Records[note])],
            applied.FindCard, Ct);

        Assert.True(result.Changed);
        var after = await Store(_second).ReadAsync(Ct);
        Assert.DoesNotContain(campaign, after.Records.Keys);
        Assert.DoesNotContain(binder, after.Records.Keys);
        Assert.DoesNotContain(note, after.Records.Keys);
        Assert.DoesNotContain(after.Records, r => r.Key.StartsWith(SyncKeys.Item, StringComparison.Ordinal));
        var maps = Assert.Single(after.Records, r => r.Key.StartsWith(SyncKeys.Collection, StringComparison.Ordinal));
        Assert.False(maps.Value.ContainsKey(SyncFields.Parent));
        await using var db = _second.CreateContext();
        // The binder's and the note's pages went with them; nothing points at a missing row.
        Assert.Equal(0, await db.PageRefs.CountAsync(Ct));
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS \"Value\" FROM pragma_foreign_key_check").SingleAsync(Ct));
    }

    [Fact]
    public async Task A_record_whose_book_or_file_is_not_here_waits()
    {
        var first = await FillAsync(_first);
        await TestEntries.AddAsync(_second, 'b');
        var before = await Store(_second).ReadAsync(Ct);

        var result = await Store(_second).ApplyAsync([.. first.Records.Select(r => new SyncChange(r.Key, r.Value, before.Records.GetValueOrDefault(r.Key)))],
            before.FindCard, Ct);

        // The abbey isn't on this PC: its card, its page note and its binder item wait; the rest applies.
        Assert.Contains(SyncKeys.ForBook(Hash('a')), result.NotApplied);
        Assert.Contains(result.NotApplied, k => k.StartsWith(SyncKeys.PageNote, StringComparison.Ordinal));
        Assert.Single(result.NotApplied, k => k.StartsWith(SyncKeys.Item, StringComparison.Ordinal));
        Assert.DoesNotContain(SyncKeys.ForBook(Hash('b')), result.NotApplied);
        var after = await Store(_second).ReadAsync(Ct);
        // The bridge's two items, the whole book and its page online, are here.
        Assert.Equal(2, after.Records.Count(r => r.Key.StartsWith(SyncKeys.Item, StringComparison.Ordinal)));
    }

    [Fact]
    public void Writes_to_the_users_work_are_noticed_and_reads_and_machine_tables_are_not()
    {
        Assert.True(CatalogWrites.WritesUserWork("INSERT INTO \"favorite\" (\"entry_id\", \"created_utc\") VALUES (@p0, @p1);"));
        Assert.True(CatalogWrites.WritesUserWork("UPDATE \"pin\" AS \"p\" SET \"position\" = \"p\".\"position\" + 1 WHERE \"p\".\"position\" >= @at"));
        Assert.True(CatalogWrites.WritesUserWork("DELETE FROM \"collection_item\" AS \"c\" WHERE \"c\".\"collection_id\" = @id"));
        Assert.True(CatalogWrites.WritesUserWork("UPDATE \"file_location\" SET \"last_seen_utc\" = @p0;\nINSERT INTO \"note\" (\"text\") VALUES (@p1) RETURNING \"id\";"));
        Assert.False(CatalogWrites.WritesUserWork("SELECT \"f\".\"entry_id\" FROM \"favorite\" AS \"f\""));
        Assert.False(CatalogWrites.WritesUserWork("UPDATE \"file_location\" SET \"last_seen_utc\" = @p0"));
        Assert.False(CatalogWrites.WritesUserWork("INSERT INTO \"sync_state\" (\"key\") VALUES (@p0)"));
    }

    [Fact]
    public async Task A_change_through_any_store_is_noticed_but_not_one_made_quietly()
    {
        var book = await TestEntries.AddAsync(_first, 'a');
        var noticed = 0;
        _first.Writes.Written += (_, _) => Interlocked.Increment(ref noticed);

        await new FavoriteStore(new Factory(_first)).SetAsync([book], true, Ct);
        Assert.True(noticed > 0);

        noticed = 0;
        using (CatalogWrites.Quiet()) await new FavoriteStore(new Factory(_first)).SetAsync([book], false, Ct);
        await new SettingsStore(new Factory(_first)).SetAsync("start-page", "Library", Ct);
        Assert.Equal(0, noticed);
    }
}
