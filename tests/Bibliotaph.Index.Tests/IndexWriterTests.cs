using Dapper;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Index.Tests;

public sealed class IndexWriterTests : IAsyncLifetime
{
    readonly IndexDatabase _db = IndexDatabase.InMemory($"writer-{Guid.NewGuid():N}");
    SqliteConnection _keepAlive = null!;
    IndexWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_db.ConnectionString);
        _keepAlive.Open();
        await _db.InitializeAsync();
        _writer = new IndexWriter(_db);
    }

    public async ValueTask DisposeAsync()
    {
        _writer.Dispose();
        await _keepAlive.DisposeAsync();
    }

    static Action<SqliteConnection, SqliteTransaction> InsertPage(int doc, int page) => (c, t) =>
        c.Execute("INSERT INTO page (document_id, pdf_page, width_pt, height_pt, text) VALUES (@doc, @page, 612, 792, 'text')",
            new { doc, page }, t);

    long PageCount() => _keepAlive.ExecuteScalar<long>("SELECT count(*) FROM page");

    [Fact]
    public async Task Writes_queued_together_commit_in_one_transaction()
    {
        // Queue before the writer starts, so all of them are waiting when it drains.
        var writes = Enumerable.Range(0, 50).Select(i => _writer.WriteAsync(InsertPage(1, i))).ToList();
        await _writer.StartAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(writes);

        Assert.Equal(50, PageCount());
        Assert.Equal(1, _writer.CommittedBatches);
    }

    [Fact]
    public async Task A_failing_write_does_not_sink_the_rest_of_its_batch()
    {
        var good1 = _writer.WriteAsync(InsertPage(1, 0));
        var bad = _writer.WriteAsync((c, t) => c.Execute("INSERT INTO no_such_table VALUES (1)", transaction: t));
        var good2 = _writer.WriteAsync(InsertPage(1, 1));
        await _writer.StartAsync(TestContext.Current.CancellationToken);

        await good1;
        await good2;
        await Assert.ThrowsAsync<SqliteException>(() => bad);
        Assert.Equal(2, PageCount());
    }

    [Fact]
    public async Task Stopping_finishes_queued_writes_first()
    {
        await _writer.StartAsync(TestContext.Current.CancellationToken);
        var writes = Enumerable.Range(0, 20).Select(i => _writer.WriteAsync(InsertPage(2, i))).ToList();

        await _writer.StopAsync(TestContext.Current.CancellationToken);

        await Task.WhenAll(writes);
        Assert.Equal(20, PageCount());
        Assert.Throws<InvalidOperationException>(() => { _ = _writer.WriteAsync(InsertPage(3, 0)); });
    }

    [Fact]
    public async Task Queue_summary_counts_pending_and_failed_jobs()
    {
        await _writer.StartAsync(TestContext.Current.CancellationToken);
        await _writer.WriteAsync((c, t) => c.Execute(
            """
            INSERT INTO job (document_id, content_hash, stage, stage_version, status, created_utc) VALUES
                (1, 'a', 'Text', 1, 'pending', '2026-10-08T00:00:00Z'),
                (1, 'a', 'Ocr', 1, 'leased', '2026-10-08T00:00:00Z'),
                (2, 'b', 'Text', 1, 'failed', '2026-10-08T00:00:00Z'),
                (3, 'c', 'Text', 1, 'done', '2026-10-08T00:00:00Z')
            """, transaction: t), TestContext.Current.CancellationToken);

        var summary = await new IndexQueries(_db).GetQueueSummaryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new QueueSummary(Pending: 2, Failed: 1), summary);
        Assert.False(summary.IsIdle);
    }
}
