using Bibliotaph.Core;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Slice 3f: a note on each book, notes on its pages, and what happens to them when cards join and split.</summary>
public sealed class NoteStoreTests : IAsyncLifetime
{
    static readonly DateTimeOffset Monday = new(2026, 10, 5, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-notes-").FullName;
    readonly MetadataStoreTests.SteppingClock _clock = new(Monday);
    readonly List<ScannedFile> _files = [];
    CatalogDatabase _database = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    NoteStore _notes = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        var contexts = new Factory(_database);
        _library = new LibraryStore(contexts);
        _entries = new EntryStore(contexts);
        _notes = new NoteStore(contexts, _clock);
        _root = (await new SourceRootStore(contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
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

    [Fact]
    public async Task A_book_has_one_note_saved_as_typed_and_blank_deletes_it()
    {
        var (_, abbey) = await AddBookAsync("abbey.pdf", 'a');

        Assert.True(await _notes.SetEntryNoteAsync(abbey, "  Run the bell tower first.\n", Ct));
        Assert.False(await _notes.SetEntryNoteAsync(abbey, "Run the bell tower first.", Ct));
        Assert.True(await _notes.SetEntryNoteAsync(abbey, "Run the bell tower last.", Ct));
        Assert.Equal("Run the bell tower last.", await _notes.GetEntryNoteAsync(abbey, Ct));
        Assert.Equal(new Dictionary<EntryId, string> { [abbey] = "Run the bell tower last." }, await _notes.GetEntryNotesAsync(ct: Ct));

        Assert.True(await _notes.SetEntryNoteAsync(abbey, "   ", Ct));
        Assert.Null(await _notes.GetEntryNoteAsync(abbey, Ct));
        Assert.False(await _notes.SetEntryNoteAsync(new EntryId(9999), "Nobody's book", Ct));
    }

    [Fact]
    public async Task Page_notes_list_in_page_order_and_come_back_after_delete()
    {
        var (document, abbey) = await AddBookAsync("abbey.pdf", 'a');
        await _notes.SetEntryNoteAsync(abbey, "The book's own note", Ct);

        var late = await _notes.AddPageNoteAsync(abbey, new PageRange(document, 12, 14, "10", "12"), "Warehouse ambush starts here", Ct);
        var early = await _notes.AddPageNoteAsync(abbey, new PageRange(document, 3, 3, "1", "1"), "Read aloud", Ct);
        Assert.Null(await _notes.AddPageNoteAsync(abbey, new PageRange(document, 4, 4), " ", Ct));

        Assert.Equal(["Read aloud", "Warehouse ambush starts here"], (await _notes.GetPageNotesAsync(abbey, Ct)).Select(n => n.Text));
        Assert.Equal(new PageRange(document, 12, 14, "10", "12"), late!.Range with { RefId = 0 });

        var moved = await _notes.UpdatePageNoteAsync(early!.Id, "Read aloud, then roll", new PageRange(document, 20, 20, "18", "18"), Ct);
        Assert.Equal((20, "Read aloud, then roll"), (moved!.Range.FirstPdfPage, moved.Text));
        Assert.Equal(["Warehouse ambush starts here", "Read aloud, then roll"], (await _notes.GetPageNotesAsync(abbey, Ct)).Select(n => n.Text));

        var deleted = await _notes.DeletePageNoteAsync(late.Id, Ct);
        Assert.Null(await _notes.DeletePageNoteAsync(late.Id, Ct));
        Assert.Single(await _notes.GetPageNotesAsync(abbey, Ct));
        var back = await _notes.RestorePageNoteAsync(deleted!, Ct);
        Assert.Equal(deleted! with { Id = back!.Id, Range = deleted.Range with { RefId = back.Range.RefId } }, back);
        Assert.Equal(2, (await _notes.GetPageNotesAsync(abbey, Ct)).Count);
        // The book's own note is never a page note.
        Assert.Equal("The book's own note", await _notes.GetEntryNoteAsync(abbey, Ct));
    }

    [Fact]
    public async Task A_joined_copy_brings_its_notes_and_a_split_takes_them_back()
    {
        var (coreDoc, core) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backupDoc, backup) = await AddBookAsync("Backup/abbey.pdf", 'b');
        await _notes.SetEntryNoteAsync(backup, "Notes from the backup", Ct);
        await _notes.AddPageNoteAsync(backup, new PageRange(backupDoc, 2, 2), "Map key", Ct);

        await _entries.JoinAsCopyAsync(coreDoc, backupDoc, Ct);

        Assert.Equal("Notes from the backup", await _notes.GetEntryNoteAsync(core, Ct));
        Assert.Equal(["Map key"], (await _notes.GetPageNotesAsync(core, Ct)).Select(n => n.Text));

        Assert.Equal(backup, await _entries.SplitCopyAsync(core, backupDoc, ct: Ct));
        Assert.Equal("Notes from the backup", await _notes.GetEntryNoteAsync(backup, Ct));
        Assert.Null(await _notes.GetEntryNoteAsync(core, Ct));
        Assert.Equal(["Map key"], (await _notes.GetPageNotesAsync(backup, Ct)).Select(n => n.Text));
        Assert.Empty(await _notes.GetPageNotesAsync(core, Ct));
    }

    [Fact]
    public async Task A_note_joining_a_card_with_its_own_is_added_at_the_end_and_taken_off_on_split()
    {
        var (coreDoc, core) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backupDoc, backup) = await AddBookAsync("Backup/abbey.pdf", 'b');
        await _notes.SetEntryNoteAsync(core, "Core note", Ct);
        await _notes.SetEntryNoteAsync(backup, "Backup note", Ct);

        await _entries.JoinAsCopyAsync(coreDoc, backupDoc, Ct);
        var joined = await _notes.GetEntryNoteAsync(core, Ct);
        Assert.StartsWith("Core note", joined, StringComparison.Ordinal);
        Assert.EndsWith("Backup note", joined, StringComparison.Ordinal);

        await _entries.SplitCopyAsync(core, backupDoc, ct: Ct);
        Assert.Equal("Core note", await _notes.GetEntryNoteAsync(core, Ct));
        Assert.Equal("Backup note", await _notes.GetEntryNoteAsync(backup, Ct));
    }

    [Fact]
    public async Task Removing_a_book_owned_elsewhere_and_undoing_brings_its_note_back()
    {
        var printed = await _entries.AddElsewhereAsync(Ct);
        await _notes.SetEntryNoteAsync(printed, "Borrowed from a friend", Ct);

        var removed = await _entries.RemoveElsewhereAsync(printed, Ct);
        Assert.Equal("Borrowed from a friend", removed!.Note);
        Assert.Empty(await _notes.GetEntryNotesAsync(ct: Ct));

        var restored = await _entries.RestoreElsewhereAsync(removed, Ct);
        Assert.Equal("Borrowed from a friend", await _notes.GetEntryNoteAsync(restored, Ct));
    }
}
