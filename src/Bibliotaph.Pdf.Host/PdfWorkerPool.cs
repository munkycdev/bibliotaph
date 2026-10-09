using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Pdf.Host;

public sealed record PdfWorkerPoolOptions
{
    public string? WorkerPath { get; init; }

    /// <summary>The viewer renders large art-heavy pages; the spike peaked at 803 MB on a 782 MB book.</summary>
    public long ViewerMemoryLimitBytes { get; init; } = 1536L * 1024 * 1024;
    public long IndexMemoryLimitBytes { get; init; } = 1024L * 1024 * 1024;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The main reader and two pop-outs get a worker each; more readers share. Three at the cap is about 4.5 GB.</summary>
    public int MaxViewerWorkers { get; init; } = 3;
}

/// <summary>
/// The PdfWorker processes, each in its own job object: one for indexing, and viewer workers handed out to readers as
/// leases. A reader never shares a worker with indexing, so a slow or hostile book being indexed can't stall the page
/// being read; and each reader gets a worker of its own up to <see cref="PdfWorkerPoolOptions.MaxViewerWorkers"/>,
/// because a worker answers one request at a time and a slow page in one window would otherwise hold up the others.
/// A worker process starts with its first request, so the app starts none until it needs one; a viewer worker stops
/// when its last lease is released. All workers share one <see cref="PoisonTracker"/>.
/// </summary>
public sealed class PdfWorkerPool(PdfWorkerPoolOptions options, ILoggerFactory? loggers = null) : IAsyncDisposable
{
    readonly ILoggerFactory _loggers = loggers ?? NullLoggerFactory.Instance;
    readonly Lock _lock = new();
    readonly ViewerLeases<WorkerClient> _viewers = new(options.MaxViewerWorkers);
    WorkerClient? _index;
    int _viewersStarted;
    bool _disposed;

    public PoisonTracker Poison { get; } = new();

    /// <summary>The index worker, created on first use and kept until the pool is disposed.</summary>
    public WorkerClient Index
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _index ??= new WorkerClient(new WorkerOptions
                {
                    Name = "index worker",
                    WorkerPath = options.WorkerPath,
                    DefaultTimeout = options.DefaultTimeout,
                    MemoryLimitBytes = options.IndexMemoryLimitBytes,
                }, Poison, _loggers.CreateLogger<WorkerClient>());
            }
        }
    }

    /// <summary>How many viewer workers have leases now.</summary>
    public int ViewerWorkerCount
    {
        get
        {
            lock (_lock) return _viewers.Workers.Count;
        }
    }

    /// <summary>
    /// A viewer worker for one reader until the lease is disposed: one of its own while fewer than the limit are
    /// leased, otherwise the least-busy one, shared. Dispose the lease only once the reader's documents are closed.
    /// </summary>
    public ViewerLease LeaseViewer()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var worker = _viewers.Acquire(() => new WorkerClient(new WorkerOptions
            {
                Name = $"viewer worker {++_viewersStarted}",
                WorkerPath = options.WorkerPath,
                DefaultTimeout = options.DefaultTimeout,
                MemoryLimitBytes = options.ViewerMemoryLimitBytes,
            }, Poison, _loggers.CreateLogger<WorkerClient>()));
            return new ViewerLease(this, worker);
        }
    }

    internal async ValueTask ReleaseAsync(WorkerClient worker)
    {
        bool last;
        lock (_lock) last = !_disposed && _viewers.Release(worker);
        // Its process exits now; a reader that comes along later gets a fresh worker.
        if (last) await worker.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        WorkerClient[] clients;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            clients = _index is null ? [.. _viewers.Workers] : [.. _viewers.Workers, _index];
            _index = null;
        }
        foreach (var client in clients) await client.DisposeAsync();
    }
}

/// <summary>A reader's hold on a viewer worker. Disposing it gives the worker back; the last lease stops it.</summary>
public sealed class ViewerLease(PdfWorkerPool pool, WorkerClient worker) : IAsyncDisposable
{
    int _released;

    public WorkerClient Worker { get; } = worker;

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? pool.ReleaseAsync(Worker) : ValueTask.CompletedTask;
}
