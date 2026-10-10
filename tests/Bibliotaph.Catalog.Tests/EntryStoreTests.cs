using Bibliotaph.Catalog.Entities;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog.Tests;

/// <summary>Copies (F2): joining two cards of one book, Undo, which copy opens, and new versions at the same path.</summary>
public sealed class EntryStoreTests : IAsyncLifetime
{
    static readonly DateTime Monday = new(2026, 10, 5, 9, 30, 0, DateTimeKind.Utc);
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-entries-").FullName;
    CatalogDatabase _database = null!;
    Factory _contexts = null!;
    LibraryStore _library = null!;
    EntryStore _entries = null!;
    MetadataStore _metadata = null!;
    long _root;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = new CatalogDatabase(Path.Combine(_dir, "catalog.db"), Path.Combine(_dir, "backups"));
        await _database.MigrateAsync();
        _contexts = new Factory(_database);
        _library = new LibraryStore(_contexts);
        _entries = new EntryStore(_contexts);
        _metadata = new MetadataStore(_contexts);
        _root = (await new SourceRootStore(_contexts).AddAsync(Path.Combine(_dir, "Library"), Ct)).Id;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return ValueTask.CompletedTask;
    }

    static string Hex(char c) => new(c, 64);

    readonly List<ScannedFile> _files = [];

    /// <summary>Scans a new file into the library and hashes it as <paramref name="hash"/>: a document with its own card.</summary>
    async Task<(long Document, EntryId Entry)> AddBookAsync(string path, char hash, long size = 1000)
    {
        _files.RemoveAll(f => f.RelativePath == path);
        _files.Add(new ScannedFile(path, size, Monday, OnlineOnly: false));
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);
        var file = (await _library.NextUnhashedAsync(10, includeOnlineOnly: true, Ct)).Single(f => f.FullPath.EndsWith(path, StringComparison.Ordinal));
        var document = (await _library.AttachHashAsync(file, ContentHash.Parse(Hex(hash)), Ct))!.Value.DocumentId;
        return (document, (await _entries.GetEntryAsync(document, Ct))!.EntryId);
    }

    async Task<List<Assertion>> ClaimsAsync(EntryId entry, MetadataField field)
    {
        await using var db = _database.CreateContext();
        return await db.Assertions.AsNoTracking().Where(a => a.EntryId == entry.Value && a.Field == field.Key).OrderBy(a => a.Id).ToListAsync(Ct);
    }

    [Fact]
    public async Task A_join_brings_the_newer_cards_work_to_the_older_and_not_the_same_book_takes_it_back()
    {
        var (core, original) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backup, copy) = await AddBookAsync("Backup/abbey.pdf", 'b');
        await _metadata.SetValuesAsync(original, MetadataFields.Title, ["The Drowned Abbey"], Ct);
        await _metadata.SetValuesAsync(original, MetadataFields.Tags, ["Friday game"], Ct);
        await _metadata.SetValuesAsync(copy, MetadataFields.Title, ["Abbey backup"], Ct);
        await _metadata.SetValuesAsync(copy, MetadataFields.Tags, ["Printed"], Ct);
        await _metadata.ReplaceHintsAsync(copy, Hex('b'), [new MetadataProposal(MetadataFields.Edition, "dnd-5e", AssertionOrigin.Folder, "5e")], Ct);
        await _metadata.RejectAsync(copy, MetadataFields.Types, "adventure", Ct);
        var run = await new ClassificationStore(_contexts).RecordAsync(
            new RunRecord(copy, Hex('b'), "ollama", "model-a", 1, 1, [0], Monday, ClassificationStore.Complete), Ct);

        // Matched from either side, the newer card joins the older one.
        var join = await _entries.JoinAsCopyAsync(core, backup, Ct);

        Assert.Equal(new CopyJoin(original, copy), join);
        Assert.Equal([new EntryDocument(original, core, EntryKind.Whole, Copies: 2)], await _entries.GetCurrentAsync(ct: Ct));
        var effective = (await _metadata.GetAsync(original, Ct)).Compute();
        Assert.Equal(("The Drowned Abbey", true), (effective[MetadataFields.Title].First!.Value, effective[MetadataFields.Title].First!.Confirmed));
        Assert.Contains(await ClaimsAsync(original, MetadataFields.Title), a => a is { NormalizedValue: "abbey backup", State: AssertionState.SetAside });
        Assert.Equal(["Friday game", "Printed"], effective[MetadataFields.Tags].Values.Select(v => v.Value).Order());
        Assert.Equal("dnd-5e", effective[MetadataFields.Edition].First?.Value);
        Assert.Contains((await _metadata.GetAsync(original, Ct)).Rejections, r => r.Normalized == "adventure");
        Assert.Equal("model-a", (await new ClassificationStore(_contexts).GetReadByAsync([original], Ct))[original]);
        var copies = await _entries.GetCopiesAsync(original, Ct);
        Assert.Equal([(core, true, false), (backup, false, true)], copies.Select(c => (c.DocumentId, c.IsCurrent, c.Joined)));

        var split = await _entries.SplitCopyAsync(original, backup, ct: Ct);

        Assert.Equal(copy, split);
        Assert.Equal([(original, core, 1), (copy, backup, 1)],
            (await _entries.GetCurrentAsync(ct: Ct)).OrderBy(e => e.EntryId.Value).Select(e => (e.EntryId, e.DocumentId, e.Copies)));
        var back = (await _metadata.GetAsync(copy, Ct)).Compute();
        Assert.Equal(("Abbey backup", true), (back[MetadataFields.Title].First!.Value, back[MetadataFields.Title].First!.Confirmed));
        Assert.Equal(["Printed"], back[MetadataFields.Tags].Values.Select(v => v.Value));
        Assert.Contains((await _metadata.GetAsync(copy, Ct)).Rejections, r => r.Normalized == "adventure");
        Assert.Equal(["Friday game"], (await _metadata.GetAsync(original, Ct)).Compute()[MetadataFields.Tags].Values.Select(v => v.Value));
        Assert.Equal("model-a", (await new ClassificationStore(_contexts).GetReadByAsync([copy], Ct))[copy]);
        Assert.NotNull(run);

        // Remembered for the pair, from either side.
        Assert.True(await _entries.IsNotSameBookAsync(Hex('b'), Hex('a'), Ct));
        Assert.Null(await _entries.JoinAsCopyAsync(backup, core, Ct));
    }

    /// <summary>The entry's Needs review cards, less the missing-title card these untitled test books all have.</summary>
    static IReadOnlyList<ReviewIssue> Cards(EntryMetadata metadata) =>
        [.. MetadataReview.Find(metadata.Compute(), reviewAll: false).Where(i => i.Kind != ReviewKind.MissingTitle)];

    [Fact]
    public async Task Copies_that_disagree_are_settled_by_the_users_choice_and_a_split_gives_each_card_its_own_value_back()
    {
        var (core, original) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backup, copy) = await AddBookAsync("Backup/abbey.pdf", 'b');
        await _metadata.SetValuesAsync(original, MetadataFields.Title, ["The Drowned Abbey"], Ct);
        await _metadata.SetValuesAsync(copy, MetadataFields.Title, ["The Drowned Abbey, revised"], Ct);
        await _entries.JoinAsCopyAsync(core, backup, Ct);

        var card = Assert.Single(Cards(await _metadata.GetAsync(original, Ct)));
        Assert.Equal((ReviewKind.CopiesDisagree, "The Drowned Abbey, revised"), (card.Kind, card.Proposed.Single().Value));

        // Accepting the other copy's title settles the card.
        await _metadata.SetValuesAsync(original, MetadataFields.Title, ["The Drowned Abbey, revised"], Ct);
        Assert.Empty(Cards(await _metadata.GetAsync(original, Ct)));
        Assert.Equal("The Drowned Abbey, revised", (await _metadata.GetAsync(original, Ct)).Compute()[MetadataFields.Title].First?.Value);

        // Split, each card has the title the user gave it: the copy's from before the join, the book's from the card.
        var split = (await _entries.SplitCopyAsync(original, backup, ct: Ct))!.Value;
        Assert.Equal(copy, split);
        Assert.Equal(("The Drowned Abbey, revised", true), Title(await _metadata.GetAsync(copy, Ct)));
        Assert.Equal(("The Drowned Abbey, revised", true), Title(await _metadata.GetAsync(original, Ct)));
        Assert.Empty(Cards(await _metadata.GetAsync(copy, Ct)));
    }

    [Fact]
    public async Task Keeping_this_cards_value_or_resetting_the_field_settles_copies_that_disagree()
    {
        var (core, original) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backup, copy) = await AddBookAsync("Backup/abbey.pdf", 'b');
        var (lantern, lanternCard) = await AddBookAsync("lantern.pdf", 'c');
        var (lanternCopy, lanternCopyCard) = await AddBookAsync("Backup/lantern.pdf", 'd');
        foreach (var (entry, year) in new[] { (original, "1999"), (copy, "2001"), (lanternCard, "1999"), (lanternCopyCard, "2001") })
            await _metadata.SetValuesAsync(entry, MetadataFields.Year, [year], Ct);
        await _entries.JoinAsCopyAsync(core, backup, Ct);
        await _entries.JoinAsCopyAsync(lantern, lanternCopy, Ct);

        await _metadata.RejectAndKeepAsync(original, MetadataFields.Year, ["2001"], ["1999"], Ct);
        await _metadata.ResetAsync(lanternCard, MetadataFields.Year, Ct);

        Assert.Empty(Cards(await _metadata.GetAsync(original, Ct)));
        Assert.Equal(("1999", true), Year(await _metadata.GetAsync(original, Ct)));
        Assert.Empty(Cards(await _metadata.GetAsync(lanternCard, Ct)));
        Assert.Null((await _metadata.GetAsync(lanternCard, Ct)).Compute()[MetadataFields.Year].First);
    }

    static (string?, bool) Title(EntryMetadata metadata) => Value(metadata, MetadataFields.Title);

    static (string?, bool) Year(EntryMetadata metadata) => Value(metadata, MetadataFields.Year);

    static (string?, bool) Value(EntryMetadata metadata, MetadataField field) =>
        metadata.Compute()[field].First is { } value ? (value.Value, value.Confirmed) : (null, false);

    [Fact]
    public async Task Make_current_chooses_the_copy_that_opens_and_a_copy_whose_file_has_gone_gives_way()
    {
        var (core, original) = await AddBookAsync("Core/abbey.pdf", 'a');
        var (backup, _) = await AddBookAsync("Backup/abbey.pdf", 'b');
        await _entries.JoinAsCopyAsync(backup, core, Ct);

        Assert.True(await _entries.MakeCurrentAsync(original, backup, Ct));
        Assert.Equal(backup, await _entries.GetCurrentDocumentAsync(original, Ct));
        Assert.False(await _entries.MakeCurrentAsync(original, 999, Ct));

        // The backup's file is deleted: the other copy opens, though the choice is kept for when it comes back.
        _files.RemoveAll(f => f.RelativePath == "Backup/abbey.pdf");
        await _library.ReconcileRootAsync(_root, _files, ct: Ct);

        Assert.Equal(core, await _entries.GetCurrentDocumentAsync(original, Ct));
        var copies = await _entries.GetCopiesAsync(original, Ct);
        Assert.Equal([(backup, true, false, false), (core, false, true, true)], copies.Select(c => (c.DocumentId, c.IsCurrent, c.IsShown, c.HasFile)));
        Assert.Equal([original], await _library.GetVisibleEntryIdsAsync(ct: Ct));
    }

    [Fact]
    public async Task New_content_at_a_books_path_becomes_its_current_version_and_can_still_be_split_off()
    {
        var (first, entry) = await AddBookAsync("abbey.pdf", 'a');
        await _metadata.SetValuesAsync(entry, MetadataFields.Tags, ["Friday game"], Ct);

        var (second, secondEntry) = await AddBookAsync("abbey.pdf", 'c', size: 2000);

        Assert.Equal(entry, secondEntry);
        Assert.Equal([new EntryDocument(entry, second, EntryKind.Whole, Copies: 2)], await _entries.GetCurrentAsync(ct: Ct));
        Assert.Equal([(second, true, true), (first, false, false)], (await _entries.GetCopiesAsync(entry, Ct)).Select(c => (c.DocumentId, c.IsCurrent, c.HasFile)));

        // Not the same book on a version that didn't join by matching: it gets a new card, and the book keeps its work.
        var split = await _entries.SplitCopyAsync(entry, second, ct: Ct);

        Assert.NotNull(split);
        Assert.NotEqual(entry, split);
        Assert.Equal(first, await _entries.GetCurrentDocumentAsync(entry, Ct));
        Assert.Equal(second, await _entries.GetCurrentDocumentAsync(split.Value, Ct));
        Assert.Equal(["Friday game"], (await _metadata.GetAsync(entry, Ct)).Compute()[MetadataFields.Tags].Values.Select(v => v.Value));
        Assert.Null(await _entries.SplitCopyAsync(split.Value, second, ct: Ct));
    }

    [Fact]
    public async Task Content_already_known_at_a_new_path_keeps_its_own_card()
    {
        var (first, entry) = await AddBookAsync("abbey.pdf", 'a');
        var (other, otherEntry) = await AddBookAsync("lantern.pdf", 'b');

        // The abbey's file is overwritten with the lantern's content: the lantern's card has it already.
        var (again, againEntry) = await AddBookAsync("abbey.pdf", 'b', size: 2000);

        Assert.Equal((other, otherEntry), (again, againEntry));
        Assert.Equal(first, await _entries.GetCurrentDocumentAsync(entry, Ct));
    }

    // ---- Owned elsewhere (F5a) -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_book_owned_elsewhere_is_a_card_with_no_file_that_shows_whatever_folders_are_online()
    {
        var (abbey, abbeyEntry) = await AddBookAsync("abbey.pdf", 'a');
        var printed = await _entries.AddElsewhereAsync(Ct);

        var current = await _entries.GetCurrentAsync(ct: Ct);
        Assert.Contains(new EntryDocument(abbeyEntry, abbey, EntryKind.Whole), current);
        var elsewhere = Assert.Single(current, c => c.EntryId == printed);
        Assert.Equal((null, EntryKind.Elsewhere, 0), (elsewhere.DocumentId, elsewhere.Kind, elsewhere.Copies));
        Assert.NotNull(elsewhere.AddedUtc);
        Assert.Equal(EntryKind.Elsewhere, await _entries.GetKindAsync(printed, Ct));
        Assert.Null(await _entries.GetCurrentDocumentAsync(printed, Ct));

        // Always in scope, but in no one folder.
        Assert.Equal([abbeyEntry, printed], (await _library.GetVisibleEntryIdsAsync(ct: Ct)).OrderBy(e => e.Value));
        Assert.Equal([abbeyEntry], await _library.GetVisibleEntryIdsAsync(_root, Ct));
    }

    [Fact]
    public async Task Removing_a_book_owned_elsewhere_deletes_it_and_undo_brings_back_what_was_set_on_it()
    {
        var (_, abbeyEntry) = await AddBookAsync("abbey.pdf", 'a');
        var printed = await _entries.AddElsewhereAsync(Ct);
        await _metadata.SetValuesAsync(printed, MetadataFields.Title, ["Tomb of the Serpent"], Ct);
        await _metadata.SetValuesAsync(printed, MetadataFields.AlsoOwn, ["print", "foundry"], Ct);
        await _metadata.RejectAsync(printed, MetadataFields.Types, "adventure", Ct);

        // A file's card can't be removed this way.
        Assert.Null(await _entries.RemoveElsewhereAsync(abbeyEntry, Ct));

        var removed = await _entries.RemoveElsewhereAsync(printed, Ct);

        Assert.NotNull(removed);
        Assert.DoesNotContain(await _entries.GetCurrentAsync(ct: Ct), c => c.EntryId == printed);
        Assert.Null(await _entries.GetKindAsync(printed, Ct));

        var restored = await _entries.RestoreElsewhereAsync(removed, Ct);

        var metadata = (await _metadata.GetAsync(restored, Ct)).Compute();
        Assert.Equal("Tomb of the Serpent", metadata[MetadataFields.Title].First?.Value);
        Assert.Equal(["foundry", "print"], metadata[MetadataFields.AlsoOwn].Values.Select(v => v.Value).Order());
        Assert.All(metadata[MetadataFields.AlsoOwn].Values, v => Assert.True(v.Confirmed));
        Assert.Single((await _metadata.GetAsync(restored, Ct)).Rejections);
        Assert.Equal(EntryKind.Elsewhere, await _entries.GetKindAsync(restored, Ct));
    }

    [Fact]
    public async Task Same_book_brings_the_file_to_the_book_owned_elsewhere_and_undo_takes_it_back()
    {
        var printed = await _entries.AddElsewhereAsync(Ct);
        await _metadata.SetValuesAsync(printed, MetadataFields.Title, ["Tomb of the Serpent"], Ct);
        await _metadata.SetValuesAsync(printed, MetadataFields.AlsoOwn, ["print"], Ct);
        var (tomb, file) = await AddBookAsync("tomb.pdf", 'c');
        await _metadata.ReplaceHintsAsync(file, Hex('c'), [new MetadataProposal(MetadataFields.Title, "tomb", AssertionOrigin.Filename, "tomb.pdf")], Ct);
        await _metadata.SetValuesAsync(file, MetadataFields.Tags, ["Prep"], Ct);

        var join = await _entries.JoinElsewhereAsync(printed, tomb, Ct);

        Assert.Equal(new CopyJoin(printed, file), join);
        Assert.Equal([new EntryDocument(printed, tomb, EntryKind.Whole)], await _entries.GetCurrentAsync(ct: Ct));
        var metadata = (await _metadata.GetAsync(printed, Ct)).Compute();
        Assert.Equal("Tomb of the Serpent", metadata[MetadataFields.Title].First?.Value);
        Assert.Equal(["print"], metadata[MetadataFields.AlsoOwn].Values.Select(v => v.Value));
        Assert.Equal(["Prep"], metadata[MetadataFields.Tags].Values.Select(v => v.Value));
        Assert.Null(await _entries.JoinElsewhereAsync(printed, tomb, Ct));

        // Undo: the file has its own card with what it brought, and the book is owned elsewhere again.
        Assert.Equal(file, await _entries.UndoJoinElsewhereAsync(printed, tomb, Ct));

        var current = await _entries.GetCurrentAsync(ct: Ct);
        Assert.Contains(new EntryDocument(file, tomb, EntryKind.Whole), current);
        Assert.Contains(current, c => c.EntryId == printed && c.Kind == EntryKind.Elsewhere && c.DocumentId is null);
        Assert.Equal(["Prep"], (await _metadata.GetAsync(file, Ct)).Compute()[MetadataFields.Tags].Values.Select(v => v.Value));
        Assert.Empty((await _metadata.GetAsync(printed, Ct)).Compute()[MetadataFields.Tags].Values);
        Assert.Null(await _entries.UndoJoinElsewhereAsync(printed, tomb, Ct));
    }

    [Fact]
    public async Task A_file_is_offered_once_to_a_book_owned_elsewhere_and_separate_book_is_remembered()
    {
        var elsewhere = new ElsewhereStore(_contexts, _entries);
        var printed = await _entries.AddElsewhereAsync(Ct);
        var (tomb, file) = await AddBookAsync("tomb.pdf", 'c');
        var (abbey, _) = await AddBookAsync("abbey.pdf", 'a');

        Assert.True(await elsewhere.ProposeAsync(printed, tomb, Ct));
        Assert.False(await elsewhere.ProposeAsync(printed, tomb, Ct));
        Assert.False(await elsewhere.ProposeAsync(file, abbey, Ct)); // not a book owned elsewhere
        var pending = Assert.Single(await elsewhere.GetPendingAsync(Ct));
        Assert.Equal((printed, tomb, file), (pending.EntryId, pending.DocumentId, pending.FileEntryId));

        var separate = await elsewhere.AnswerAsync(pending.Id, ElsewhereAnswer.SeparateBook, Ct);

        Assert.NotNull(separate);
        Assert.Empty(await elsewhere.GetPendingAsync(Ct));
        Assert.False(await elsewhere.ProposeAsync(printed, tomb, Ct));
        Assert.Null(await elsewhere.AnswerAsync(pending.Id, ElsewhereAnswer.SameBook, Ct));

        await elsewhere.UndoAsync(separate, Ct);
        pending = Assert.Single(await elsewhere.GetPendingAsync(Ct));

        var same = await elsewhere.AnswerAsync(pending.Id, ElsewhereAnswer.SameBook, Ct);

        Assert.NotNull(same);
        Assert.Equal(tomb, await _entries.GetCurrentDocumentAsync(printed, Ct));
        Assert.Empty(await elsewhere.GetPendingAsync(Ct));

        await elsewhere.UndoAsync(same, Ct);

        Assert.Equal(EntryKind.Elsewhere, await _entries.GetKindAsync(printed, Ct));
        Assert.Equal(tomb, await _entries.GetCurrentDocumentAsync(file, Ct));
        Assert.Single(await elsewhere.GetPendingAsync(Ct));
    }
}
