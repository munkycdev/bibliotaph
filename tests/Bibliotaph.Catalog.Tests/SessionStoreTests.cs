using Bibliotaph.Core;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Slice 3c: session packs, with sections and ordered items, each a book or a page range of one.</summary>
public sealed class SessionStoreTests : IAsyncLifetime
{
    static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-sessions-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(Monday);
    readonly List<ScannedFile> _files = [];
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    SessionStore _sessions = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _entries = new EntryStore(_contexts);
        _sessions = new SessionStore(_contexts, _clock);
        _root = (await new SourceRootStore(_contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    async Task<(long Document, EntryId Entry)> AddBookAsync(string path, char hash)
    {
        _files.Add(new ScannedFile(path, 1000, Monday.UtcDateTime, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        var document = (await _library.AttachHashAsync(file, ContentHash.Parse(new string(hash, 64)), Ct))!.Value.DocumentId;
        return (document, (await _entries.GetEntryAsync(document, Ct))!.EntryId);
    }

    async Task<List<string>> LabelsAsync(long packId) => [.. (await _sessions.GetAsync(packId, Ct))!.Items.Select(i => i.Label ?? "")];

    [Fact]
    public async Task Items_keep_their_order_labels_and_pages_and_the_same_book_can_come_twice()
    {
        var (abbeyDoc, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var (_, tomb) = await AddBookAsync("tomb.pdf", 'b');
        var pack = await _sessions.CreateAsync("  The midnight bell ", new DateOnly(2026, 10, 17), Ct);
        Assert.Equal("The midnight bell", pack.Title);

        await _sessions.AddItemsAsync(pack.Id, [
            new NewSessionItem(abbey, new PageRange(abbeyDoc, 44, 41, "42", "45", "f1", "f4"), "  Warehouse ambush "),
            new NewSessionItem(tomb),
            new NewSessionItem(abbey, new PageRange(abbeyDoc, 3, 3), "Village map", "Keep it handy"),
        ], ct: Ct);

        var contents = (await _sessions.GetAsync(pack.Id, Ct))!;
        Assert.Equal(["Warehouse ambush", "", "Village map"], contents.Items.Select(i => i.Label ?? ""));
        // A range is stored first page first, whichever way it was given.
        Assert.Equal(new PageRange(abbeyDoc, 41, 44, "42", "45", "f1", "f4"), contents.Items[0].Range);
        Assert.Null(contents.Items[1].Range);
        Assert.Equal("Keep it handy", contents.Items[2].Note);
        Assert.Equal(3, contents.Pack.ItemCount);
        Assert.Equal([abbey, tomb], contents.Pack.FirstEntries);
        Assert.Equal(new DateOnly(2026, 10, 17), contents.Pack.Date);
        Assert.Equal([(abbey, ScopeKeys.Session(pack.Id))], await _sessions.GetScopesAsync([abbey], Ct));
    }

    [Fact]
    public async Task Moving_up_and_down_crosses_section_headings_and_stops_at_the_ends()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var pack = await _sessions.CreateAsync("Session 4", ct: Ct);
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(abbey, Label: "Intro")], ct: Ct);
        var maps = await _sessions.AddSectionAsync(pack.Id, "Maps", Ct);
        // Without a section, new items go to the end of the pack: the last section.
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(abbey, Label: "Harbor"), new NewSessionItem(abbey, Label: "Docks")], ct: Ct);
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(abbey, Label: "Hook")], SessionStore.NoSection, Ct);
        Assert.Equal(["Intro", "Hook", "Harbor", "Docks"], await LabelsAsync(pack.Id));

        var items = (await _sessions.GetAsync(pack.Id, Ct))!.Items;
        Assert.False(await _sessions.MoveItemAsync(items[0].Id, -1, Ct));
        Assert.False(await _sessions.MoveItemAsync(items[3].Id, 1, Ct));

        Assert.True(await _sessions.MoveItemAsync(items[1].Id, 1, Ct)); // Hook goes over the Maps heading
        var moved = (await _sessions.GetAsync(pack.Id, Ct))!.Items;
        Assert.Equal(["Intro", "Hook", "Harbor", "Docks"], moved.Select(i => i.Label));
        Assert.Equal(maps.Id, moved[1].SectionId);

        Assert.True(await _sessions.MoveItemAsync(items[1].Id, 1, Ct));
        Assert.Equal(["Intro", "Harbor", "Hook", "Docks"], await LabelsAsync(pack.Id));

        Assert.True(await _sessions.MoveItemToAsync(items[3].Id, SessionStore.NoSection, 0, Ct)); // a drag to the top
        Assert.Equal(["Docks", "Intro", "Harbor", "Hook"], await LabelsAsync(pack.Id));

        // Deleting the heading keeps its items, after those above it.
        Assert.True(await _sessions.DeleteSectionAsync(maps.Id, Ct));
        var contents = (await _sessions.GetAsync(pack.Id, Ct))!;
        Assert.Empty(contents.Sections);
        Assert.Equal(["Docks", "Intro", "Harbor", "Hook"], contents.Items.Select(i => i.Label));
        Assert.Equal([0, 1, 2, 3], contents.Items.Select(i => i.Position));
    }

    [Fact]
    public async Task Removed_items_come_back_where_they_were_with_their_pages()
    {
        var (doc, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var pack = await _sessions.CreateAsync("Session 4", ct: Ct);
        await _sessions.AddItemsAsync(pack.Id, [
            new NewSessionItem(abbey, Label: "One"),
            new NewSessionItem(abbey, new PageRange(doc, 5, 6, "5", "6"), "Two"),
            new NewSessionItem(abbey, Label: "Three"),
        ], ct: Ct);
        var items = (await _sessions.GetAsync(pack.Id, Ct))!.Items;

        var removed = await _sessions.RemoveItemsAsync([items[1].Id], Ct);
        Assert.Equal(["One", "Three"], await LabelsAsync(pack.Id));

        var restored = await _sessions.RestoreItemsAsync(removed, Ct);
        var contents = (await _sessions.GetAsync(pack.Id, Ct))!;
        Assert.Equal(["One", "Two", "Three"], contents.Items.Select(i => i.Label));
        Assert.Equal([contents.Items[1].Id], restored);
        Assert.Equal(new PageRange(doc, 5, 6, "5", "6"), contents.Items[1].Range);
    }

    [Fact]
    public async Task A_deleted_pack_comes_back_and_a_duplicate_copies_everything_but_the_date()
    {
        var (doc, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var pack = await _sessions.CreateAsync("Session 4", new DateOnly(2026, 10, 17), Ct);
        await _sessions.SetNotesAsync(pack.Id, "Start with the bell.", Ct);
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(abbey, Label: "Intro")], ct: Ct);
        var maps = await _sessions.AddSectionAsync(pack.Id, "Maps", Ct);
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(abbey, new PageRange(doc, 9, 9), "Harbor", "At night")], maps.Id, Ct);

        var copy = (await _sessions.DuplicateAsync(pack.Id, "Session 5", Ct))!;
        var copied = (await _sessions.GetAsync(copy.Id, Ct))!;
        Assert.Null(copied.Pack.Date);
        Assert.Equal("Start with the bell.", copied.Pack.Notes);
        Assert.Equal(["Maps"], copied.Sections.Select(s => s.Name));
        Assert.Equal(["Intro", "Harbor"], copied.Items.Select(i => i.Label));
        Assert.Equal(copied.Sections[0].Id, copied.Items[1].SectionId);
        Assert.Equal(new PageRange(doc, 9, 9), copied.Items[1].Range);
        // The copy's page reference is its own: re-pointing it leaves the original alone.
        await _sessions.RepointAsync(copied.Items[1].Id, new PageRange(doc, 10, 10), Ct);
        Assert.Equal(9, (await _sessions.GetAsync(pack.Id, Ct))!.Items[1].Range!.FirstPdfPage);
        // The pack worked on last is the current one, listed first.
        Assert.Equal([copy.Id, pack.Id], (await _sessions.ListAsync(Ct)).Select(p => p.Id));

        var deleted = (await _sessions.DeleteAsync(pack.Id, Ct))!;
        Assert.Null(await _sessions.GetAsync(pack.Id, Ct));
        var restored = await _sessions.RestoreAsync(deleted, Ct);
        var back = (await _sessions.GetAsync(restored.Id, Ct))!;
        Assert.Equal(new DateOnly(2026, 10, 17), back.Pack.Date);
        Assert.Equal(["Intro", "Harbor"], back.Items.Select(i => i.Label));
        Assert.Equal("At night", back.Items[1].Note);
    }

    [Fact]
    public async Task A_joined_copy_brings_its_items_to_the_card_and_a_split_takes_them_back()
    {
        var (coreDoc, core) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backupDoc, backup) = await AddBookAsync("Backup/abbey.pdf", 'b');
        var pack = await _sessions.CreateAsync("Session 4", ct: Ct);
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(core, Label: "Core"), new NewSessionItem(backup, new PageRange(backupDoc, 2, 2), "Backup")], ct: Ct);

        await _entries.JoinAsCopyAsync(coreDoc, backupDoc, Ct);
        Assert.Equal([core, core], (await _sessions.GetAsync(pack.Id, Ct))!.Items.Select(i => i.EntryId));

        Assert.Equal(backup, await _entries.SplitCopyAsync(core, backupDoc, ct: Ct));
        Assert.Equal([core, backup], (await _sessions.GetAsync(pack.Id, Ct))!.Items.Select(i => i.EntryId));
    }

    [Fact]
    public async Task Removing_a_book_owned_elsewhere_and_undoing_puts_its_items_back()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');
        var printed = await _entries.AddElsewhereAsync(Ct);
        var pack = await _sessions.CreateAsync("Session 4", ct: Ct);
        await _sessions.AddItemsAsync(pack.Id, [new NewSessionItem(abbey, Label: "One"), new NewSessionItem(printed, Label: "Printed"), new NewSessionItem(abbey, Label: "Three")], ct: Ct);

        var removed = (await _entries.RemoveElsewhereAsync(printed, Ct))!;
        Assert.Equal(["One", "Three"], await LabelsAsync(pack.Id));

        var restored = await _entries.RestoreElsewhereAsync(removed, Ct);
        var items = (await _sessions.GetAsync(pack.Id, Ct))!.Items;
        Assert.Equal(["One", "Printed", "Three"], items.Select(i => i.Label));
        Assert.Equal(restored, items[1].EntryId);
    }
}
