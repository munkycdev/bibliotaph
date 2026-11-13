using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Maps, handouts and packs that go with a book: links, refusals, what the rules found, undo, sync, and the values they take.</summary>
public sealed class CompanionStoreTests : IAsyncLifetime
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-companions-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero));
    CatalogDatabase _database = null!;
    CompanionStore _store = null!;
    MetadataStore _metadata = null!;
    EntryId _book;
    EntryId _other;
    EntryId _map;
    EntryId _sheet;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateFromTemplateAsync();
        var contexts = new Factory(_database);
        _store = new CompanionStore(contexts, _clock);
        _metadata = new MetadataStore(contexts, _clock);
        _book = await TestEntries.AddAsync(_database, 'a');
        _other = await TestEntries.AddAsync(_database, 'b');
        _map = await TestEntries.AddAsync(_database, 'c');
        _sheet = await TestEntries.AddAsync(_database, 'd');
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static FoundSourceBook Found(EntryId companion, EntryId book, bool strong) =>
        new(companion, book, strong, new SourceBookEvidence(strong ? SourceBookSignal.SameFolder : SourceBookSignal.Name, "Harbor", strong ? 0.9 : 0.6));

    [Fact]
    public async Task Goes_with_links_a_card_confirmed_and_refuses_what_cant_go_with_a_book()
    {
        var change = await _store.LinkAsync([_map, _sheet, _book], _book, Ct);

        Assert.Equal([_map, _sheet], change.Changed);
        Assert.Equal([_book], change.Refused);
        var link = await _store.GetLiveAsync(_map, Ct);
        Assert.Equal((_book, CompanionState.Confirmed), (link!.Book, link.State));
        Assert.Equal([_map, _sheet], (await _store.GetCompanionsAsync(_book, Ct)).Select(c => c.Companion));
        // A book others go with can't go with a book itself, and a companion can't be the book.
        Assert.Equal([_book], (await _store.LinkAsync([_book], _other, Ct)).Refused);
        Assert.Equal([_other], (await _store.LinkAsync([_other], _map, Ct)).Refused);

        // Choosing another book moves the link.
        await _store.LinkAsync([_map], _other, Ct);
        Assert.Equal(_other, (await _store.GetLiveAsync(_map, Ct))!.Book);
        Assert.Single(await _store.GetAllAsync(_map, Ct));
    }

    [Fact]
    public async Task Not_from_this_book_is_remembered_and_the_rules_never_propose_it_again()
    {
        await _store.ReplaceFoundAsync([_map], [Found(_map, _book, strong: true)], Ct);
        Assert.Equal(CompanionState.Suggested, (await _store.GetLiveAsync(_map, Ct))!.State);

        await _store.UnlinkAsync([_map], refuse: true, Ct);
        Assert.Null(await _store.GetLiveAsync(_map, Ct));

        Assert.Empty(await _store.ReplaceFoundAsync([_map], [Found(_map, _book, strong: true)], Ct));
        Assert.Equal(CompanionState.Rejected, Assert.Single(await _store.GetAllAsync(_map, Ct)).State);
    }

    [Fact]
    public async Task What_the_rules_find_replaces_what_they_found_before_but_not_the_users_own_link()
    {
        // One strong signal suggests; two candidates are asked about.
        Assert.Equal([_map], await _store.ReplaceFoundAsync([_map, _sheet], [Found(_map, _book, true), Found(_sheet, _book, false), Found(_sheet, _other, false)], Ct));
        Assert.Equal([_sheet, _sheet], (await _store.GetProposedAsync(Ct)).Select(p => p.Companion));

        // A suggestion that stays keeps its age; the proposals that are no longer found go.
        var suggested = (await _store.GetLiveAsync(_map, Ct))!.DecidedUtc;
        _clock.Step();
        Assert.Empty(await _store.ReplaceFoundAsync([_map, _sheet], [Found(_map, _book, true)], Ct));
        Assert.Equal(suggested, (await _store.GetLiveAsync(_map, Ct))!.DecidedUtc);
        Assert.Empty(await _store.GetProposedAsync(Ct));

        // Keep makes it the user's, which a later pass leaves alone even when it finds nothing.
        await _store.ConfirmAsync([_map], Ct);
        Assert.Empty(await _store.ReplaceFoundAsync([_map], [], Ct));
        Assert.Equal(CompanionState.Confirmed, (await _store.GetLiveAsync(_map, Ct))!.State);

        // Nothing found any more takes the suggestion away.
        await _store.ReplaceFoundAsync([_sheet], [Found(_sheet, _book, true)], Ct);
        Assert.Equal([_sheet], await _store.ReplaceFoundAsync([_sheet], [], Ct));
        Assert.Null(await _store.GetLiveAsync(_sheet, Ct));
    }

    [Fact]
    public async Task Answering_a_proposal_links_the_chosen_book_or_refuses_every_candidate()
    {
        await _store.ReplaceFoundAsync([_map, _sheet], [Found(_map, _book, false), Found(_map, _other, false), Found(_sheet, _book, false)], Ct);

        await _store.AnswerAsync(_map, _other, Ct);
        await _store.AnswerAsync(_sheet, null, Ct);

        Assert.Equal((_other, CompanionState.Confirmed), ((await _store.GetLiveAsync(_map, Ct))!.Book, (await _store.GetLiveAsync(_map, Ct))!.State));
        Assert.Equal([CompanionState.Rejected], (await _store.GetAllAsync(_sheet, Ct)).Select(l => l.State));
        Assert.Empty(await _store.GetProposedAsync(Ct));
    }

    [Fact]
    public async Task Undo_puts_every_row_back_as_it_was()
    {
        await _store.ReplaceFoundAsync([_map], [Found(_map, _book, false), Found(_map, _other, false)], Ct);
        var before = await _store.GetAllAsync(_map, Ct);

        var change = await _store.LinkAsync([_map], _other, Ct);
        Assert.Equal([_map], await _store.RestoreAsync(change.Undo, Ct));

        Assert.Equal(before.Select(b => (b.Book, b.State, b.Evidence)).OrderBy(b => b.Book.Value),
            (await _store.GetAllAsync(_map, Ct)).Select(b => (b.Book, b.State, b.Evidence)).OrderBy(b => b.Book.Value));
    }

    [Fact]
    public async Task Another_device_sets_the_users_link_and_refusals()
    {
        await _store.ReplaceFoundAsync([_map], [Found(_map, _book, true)], Ct);

        Assert.True(await _store.ApplySyncedAsync(_map, _other, [_book], Ct));
        Assert.False(await _store.ApplySyncedAsync(_map, _other, [_book], Ct));

        var rows = await _store.GetAllAsync(_map, Ct);
        Assert.Equal([(_book, CompanionState.Rejected), (_other, CompanionState.Confirmed)], rows.Select(r => (r.Book, r.State)).OrderBy(r => r.Book.Value));

        // No book there and no refusals: the user's link and the refusal go.
        Assert.True(await _store.ApplySyncedAsync(_map, null, [], Ct));
        Assert.DoesNotContain(await _store.GetAllAsync(_map, Ct), r => r.State is CompanionState.Confirmed or CompanionState.Rejected);
    }

    [Fact]
    public async Task A_companion_takes_its_books_values_as_suggestions_and_keeps_its_own_confirmed_ones()
    {
        await _metadata.SetValuesAsync(_book, MetadataFields.Title, ["Harbor of Shadows"], Ct);
        await _metadata.SetValuesAsync(_book, MetadataFields.System, ["pathfinder"], Ct);
        await _metadata.SetValuesAsync(_book, MetadataFields.Publisher, ["Paizo"], Ct);
        await _metadata.SetValuesAsync(_book, MetadataFields.Tags, ["undead"], Ct);
        await _metadata.SetValuesAsync(_map, MetadataFields.Title, ["Lower docks"], Ct);
        await _metadata.SetValuesAsync(_map, MetadataFields.Publisher, ["Third party"], Ct);
        await _metadata.SetValuesAsync(_map, MetadataFields.Tags, ["docks"], Ct);

        await _store.ReplaceFoundAsync([_map], [Found(_map, _book, true)], Ct);
        var map = (await _metadata.GetAsync(_map, Ct)).Compute();

        Assert.Equal("Lower docks", map[MetadataFields.Title].First!.Value);
        var system = map[MetadataFields.System].First!;
        Assert.Equal(("pathfinder", AssertionOrigin.SourceBook, false, "Harbor of Shadows"), (system.Value, system.Origin, system.Confirmed, system.Source.Quote));
        Assert.Equal("Third party", map[MetadataFields.Publisher].First!.Value);
        Assert.Equal(["docks", "undead"], map[MetadataFields.Tags].Values.Select(v => v.Value).Order());

        // Once the user keeps the link, the book's confirmed values are confirmed on the map too.
        await _store.ConfirmAsync([_map], Ct);
        Assert.True((await _metadata.GetAsync(_map, Ct)).Compute()[MetadataFields.System].First!.Confirmed);
    }
}
