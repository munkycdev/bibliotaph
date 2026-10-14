using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Adventures inside a file: entries of kind Part with a page range of each copy of their file.</summary>
public sealed class PartStoreTests : IAsyncLifetime
{
    static readonly DateTime Monday = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-parts-").FullName;
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    MetadataStore _metadata = null!;
    PartStore _parts = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateFromTemplateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _entries = new EntryStore(_contexts);
        _metadata = new MetadataStore(_contexts);
        _parts = new PartStore(_contexts);
        _root = (await new SourceRootStore(_contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        _database.ClearPool();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static string Hex(char c) => new(c, 64);

    readonly List<ScannedFile> _files = [];

    async Task<(long Document, EntryId Entry)> AddBookAsync(string path, char hash)
    {
        _files.Add(new ScannedFile(path, 1000, Monday, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        var document = (await _library.AttachHashAsync(file, ContentHash.Parse(Hex(hash)), Ct))!.Value.DocumentId;
        return (document, (await _entries.GetEntryAsync(document, Ct))!.EntryId);
    }

    [Fact]
    public async Task Marked_pages_become_an_adventure_with_a_card_of_its_own()
    {
        var (document, file) = await AddBookAsync("Compilations/big compilation.pdf", 'a');

        var heist = await _parts.AddAsync(file, new PartSource(document, new PartPages(10, 19), "fp10", "fp19"), " Warehouse heist ", Ct);
        var chase = await _parts.AddAsync(file, new PartSource(document, new PartPages(30, 39)), "Sewer chase", Ct);

        Assert.NotNull(heist);
        Assert.NotNull(chase);
        var cards = (await _entries.GetCurrentAsync(ct: Ct)).ToDictionary(c => c.EntryId);
        Assert.Equal(2, cards[file].Parts);
        Assert.Equal(new EntryDocument(heist.Value, document, EntryKind.Part) { ParentEntryId = file, Pages = new PartPages(10, 19) }, cards[heist.Value]);
        // The file still opens as itself, and its document is still its own card's.
        Assert.Equal(file, await _entries.GetCardAsync(document, Ct));
        Assert.Equal(document, (await _entries.GetEntryAsync(document, Ct))!.DocumentId);
        Assert.Equal([new EntryDocument(file, document, EntryKind.Whole) { Parts = 2 }, cards[heist.Value], cards[chase.Value]],
            (await _entries.GetShownByAsync(document, Ct)).OrderBy(c => c.EntryId.Value));

        // Its title is the user's and it is an adventure; the file is suggested as an anthology, once.
        var part = (await _metadata.GetAsync(heist.Value, Ct)).Compute();
        Assert.Equal("Warehouse heist", part[MetadataFields.Title].First!.Value);
        Assert.True(part[MetadataFields.Title].First!.Confirmed);
        Assert.Equal(["adventure"], part[MetadataFields.Types].Values.Select(v => v.Value));
        var types = (await _metadata.GetAsync(file, Ct)).Compute()[MetadataFields.Types];
        var anthology = Assert.Single(types.Values);
        Assert.Equal(("anthology", false, AssertionOrigin.Rule), (anthology.Value, anthology.Confirmed, anthology.Origin));

        var stored = await _parts.GetPartAsync(heist.Value, Ct);
        Assert.Equal(new PartSource(document, new PartPages(10, 19), "fp10", "fp19", IsCurrent: true), Assert.Single(stored!.Sources));
        Assert.Equal([heist.Value, chase.Value], (await _parts.GetPartsAsync(file, Ct)).Select(p => p.EntryId));
        Assert.Equal([(heist.Value, new PartPages(10, 19)), (chase.Value, new PartPages(30, 39))], await _parts.GetPagesInAsync(document, Ct));
        Assert.Equal([file], await _parts.GetParentsAsync(Ct));
        Assert.Equal([heist.Value, chase.Value], (await _entries.GetPartIdsAsync([file], Ct)).OrderBy(e => e.Value));
    }

    [Fact]
    public async Task Adventures_in_one_copy_never_share_a_page_and_only_a_files_own_card_holds_them()
    {
        var (document, file) = await AddBookAsync("big compilation.pdf", 'a');
        var (other, _) = await AddBookAsync("another book.pdf", 'b');
        var heist = await _parts.AddAsync(file, new PartSource(document, new PartPages(10, 19)), "Warehouse heist", Ct);

        Assert.Null(await _parts.AddAsync(file, new PartSource(document, new PartPages(19, 25)), "Overlapping", Ct));
        Assert.Null(await _parts.AddAsync(file, new PartSource(other, new PartPages(0, 5)), "Not its copy", Ct));
        Assert.Null(await _parts.AddAsync(heist!.Value, new PartSource(document, new PartPages(20, 25)), "Nested", Ct));
        Assert.False(await _parts.SetSourcesAsync(heist.Value, [new PartSource(other, new PartPages(0, 5))], other, Ct));
        Assert.True(await _parts.SetSourcesAsync(heist.Value, [new PartSource(document, new PartPages(12, 20))], document, Ct));
        Assert.Equal(new PartPages(12, 20), (await _entries.GetCurrentAsync([heist.Value], Ct)).Single().Pages);
    }

    [Fact]
    public async Task An_adventure_takes_the_files_values_until_it_has_its_own()
    {
        var (document, file) = await AddBookAsync("big compilation.pdf", 'a');
        var heist = (await _parts.AddAsync(file, new PartSource(document, new PartPages(10, 19)), "Warehouse heist", Ct))!.Value;
        await _metadata.SetValuesAsync(file, MetadataFields.System, ["dnd"], Ct);
        await _metadata.ReplaceHintsAsync(file, Hex('a'), [new MetadataProposal(MetadataFields.Publisher, "Made Up Press", AssertionOrigin.Embedded)], Ct);

        var part = (await _metadata.GetAsync(heist, Ct)).Compute();
        Assert.Equal(("dnd", true), (part[MetadataFields.System].First!.Value, part[MetadataFields.System].First!.Confirmed));
        Assert.Equal(("Made Up Press", false), (part[MetadataFields.Publisher].First!.Value, part[MetadataFields.Publisher].First!.Confirmed));
        Assert.Contains(heist, (await _metadata.GetManyAsync(null, Ct)).Keys);

        // Keep this makes the file's suggestion the adventure's own; a change on the file doesn't reach it then.
        await _metadata.ConfirmAsync(heist, MetadataFields.Publisher, Ct);
        await _metadata.SetValuesAsync(file, MetadataFields.Publisher, ["Other Press"], Ct);
        part = (await _metadata.GetAsync(heist, Ct)).Compute();
        Assert.Equal(("Made Up Press", true), (part[MetadataFields.Publisher].First!.Value, part[MetadataFields.Publisher].First!.Confirmed));
        Assert.False(PartMetadata.IsInherited(part[MetadataFields.Publisher].First!.Source));

        // Not right on the adventure leaves the file's value on the file.
        await _metadata.RejectAsync(heist, MetadataFields.System, "dnd", Ct);
        Assert.Empty((await _metadata.GetAsync(heist, Ct)).Compute()[MetadataFields.System].Values);
        Assert.Equal("dnd", (await _metadata.GetAsync(file, Ct)).Compute()[MetadataFields.System].First!.Value);
    }

    [Fact]
    public async Task Removing_an_adventure_can_be_undone_with_what_was_set_on_it()
    {
        var (document, file) = await AddBookAsync("big compilation.pdf", 'a');
        var heist = (await _parts.AddAsync(file, new PartSource(document, new PartPages(10, 19), "fp10"), "Warehouse heist", Ct))!.Value;
        await _metadata.SetValuesAsync(heist, MetadataFields.Levels, ["3"], Ct);
        await new FavoriteStore(_contexts).SetAsync([heist], true, Ct);
        await new NoteStore(_contexts).SetEntryNoteAsync(heist, "Run it on Friday", Ct);

        var removed = await _parts.RemoveAsync(heist, Ct);

        Assert.NotNull(removed);
        Assert.Null(await _parts.RemoveAsync(file, Ct));
        Assert.Equal(0, (await _entries.GetCurrentAsync([file], Ct)).Single().Parts);
        Assert.Empty(await _entries.GetCurrentAsync([heist], Ct));
        // The file's last adventure went, so the Anthology suggestion did too.
        Assert.Empty((await _metadata.GetAsync(file, Ct)).Compute()[MetadataFields.Types].Values);

        var back = (await _parts.RestoreAsync(removed, Ct))!.Value;

        var card = (await _entries.GetCurrentAsync([back], Ct)).Single();
        Assert.Equal((document, new PartPages(10, 19), file), (card.DocumentId!.Value, card.Pages!.Value, card.ParentEntryId!.Value));
        Assert.Equal("fp10", (await _parts.GetPartAsync(back, Ct))!.Sources.Single().FirstFingerprint);
        var values = (await _metadata.GetAsync(back, Ct)).Compute();
        Assert.Equal(("Warehouse heist", "3"), (values[MetadataFields.Title].First!.Value, values[MetadataFields.Levels].First!.Value));
        Assert.True(await new FavoriteStore(_contexts).IsFavoriteAsync(back, Ct));
        Assert.Equal("Run it on Friday", await new NoteStore(_contexts).GetEntryNoteAsync(back, Ct));
        Assert.Equal("anthology", (await _metadata.GetAsync(file, Ct)).Compute()[MetadataFields.Types].First!.Value);
    }

    [Fact]
    public async Task Undo_after_removing_an_adventure_brings_back_its_page_notes()
    {
        var (document, heist) = await AddHeistAsync();
        var notes = new NoteStore(_contexts);
        var range = new PageRange(document, 12, 13, "13", "14", "fp12", "fp13") { Check = new PageRefCheck(PageCheck.Checked, document, 12, 13) };
        var note = (await notes.AddPageNoteAsync(heist, range, "The vault key is under the third barrel", Ct))!;

        var removed = await _parts.RemoveAsync(heist, Ct);

        // Nothing is left pointing at the pages: the note's page reference went with it.
        Assert.Equal(0, await CountAsync(db => db.PageRefs));
        var back = (await _parts.RestoreAsync(removed!, Ct))!.Value;

        var restored = Assert.Single(await notes.GetPageNotesAsync(back, Ct));
        Assert.Equal((note.Text, note.CreatedUtc, note.UpdatedUtc), (restored.Text, restored.CreatedUtc, restored.UpdatedUtc));
        Assert.Equal(range, restored.Range with { RefId = 0 });
    }

    [Fact]
    public async Task Undo_after_removing_an_adventure_brings_back_where_it_was_left()
    {
        var (document, heist) = await AddHeistAsync();
        await using (var db = _database.CreateContext())
        {
            db.ReadingStates.Add(new ReadingState { EntryId = heist.Value, DocumentId = document, PageIndex = 15, OpenedUtc = Monday });
            await db.SaveChangesAsync(Ct);
        }

        var back = (await _parts.RestoreAsync((await _parts.RemoveAsync(heist, Ct))!, Ct))!.Value;

        await using var check = _database.CreateContext();
        var reading = await check.ReadingStates.SingleAsync(Ct);
        Assert.Equal((back.Value, document, 15, Monday), (reading.EntryId, reading.DocumentId, reading.PageIndex, reading.OpenedUtc));
    }

    [Fact]
    public async Task Undo_after_removing_an_adventure_brings_back_the_pages_of_its_session_items()
    {
        var (document, heist) = await AddHeistAsync();
        var sessions = new SessionStore(_contexts);
        var pack = await sessions.CreateAsync("Friday game", ct: Ct);
        var range = new PageRange(document, 14, 15, "15", "16", "fp14", "fp15");
        await sessions.AddItemsAsync(pack.Id, [new NewSessionItem(heist, range, "Vault door", "Read the trap aloud")], ct: Ct);

        var removed = await _parts.RemoveAsync(heist, Ct);

        Assert.Equal(0, await CountAsync(db => db.PageRefs));
        var back = (await _parts.RestoreAsync(removed!, Ct))!.Value;

        var item = Assert.Single((await sessions.GetAsync(pack.Id, Ct))!.Items);
        Assert.Equal((back, "Vault door", "Read the trap aloud"), (item.EntryId, item.Label, item.Note));
        Assert.Equal(range, item.Range! with { RefId = 0 });
    }

    [Fact]
    public async Task Undo_after_removing_an_adventure_brings_back_its_ask_conversation()
    {
        var (_, heist) = await AddHeistAsync();
        var history = new AskHistoryStore(_contexts);
        await history.AddAsync(heist, "Who guards the vault?", null, "Two hired thugs [p. 13].", "[]", [12], "model-a", false, Ct);
        await history.AddAsync(heist, "And at night?", "Who guards the vault at night?", "The pages don't say.", "[]", [], "model-a", true, Ct);
        var before = await TurnsAsync(history, heist);

        var back = (await _parts.RestoreAsync((await _parts.RemoveAsync(heist, Ct))!, Ct))!.Value;

        Assert.Equal(before, await TurnsAsync(history, back));
    }

    [Fact]
    public async Task Undo_after_removing_an_adventure_keeps_it_read_by_the_model()
    {
        var (_, heist) = await AddHeistAsync();
        var runs = new ClassificationStore(_contexts);
        await runs.RecordAsync(new RunRecord(heist, Hex('a'), "ollama", "model-a", 3, 1, [10, 11], Monday, ClassificationStore.Complete), Ct);

        var back = (await _parts.RestoreAsync((await _parts.RemoveAsync(heist, Ct))!, Ct))!.Value;

        Assert.True(await runs.HasRunOnAsync(back, "model-a", 3, Ct));
        Assert.Equal("model-a", (await runs.GetReadByAsync([back], Ct))[back]);
    }

    /// <summary>A file with the adventure "Warehouse heist" marked on pages 10 to 19 of its only copy.</summary>
    async Task<(long Document, EntryId Heist)> AddHeistAsync()
    {
        var (document, file) = await AddBookAsync("big compilation.pdf", 'a');
        return (document, (await _parts.AddAsync(file, new PartSource(document, new PartPages(10, 19)), "Warehouse heist", Ct))!.Value);
    }

    async Task<int> CountAsync<T>(Func<CatalogDbContext, IQueryable<T>> rows)
    {
        await using var db = _database.CreateContext();
        return await rows(db).CountAsync(Ct);
    }

    static async Task<List<string>> TurnsAsync(AskHistoryStore history, EntryId entryId) =>
        [.. (await history.GetAsync(entryId, Ct)).Select(t => $"{t.AskedUtc:O} {t.Question} / {t.Rewritten} / {t.Answer} {t.Parts} [{string.Join(',', t.CitedPages)}] {t.Model} {t.NotAnswered}")];

    [Fact]
    public async Task A_kept_anthology_stays_when_the_last_adventure_goes()
    {
        var (document, file) = await AddBookAsync("big compilation.pdf", 'a');
        var heist = (await _parts.AddAsync(file, new PartSource(document, new PartPages(10, 19)), "Warehouse heist", Ct))!.Value;
        await _metadata.ConfirmAsync(file, MetadataFields.Types, Ct);
        await _parts.RemoveAsync(heist, Ct);
        Assert.Equal("anthology", (await _metadata.GetAsync(file, Ct)).Compute()[MetadataFields.Types].First!.Value);
    }

    [Fact]
    public async Task A_joining_copy_brings_its_adventures_and_not_the_same_book_takes_them_back()
    {
        var (core, original) = await AddBookAsync("Core/compilation.pdf", 'a');
        var (backup, copy) = await AddBookAsync("Backup/compilation.pdf", 'b');
        var heist = (await _parts.AddAsync(copy, new PartSource(backup, new PartPages(10, 19)), "Warehouse heist", Ct))!.Value;

        var join = await _entries.JoinAsCopyAsync(core, backup, Ct);

        Assert.Equal(original, join!.EntryId);
        Assert.Equal(original, (await _parts.GetPartAsync(heist, Ct))!.ParentEntryId);
        Assert.Equal(1, (await _entries.GetCurrentAsync([original], Ct)).Single().Parts);
        // Its pages are in the copy that joined, not yet in the one the file opens: it shows those until they're found.
        var card = (await _entries.GetCurrentAsync([heist], Ct)).Single();
        Assert.Equal((backup, original), (card.DocumentId!.Value, card.ParentEntryId!.Value));

        await _entries.SplitCopyAsync(original, backup, ct: Ct);

        Assert.Equal(copy, (await _parts.GetPartAsync(heist, Ct))!.ParentEntryId);
        Assert.Equal(0, (await _entries.GetCurrentAsync([original], Ct)).Single().Parts);
    }

    [Fact]
    public async Task An_adventure_in_a_missing_copy_doesnt_mark_its_file_missing()
    {
        var (core, file) = await AddBookAsync("Core/compilation.pdf", 'a');
        var (backup, _) = await AddBookAsync("Backup/compilation.pdf", 'b');
        await _entries.JoinAsCopyAsync(core, backup, Ct);
        var heist = (await _parts.AddAsync(file, new PartSource(backup, new PartPages(10, 19)), "Warehouse heist", Ct))!.Value;
        _files.RemoveAll(f => f.RelativePath == "Backup/compilation.pdf");
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);

        var unavailable = await _library.GetUnavailableAsync(Ct);

        Assert.Equal(EntryAvailability.Missing, unavailable[heist]);
        Assert.False(unavailable.ContainsKey(file));
    }

    [Fact]
    public async Task An_adventures_pages_are_stored_with_its_fingerprints()
    {
        var (document, file) = await AddBookAsync("big compilation.pdf", 'a');
        await _parts.AddAsync(file, new PartSource(document, new PartPages(10, 19), "first", "last"), "Warehouse heist", Ct);
        await using var db = _database.CreateContext();
        var row = await db.EntrySources.AsNoTracking().SingleAsync(s => s.FirstPdfPage != null, Ct);
        Assert.Equal((10, 19, "first", "last", true), (row.FirstPdfPage!.Value, row.LastPdfPage!.Value, row.FirstFingerprint, row.LastFingerprint, row.IsCurrent));
        Assert.Equal(EntryKind.Part, (await db.Entries.AsNoTracking().SingleAsync(e => e.Id == row.EntryId, Ct)).Kind);
    }
}
