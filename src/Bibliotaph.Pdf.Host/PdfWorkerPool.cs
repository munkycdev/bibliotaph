using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Pdf.Host;

/// <summary>
/// The viewer never shares a worker with indexing, so a slow or hostile book being indexed can't
/// stall the page being read.
/// </summary>
public enum WorkerSlot
{
    Viewer,
    Index,
}

public sealed record PdfWorkerPoolOptions
{
    public string? WorkerPath { get; init; }

    /// <summary>The viewer renders large art-heavy pages; the spike peaked at 803 MB on a 782 MB book.</summary>
    public long ViewerMemoryLimitBytes { get; init; } = 1536L * 1024 * 1024;
    public long IndexMemoryLimitBytes { get; init; } = 1024L * 1024 * 1024;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// One viewer slot and one index slot, each its own PdfWorker process in its own job object.
/// Slots are created on first use, so the app starts no worker until it needs one.
/// Both slots share one <see cref="PoisonTracker"/>.
/// </summary>
public sealed class PdfWorkerPool(PdfWorkerPoolOptions options, ILoggerFactory? loggers = null) : IAsyncDisposable
{
    readonly ILoggerFactory _loggers = loggers ?? NullLoggerFactory.Instance;
    readonly Lock _lock = new();
    readonly Dictionary<WorkerSlot, WorkerClient> _clients = [];
    bool _disposed;

    public PoisonTracker Poison { get; } = new();

    public WorkerClient this[WorkerSlot slot]
    {
        get
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!_clients.TryGetValue(slot, out var client))
                {
                    client = new WorkerClient(OptionsFor(slot), Poison, _loggers.CreateLogger<WorkerClient>());
                    _clients[slot] = client;
                }
                return client;
            }
        }
    }

    WorkerOptions OptionsFor(WorkerSlot slot) => new()
    {
        Name = slot == WorkerSlot.Viewer ? "viewer worker" : "index worker",
        WorkerPath = options.WorkerPath,
        DefaultTimeout = options.DefaultTimeout,
        MemoryLimitBytes = slot == WorkerSlot.Viewer ? options.ViewerMemoryLimitBytes : options.IndexMemoryLimitBytes,
    };

    public async ValueTask DisposeAsync()
    {
        WorkerClient[] clients;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            clients = [.. _clients.Values];
            _clients.Clear();
        }
        foreach (var client in clients) await client.DisposeAsync();
    }
}
