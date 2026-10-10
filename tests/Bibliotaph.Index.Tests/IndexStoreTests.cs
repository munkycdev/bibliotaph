using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Index.Tests;

public sealed class IndexStoreTests : IndexFixture
{
    IndexStore _store = null!;
    IndexQueries _queries = null!;
    JobBoard _queue = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _store = new IndexStore(Writer, Clock);
        _queries = new IndexQueries(Database);
        _queue = new JobBoard(Writer, Database, Clock);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DocRow Doc(long id = 1) => new()
    {
        DocumentId = id,
        ContentHash = $"hash{id}",
        Format = "pdf",
        DisplayTitle = "Gazetteer of the Marches",
        PageCount = 3,
        MetaAuthor = "A. Cartographer",
        FolderHint = "Setting / Maps",
    };

    static PageRow[] Pages(int count) => [.. Enumerable.Range(0, count).Select(i => new PageRow(i, (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 612, 792))];

    long[] DocSearch(string match) =>
        [.. Connection.Query<long>("SELECT rowid FROM entry_fts WHERE entry_fts MATCH @match", new { match })];

    long[] PageSearch(string match) =>
        [.. Connection.Query<long>("SELECT p.pdf_page FROM page_fts JOIN page p ON p.id = page_fts.rowid WHERE page_fts MATCH @match", new { match })];

    [Fact]
    public async Task A_probed_document_is_searchable_by_title_and_its_provisional_details()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(3), [new OutlineRow("Chapter one", 1, 0)], Ct);
        await AddEntriesAsync(_store, 1);

        Assert.Equal([EntryOf(1).Value], DocSearch("title:gazetteer"));
        Assert.Equal([EntryOf(1).Value], DocSearch("provisional:cartographer"));
        Assert.Equal([EntryOf(1).Value], DocSearch("provisional:maps"));
        Assert.Equal(3, Connection.ExecuteScalar<long>("SELECT count(*) FROM page WHERE document_id = 1"));
        Assert.Equal("Chapter one", Connection.ExecuteScalar<string>("SELECT title FROM outline WHERE document_id = 1"));
    }

    [Fact]
    public async Task Probing_again_keeps_extracted_text_and_drops_pages_that_are_gone()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(3), [], Ct);
        await AddEntriesAsync(_store, 1);
        await _store.SetPageTextAsync(1, [new PageTextRow(0, "the owlbear sleeps", "pdf", 1, false)], Ct);

        await _store.UpsertDocumentAsync(Doc() with { DisplayTitle = "Gazetteer, revised" }, Pages(2), [], Ct);

        Assert.Equal([0L], PageSearch("owlbear"));
        Assert.Equal(2, Connection.ExecuteScalar<long>("SELECT count(*) FROM page WHERE document_id = 1"));
        Assert.Equal([EntryOf(1).Value], DocSearch("title:revised"));
        Assert.Equal(1, Connection.ExecuteScalar<long>("SELECT count(*) FROM entry_fts"));
    }

    [Fact]
    public async Task Pages_flagged_for_ocr_are_listed_until_their_ocr_text_arrives()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(3), [], Ct);
        await _store.SetPageTextAsync(1,
        [
            new PageTextRow(0, "contents", "pdf", 1, false),
            new PageTextRow(1, "", "pdf", 0, true),
            new PageTextRow(2, "", "pdf", 0, true),
        ], Ct);

        Assert.Equal([1, 2], (await _queries.GetPagesNeedingOcrAsync(1, Ct)).Select(p => p.PdfPage));

        await _store.SetOcrPageAsync(1, 1, "goblin market", 0.9, [new OcrWordRow("goblin", 72, 700, 140, 680), new OcrWordRow("market", 150, 700, 230, 680)], Ct);
        await _store.SetPageErrorAsync(1, 2, "OCR timed out", Ct);

        Assert.Empty(await _queries.GetPagesNeedingOcrAsync(1, Ct));
        Assert.Equal([1L], PageSearch("goblin"));
        Assert.Equal(2, Connection.ExecuteScalar<long>("SELECT count(*) FROM ocr_word"));
        Assert.Equal("ocr", Connection.ExecuteScalar<string>("SELECT text_source FROM page WHERE document_id = 1 AND pdf_page = 1"));
        Assert.Equal(1, (await _queries.GetProgressAsync(Ct)).OcrPagesDone);
    }

    [Fact]
    public async Task Progress_and_attention_follow_the_stage_statuses()
    {
        await _store.UpsertDocumentAsync(Doc(1), Pages(1), [], Ct);
        await _store.UpsertDocumentAsync(Doc(2) with { DisplayTitle = "Locked book" }, Pages(1), [], Ct);
        await AddEntriesAsync(_store, 1, 2);
        await _queue.EnqueueAsync(1, "hash1", Stage.Text, ct: Ct);
        await _queue.EnqueueAsync(2, "hash2", Stage.Text, ct: Ct);
        var lane = new[] { Stage.Text };
        await _queue.CompleteAsync((await _queue.LeaseAsync(lane, "t", TimeSpan.FromMinutes(1), Ct))!, ct: Ct);
        await _queue.BlockAsync((await _queue.LeaseAsync(lane, "t", TimeSpan.FromMinutes(1), Ct))!, "Needs a password", Ct);

        var progress = await _queries.GetProgressAsync(Ct);
        var attention = Assert.Single(await _queries.GetAttentionAsync(Ct));

        Assert.Equal(new IndexProgress(2, 1, 0, 1, 0, 0, Queued: 2), progress);
        Assert.Equal(2, progress.Indexed);
        Assert.Equal(1, await _queries.CountSearchableAsync([EntryOf(1), EntryOf(2), EntryOf(3)], Ct));
        Assert.Equal(0, await _queries.CountSearchableAsync([EntryOf(2)], Ct));
        Assert.Equal("Locked book", await _queries.GetTitleAsync(2, Ct));
        Assert.Null(await _queries.GetTitleAsync(9, Ct));
        Assert.Equal(("Locked book", StageStatus.Blocked, "Needs a password"), (attention.Title, attention.Status, attention.Reason));
    }

    [Fact]
    public async Task Forgetting_a_documents_text_waits_for_its_running_stage_then_removes_its_pages_and_cover()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(2), [new OutlineRow("Chapter one", 1, 0)], Ct);
        await AddEntriesAsync(_store, 1);
        await _store.SetPageTextAsync(1, [new PageTextRow(0, "the owlbear sleeps", "pdf", 1, false)], Ct);
        await _store.SetCoverAsync(1, "hash1.jpg", Ct);
        await _queue.EnqueueAsync(1, "hash1", Stage.Covers, ct: Ct);
        var running = (await _queue.LeaseAsync([Stage.Covers], "t", TimeSpan.FromMinutes(1), Ct))!;

        Assert.False(await _store.ForgetTextAsync(1, Ct));
        Assert.Equal([0L], PageSearch("owlbear"));

        await _queue.ReleaseAsync(running, Ct);
        Assert.True(await _store.ForgetTextAsync(1, Ct));

        Assert.Empty(PageSearch("owlbear"));
        Assert.Equal(0, Connection.ExecuteScalar<long>("SELECT count(*) FROM page WHERE document_id = 1"));
        Assert.Equal(0, Connection.ExecuteScalar<long>("SELECT count(*) FROM outline WHERE document_id = 1"));
        Assert.Equal(0, Connection.ExecuteScalar<long>("SELECT count(*) FROM job WHERE document_id = 1"));
        Assert.Null(Connection.ExecuteScalar<string?>("SELECT cover FROM doc WHERE document_id = 1"));
        // The card stays, by its title, saying why it has no text.
        var entry = Assert.Single(await new LibraryQueries(Database).ListAsync(new LibraryFilter(), ct: Ct));
        Assert.Equal(("Gazetteer of the Marches", TextAccess.Forgotten, false), (entry.Title, entry.TextAccess, entry.Searchable));
        Assert.Equal(1, (await _queries.GetProgressAsync(Ct)).Withheld);
        Assert.Empty(await _queries.GetAttentionAsync(Ct));
    }

    [Fact]
    public async Task A_locked_or_protected_file_is_withheld_from_what_is_still_being_read()
    {
        await _store.UpsertDocumentAsync(Doc(1) with { DisplayTitle = "Locked" }, [], [], Ct);
        await _store.UpsertDocumentAsync(Doc(2) with { DisplayTitle = "Protected" }, [], [], Ct);
        await _store.UpsertDocumentAsync(Doc(3) with { DisplayTitle = "Damaged" }, [], [], Ct);
        await AddEntriesAsync(_store, 1, 2, 3);
        foreach (var id in new long[] { 1, 2, 3 }) await _queue.EnqueueAsync(id, $"hash{id}", Stage.Probe, ct: Ct);
        var lane = new[] { Stage.Probe };
        await _queue.BlockAsync((await _queue.LeaseAsync(lane, "t", TimeSpan.FromMinutes(1), Ct))!, TextAccessReasons.Locked, Ct);
        await _queue.FailAsync((await _queue.LeaseAsync(lane, "t", TimeSpan.FromMinutes(1), Ct))!, TextAccessReasons.Protected, retry: false, Ct);
        await _queue.FailAsync((await _queue.LeaseAsync(lane, "t", TimeSpan.FromMinutes(1), Ct))!, "This file isn't a readable PDF.", retry: false, Ct);

        var entries = (await new LibraryQueries(Database).ListAsync(new LibraryFilter(), ct: Ct)).ToDictionary(e => e.Title, e => e.TextAccess);

        Assert.Equal(TextAccess.Locked, entries["Locked"]);
        Assert.Equal(TextAccess.Protected, entries["Protected"]);
        Assert.Equal(TextAccess.Readable, entries["Damaged"]);
        Assert.Equal(2, (await _queries.GetProgressAsync(Ct)).Withheld);
    }

    [Fact]
    public async Task Titles_by_content_hash_are_the_cards_or_the_files()
    {
        await _store.UpsertDocumentAsync(Doc(1), [], [], Ct);
        await _store.UpsertDocumentAsync(Doc(2) with { DisplayTitle = "Uncarded copy" }, [], [], Ct);
        await AddEntriesAsync(_store, 1);

        var titles = await _queries.GetTitlesByHashAsync(["hash1", "hash2", "hash9"], Ct);

        Assert.Equal(2, titles.Count);
        Assert.Equal("Gazetteer of the Marches", titles["hash1"]);
        Assert.Equal("Uncarded copy", titles["hash2"]);
    }

    [Fact]
    public async Task Progress_tells_index_work_from_ocr_work()
    {
        await _store.UpsertDocumentAsync(Doc(1), Pages(1), [], Ct);
        await _store.UpsertDocumentAsync(Doc(2), Pages(1), [], Ct);
        await _queue.EnqueueAsync(1, "hash1", Stage.Ocr, ct: Ct);

        // Only OCR is waiting: the index lane has nothing to pause.
        var progress = await _queries.GetProgressAsync(Ct);
        Assert.Equal((1L, 0L), (progress.Processing, progress.Indexing));

        await _queue.EnqueueAsync(2, "hash2", Stage.Covers, ct: Ct);
        progress = await _queries.GetProgressAsync(Ct);
        Assert.Equal((2L, 1L), (progress.Processing, progress.Indexing));
    }

    [Fact]
    public async Task Reading_pages_again_keeps_text_the_new_reading_cannot_replace_yet()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(4), [], Ct);
        await _store.SetPageTextAsync(1,
        [
            new PageTextRow(0, "the owlbear sleeps", "pdf", 1, false),
            new PageTextRow(1, "", "none", 0, true),
            new PageTextRow(2, "", "none", 0, true),
            new PageTextRow(3, "lich ledger", "pdf", 1, false),
        ], Ct);
        await _store.SetOcrPageAsync(1, 1, "goblin market", 0.9, [], Ct);
        await _store.SetOcrPageAsync(1, 2, "haunted mill", 0.9, [], Ct);
        // As Reprocess with OCR on every page does, before Text runs again.
        await Writer.WriteAsync((c, t) => c.Execute("UPDATE page SET needs_ocr = 1 WHERE document_id = 1 AND pdf_page IN (2, 3)", transaction: t), Ct);

        await _store.SetPageTextAsync(1,
        [
            new PageTextRow(0, "the owlbear wakes", "pdf", 1, false),
            new PageTextRow(1, "", "none", 0, true),
            new PageTextRow(2, "a good text layer", "pdf", 1, false),
            new PageTextRow(3, "", "none", 0, false, "The page could not be read."),
        ], Ct);

        // New text replaces old; OCR text waits for OCR; a page that couldn't be read keeps its text.
        Assert.Equal([0L], PageSearch("wakes"));
        Assert.Equal([1L], PageSearch("goblin"));
        Assert.Equal([2L], PageSearch("haunted"));
        Assert.Equal([3L], PageSearch("lich"));
        Assert.Equal([1, 2, 3], (await _queries.GetPagesNeedingOcrAsync(1, Ct)).Select(p => p.PdfPage));
    }

    [Fact]
    public async Task An_entry_recorded_before_its_document_is_probed_becomes_searchable_with_the_probe()
    {
        await AddEntriesAsync(_store, 1);
        Assert.Empty(DocSearch("title:gazetteer"));

        await _store.UpsertDocumentAsync(Doc(), Pages(1), [], Ct);

        Assert.Equal([EntryOf(1).Value], DocSearch("title:gazetteer"));
    }

    [Fact]
    public async Task Moving_an_entry_to_another_document_searches_the_new_documents_details()
    {
        await _store.UpsertDocumentAsync(Doc(1), Pages(1), [], Ct);
        await _store.UpsertDocumentAsync(Doc(2) with { DisplayTitle = "Bestiary, second printing" }, Pages(1), [], Ct);
        await AddEntriesAsync(_store, 1);

        await _store.SetEntriesAsync([new EntryDocRow(EntryOf(1), 2, EntryKind.Whole)], Ct);

        Assert.Empty(DocSearch("title:gazetteer"));
        Assert.Equal([EntryOf(1).Value], DocSearch("title:bestiary"));
        Assert.Equal(2, Connection.ExecuteScalar<long>("SELECT document_id FROM entry_doc WHERE entry_id = @id", new { id = EntryOf(1).Value }));
    }

    [Fact]
    public async Task Fingerprints_find_the_documents_that_share_pages()
    {
        foreach (var id in new long[] { 1, 2, 3 }) await _store.UpsertDocumentAsync(Doc(id), Pages(3), [], Ct);
        await _store.SetFingerprintsAsync(1, ["aa", "bb", null], Ct);
        await _store.SetFingerprintsAsync(2, ["aa", "bb", "cc"], Ct);
        await _store.SetFingerprintsAsync(3, ["bb", "bb", "dd"], Ct);

        Assert.Equal(["aa", "bb", null], await _queries.GetFingerprintsAsync(1, Ct));
        Assert.Equal([(2L, 2, 3), (3L, 1, 2)], await _queries.GetSharingPagesAsync(1, Ct));
        Assert.Empty(await _queries.GetSharingPagesAsync(99, Ct));
    }

    [Fact]
    public async Task Removing_an_entry_takes_it_out_of_the_library_and_search()
    {
        await _store.UpsertDocumentAsync(Doc(1), Pages(1), [], Ct);
        await _store.UpsertDocumentAsync(Doc(2) with { DisplayTitle = "Bestiary" }, Pages(1), [], Ct);
        await AddEntriesAsync(_store, 1, 2);

        await _store.RemoveEntriesAsync([EntryOf(1)], Ct);

        Assert.Empty(DocSearch("title:gazetteer"));
        Assert.Equal([EntryOf(2).Value], DocSearch("title:bestiary"));
        Assert.Equal([2L], Connection.Query<long>("SELECT document_id FROM entry_doc"));
    }

    [Fact]
    public async Task A_cover_is_recorded_against_its_document()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(1), [], Ct);

        await _store.SetCoverAsync(1, "hash1.jpg", Ct);

        Assert.Equal("hash1.jpg", await _queries.GetCoverAsync(1, Ct));
    }
}
