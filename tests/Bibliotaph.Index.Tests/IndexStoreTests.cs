using Bibliotaph.Core;
using Dapper;

namespace Bibliotaph.Index.Tests;

public sealed class IndexStoreTests : IndexFixture
{
    IndexStore _store = null!;
    IndexQueries _queries = null!;
    JobQueue _queue = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _store = new IndexStore(Writer, Clock);
        _queries = new IndexQueries(Database);
        _queue = new JobQueue(Writer, Database, Clock);
    }

    CancellationToken Ct => TestContext.Current.CancellationToken;

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
        [.. Connection.Query<long>("SELECT rowid FROM doc_fts WHERE doc_fts MATCH @match", new { match })];

    long[] PageSearch(string match) =>
        [.. Connection.Query<long>("SELECT p.pdf_page FROM page_fts JOIN page p ON p.id = page_fts.rowid WHERE page_fts MATCH @match", new { match })];

    [Fact]
    public async Task A_probed_document_is_searchable_by_title_and_its_provisional_details()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(3), [new OutlineRow("Chapter one", 1, 0)], Ct);

        Assert.Equal([1L], DocSearch("title:gazetteer"));
        Assert.Equal([1L], DocSearch("provisional:cartographer"));
        Assert.Equal([1L], DocSearch("provisional:maps"));
        Assert.Equal(3, Connection.ExecuteScalar<long>("SELECT count(*) FROM page WHERE document_id = 1"));
        Assert.Equal("Chapter one", Connection.ExecuteScalar<string>("SELECT title FROM outline WHERE document_id = 1"));
    }

    [Fact]
    public async Task Probing_again_keeps_extracted_text_and_drops_pages_that_are_gone()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(3), [], Ct);
        await _store.SetPageTextAsync(1, [new PageTextRow(0, "the owlbear sleeps", "pdf", 1, false)], Ct);

        await _store.UpsertDocumentAsync(Doc() with { DisplayTitle = "Gazetteer, revised" }, Pages(2), [], Ct);

        Assert.Equal([0L], PageSearch("owlbear"));
        Assert.Equal(2, Connection.ExecuteScalar<long>("SELECT count(*) FROM page WHERE document_id = 1"));
        Assert.Equal([1L], DocSearch("title:revised"));
        Assert.Equal(1, Connection.ExecuteScalar<long>("SELECT count(*) FROM doc_fts"));
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
    }

    [Fact]
    public async Task Progress_and_attention_follow_the_stage_statuses()
    {
        await _store.UpsertDocumentAsync(Doc(1), Pages(1), [], Ct);
        await _store.UpsertDocumentAsync(Doc(2) with { DisplayTitle = "Locked book" }, Pages(1), [], Ct);
        await _queue.EnqueueAsync(1, "hash1", Stage.Text, ct: Ct);
        await _queue.EnqueueAsync(2, "hash2", Stage.Text, ct: Ct);
        var lane = new[] { Stage.Text };
        await _queue.CompleteAsync((await _queue.LeaseAsync(lane, "t", TimeSpan.FromMinutes(1), Ct))!, ct: Ct);
        await _queue.BlockAsync((await _queue.LeaseAsync(lane, "t", TimeSpan.FromMinutes(1), Ct))!, "Needs a password", Ct);

        var progress = await _queries.GetProgressAsync(Ct);
        var attention = Assert.Single(await _queries.GetAttentionAsync(Ct));

        Assert.Equal(new IndexProgress(2, 1, 0, 1, 0), progress);
        Assert.Equal(("Locked book", StageStatus.Blocked, "Needs a password"), (attention.Title, attention.Status, attention.Reason));
    }

    [Fact]
    public async Task A_cover_is_recorded_against_its_document()
    {
        await _store.UpsertDocumentAsync(Doc(), Pages(1), [], Ct);

        await _store.SetCoverAsync(1, "hash1.jpg", Ct);

        Assert.Equal("hash1.jpg", await _queries.GetCoverAsync(1, Ct));
    }
}
