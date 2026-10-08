using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Index;

/// <summary>
/// The single writer for index.db. Callers queue writes; the writer runs them in batched transactions
/// on one connection, which keeps SQLite fast under a busy indexer and never contends for the write lock.
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

    // 0 = not started, 1 = the write loop owns the queue, 2 = StopAsync drained it because the loop never ran.
    int _owner;

    // On the thread pool, never the caller's synchronization context: in the app that would be the UI thread.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() => RunAsync(stoppingToken), CancellationToken.None);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // BackgroundService starts ExecuteAsync on the thread pool and cancels it if stopped before it has run, so on
        // a busy pool a quick start-stop never runs the loop. Queued writes must still finish.
        if (Interlocked.CompareExchange(ref _owner, 2, 0) != 0) return;
        _queue.Writer.TryComplete();
        using var connection = database.OpenWrite();
        Drain(connection, [with(MaxBatch)]);
    }

    async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.CompareExchange(ref _owner, 1, 0) != 0) return;
        try
        {
            await WriteLoopAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            // Without a writer nothing queued would ever complete: fail it all, loudly, rather than hang every caller.
            _log.LogCritical(ex, "The index writer stopped");
            _queue.Writer.TryComplete(ex);
            while (_queue.Reader.TryRead(out var item)) item.Done.TrySetException(ex);
            throw;
        }
    }

    async Task WriteLoopAsync(CancellationToken stoppingToken)
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

    /// <summary>Queues a write that returns a value, such as a job lease, and completes with it once committed.</summary>
    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken ct = default)
    {
        T result = default!;
        // A statement lambda, so this binds to the Action overload rather than back to this one.
        await WriteAsync((c, t) => { result = write(c, t); }, ct);
        return result;
    }

    sealed record WriteItem(Action<SqliteConnection, SqliteTransaction> Write, TaskCompletionSource Done);
}
