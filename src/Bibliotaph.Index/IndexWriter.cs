using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Index;

/// <summary>
/// The single writer for index.db. Callers queue writes; the writer runs them in batched transactions
/// on one connection, which keeps SQLite fast under a busy indexer and never contends for the write lock.
/// A stub in slice 0: the pipeline stages that use it arrive in slice 1.
/// </summary>
public sealed class IndexWriter(IndexDatabase database, ILogger<IndexWriter>? log = null) : BackgroundService
{
    public const int MaxBatch = 256;

    readonly ILogger _log = log ?? NullLogger<IndexWriter>.Instance;
    readonly Channel<WriteItem> _queue = Channel.CreateUnbounded<WriteItem>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Number of transactions committed so far, for tests and diagnostics.</summary>
    public int CommittedBatches { get; private set; }

    /// <summary>Queues a write and completes when its transaction has committed.</summary>
    public Task WriteAsync(Action<SqliteConnection, SqliteTransaction> write, CancellationToken ct = default)
    {
        var item = new WriteItem(write, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_queue.Writer.TryWrite(item)) throw new InvalidOperationException("The index writer has stopped.");
        return item.Done.Task.WaitAsync(ct);
    }

    // On the thread pool, never the caller's synchronization context: in the app that would be the UI thread.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);

    async Task RunAsync(CancellationToken stoppingToken)
    {
        using var connection = database.OpenWrite();
        var batch = new List<WriteItem>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
                Drain(connection, batch);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Shutting down: finish what was queued, then refuse anything new.
        _queue.Writer.TryComplete();
        Drain(connection, batch);
    }

    void Drain(SqliteConnection connection, List<WriteItem> batch)
    {
        while (true)
        {
            batch.Clear();
            while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var item)) batch.Add(item);
            if (batch.Count == 0) return;
            Commit(connection, batch);
        }
    }

    void Commit(SqliteConnection connection, List<WriteItem> batch)
    {
        try
        {
            RunInTransaction(connection, batch);
            foreach (var item in batch) item.Done.TrySetResult();
        }
        catch (Exception ex) when (batch.Count > 1)
        {
            // One bad write must not sink its neighbours: retry each on its own.
            _log.LogWarning(ex, "Index batch of {Count} failed; retrying writes one by one", batch.Count);
            foreach (var item in batch) Commit(connection, [item]);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Index write failed");
            batch[0].Done.TrySetException(ex);
        }
    }

    void RunInTransaction(SqliteConnection connection, List<WriteItem> batch)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var item in batch) item.Write(connection, transaction);
        transaction.Commit();
        CommittedBatches++;
    }

    sealed record WriteItem(Action<SqliteConnection, SqliteTransaction> Write, TaskCompletionSource Done);
}
