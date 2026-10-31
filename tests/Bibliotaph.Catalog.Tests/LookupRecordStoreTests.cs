using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Sync;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>The catalogues' records for a file's ISBNs, the "Same book?" cards for those whose title isn't printed in it, and their answers.</summary>
public sealed class LookupRecordStoreTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-lookup-records-").FullName;
    CatalogDatabase _first = null!;
    CatalogDatabase _second = null!;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly BookRecord Mill = new("RPGGeek", "4021", "9781234567897", "The Owlbear Mill", ["Ada Quill"], ["Gull Rock Games"], "2019",
        [new CatalogueValue("type", "Scenario / Adventure / Module")]);

    static readonly BookRecord Other = new("Open Library", "/books/OL1M", "9781234567897", "Tides of the Gull Coast", ["B. Example"], ["Example Press"], "2018");

    static string Hash(char c) => new(c, 64);

    public async ValueTask InitializeAsync()
    {
        _first = new CatalogDatabase(Path.Combine(_dir, "first.db"), Path.Combine(_dir, "backups"));
        _second = new CatalogDatabase(Path.Combine(_dir, "second.db"), Path.Combine(_dir, "backups"));
        await _first.MigrateFromTemplateAsync(Ct);
        await _second.MigrateFromTemplateAsync(Ct);
        foreach (var database in new[] { _first, _second }) await new VocabularyStore(new Factory(database)).SeedAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        _first.ClearPool();
        _second.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static LookupRecordStore Store(CatalogDatabase database)
    {
        var contexts = new Factory(database);
        return new LookupRecordStore(contexts, new MetadataStore(contexts), new VocabularyStore(contexts), new SettingsStore(contexts));
    }

    static async Task<List<string>> LookedUpAsync(CatalogDatabase database, EntryId entryId, string field)
    {
        await using var db = database.CreateContext();
        return await db.Assertions.Where(a => a.EntryId == entryId.Value && a.Origin == AssertionOrigin.Lookup && a.Field == field)
            .Select(a => a.NormalizedValue).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_record_whose_title_isnt_printed_waits_for_an_answer_and_fills_the_fields_once_it_is_the_books()
    {
        var book = await TestEntries.AddAsync(_first, 'a');
        var store = Store(_first);

        var saved = await store.SaveAsync(Hash('a'), [Mill], _ => false, Ct);
        await store.ApplyAsync(book, Hash('a'), Ct);

        Assert.Equal([Mill], saved.Waiting);
        Assert.Empty(await LookedUpAsync(_first, book, "title"));
        var card = Assert.Single(await store.GetPendingAsync(Ct));
        Assert.Equal(book, card.EntryId);
        Assert.Equal((Mill.Source, Mill.Key, Mill.Title), (card.Record.Source, card.Record.Key, card.Record.Title));

        var decision = await store.AnswerAsync(card, LookupAnswer.SameBook, Ct);
        await store.ApplyAsync(book, Hash('a'), Ct);

        Assert.NotNull(decision);
        Assert.Empty(await store.GetPendingAsync(Ct));
        Assert.Equal([MetadataText.Normalize("The Owlbear Mill")], await LookedUpAsync(_first, book, "title"));
        Assert.Equal(["adventure"], await LookedUpAsync(_first, book, "type"));
        // Asked again, the catalogue's record is used without a question.
        Assert.Equal([Mill], (await store.SaveAsync(Hash('a'), [Mill], _ => false, Ct)).Used);

        await store.UndoAsync(decision, Ct);
        await store.ApplyAsync(book, Hash('a'), Ct);

        Assert.Empty(await LookedUpAsync(_first, book, "title"));
        Assert.Single(await store.GetPendingAsync(Ct));
    }

    [Fact]
    public async Task Not_this_book_is_remembered_after_the_catalogue_stops_giving_the_record()
    {
        var book = await TestEntries.AddAsync(_first, 'a');
        var store = Store(_first);
        await store.SaveAsync(Hash('a'), [Mill, Other], r => r == Other, Ct);

        var card = Assert.Single(await store.GetPendingAsync(Ct));
        await store.AnswerAsync(card, LookupAnswer.NotThisBook, Ct);
        await store.ApplyAsync(book, Hash('a'), Ct);

        Assert.Equal([MetadataText.Normalize("Tides of the Gull Coast")], await LookedUpAsync(_first, book, "title"));
        await store.SaveAsync(Hash('a'), [Other], r => r == Other, Ct);
        var again = await store.SaveAsync(Hash('a'), [Mill, Other], r => r == Other, Ct);
        Assert.Equal([Mill], again.Refused);
        Assert.Equal([Other], again.Used);
        Assert.Empty(await store.GetPendingAsync(Ct));
    }

    [Fact]
    public async Task A_book_is_found_by_the_catalogues_whose_records_it_uses()
    {
        var used = await TestEntries.AddAsync(_first, 'a');
        var waiting = await TestEntries.AddAsync(_first, 'b');
        var refused = await TestEntries.AddAsync(_first, 'c');
        var store = Store(_first);
        await store.SaveAsync(Hash('a'), [Mill, Other], _ => true, Ct);
        await store.SaveAsync(Hash('b'), [Mill], _ => false, Ct);
        await store.SaveAsync(Hash('c'), [Mill], _ => false, Ct);
        await store.AnswerAsync(Assert.Single(await store.GetPendingAsync(Ct), p => p.EntryId == refused), LookupAnswer.NotThisBook, Ct);

        Assert.Equal([(used, "Open Library"), (used, "RPGGeek")], (await store.GetFoundByAsync(ct: Ct)).OrderBy(f => f.Source));
        Assert.Empty(await store.GetFoundByAsync([waiting, refused], Ct));

        await store.AnswerAsync(Assert.Single(await store.GetPendingAsync(Ct)), LookupAnswer.SameBook, Ct);
        Assert.Equal([(waiting, "RPGGeek")], await store.GetFoundByAsync([waiting], Ct));
    }

    [Fact]
    public async Task A_record_no_lookup_gives_any_more_stops_asking()
    {
        await TestEntries.AddAsync(_first, 'a');
        var store = Store(_first);
        await store.SaveAsync(Hash('a'), [Mill], _ => false, Ct);

        await store.SaveAsync(Hash('a'), [], _ => false, Ct);

        Assert.Empty(await store.GetPendingAsync(Ct));
    }

    [Fact]
    public async Task A_book_with_two_copies_is_asked_about_its_record_once()
    {
        var book = await TestEntries.AddAsync(_first, 'a');
        await using (var db = _first.CreateContext())
        {
            var copy = db.Documents.Add(new Entities.Document { ContentHash = Hash('b'), Format = "pdf", CreatedUtc = DateTime.UtcNow }).Entity;
            await db.SaveChangesAsync(Ct);
            db.EntrySources.Add(new Entities.EntrySource { EntryId = book.Value, DocumentId = copy.Id });
            await db.SaveChangesAsync(Ct);
        }
        var store = Store(_first);
        await store.SaveAsync(Hash('a'), [Mill], _ => false, Ct);
        await store.SaveAsync(Hash('b'), [Mill], _ => false, Ct);

        var card = Assert.Single(await store.GetPendingAsync(Ct));
        Assert.Equal([Hash('a'), Hash('b')], card.Hashes.Order(StringComparer.Ordinal));
        await store.AnswerAsync(card, LookupAnswer.SameBook, Ct);

        Assert.Equal([Mill], (await store.SaveAsync(Hash('b'), [Mill], _ => false, Ct)).Used);
    }

    [Fact]
    public async Task An_answer_goes_to_the_other_PCs_which_fill_the_fields_at_once()
    {
        await TestEntries.AddAsync(_first, 'a');
        var second = await TestEntries.AddAsync(_second, 'a');
        await Store(_first).SaveAsync(Hash('a'), [Mill], _ => false, Ct);
        await Store(_second).SaveAsync(Hash('a'), [Mill], _ => false, Ct);
        await Store(_first).AnswerAsync(Assert.Single(await Store(_first).GetPendingAsync(Ct)), LookupAnswer.SameBook, Ct);

        var snapshot = await Sync(_first).ReadAsync(Ct);
        var key = SyncKeys.ForLookupRecord(Hash('a'), "RPGGeek", "4021");
        var fields = snapshot.Records[key];
        var applied = await Sync(_second).ApplyAsync([new SyncChange(key, fields, null)], _ => null, Ct);

        Assert.True(applied.Changed);
        Assert.Empty(await Store(_second).GetPendingAsync(Ct));
        Assert.Equal([MetadataText.Normalize("The Owlbear Mill")], await LookedUpAsync(_second, second, "title"));

        // Undone there: the card waits here again, and the record's values go.
        await Sync(_second).ApplyAsync([new SyncChange(key, null, fields)], _ => null, Ct);
        Assert.Single(await Store(_second).GetPendingAsync(Ct));
        Assert.Empty(await LookedUpAsync(_second, second, "title"));
    }

    [Fact]
    public async Task An_answer_from_another_PC_before_this_one_looks_the_file_up_is_kept_for_when_it_does()
    {
        await TestEntries.AddAsync(_second, 'a');
        var key = SyncKeys.ForLookupRecord(Hash('a'), "RPGGeek", "4021");

        await Sync(_second).ApplyAsync([new SyncChange(key, new Dictionary<string, string> { [SyncFields.SameBook] = "false" }, null)], _ => null, Ct);
        var saved = await Store(_second).SaveAsync(Hash('a'), [Mill], _ => false, Ct);

        Assert.Equal([Mill], saved.Refused);
        Assert.Empty(await Store(_second).GetPendingAsync(Ct));
    }

    static LibrarySyncStore Sync(CatalogDatabase database)
    {
        var contexts = new Factory(database);
        return new LibrarySyncStore(contexts, new MetadataStore(contexts), new VocabularyStore(contexts), lookups: Store(database));
    }
}
