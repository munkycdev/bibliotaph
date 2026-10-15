using Bibliotaph.Classification;

namespace Bibliotaph.Processing;

/// <summary>
/// Knows when the AI is being used for something a person is waiting on: cataloging a book, Ask, a plain-English
/// search, a test from Settings. Reading whole books is the one thing that never counts: it gives way to all of these,
/// stopping its request the moment one starts (<see cref="TryQuiet"/>) and waiting until the AI has been left alone for
/// <see cref="QuietPeriod"/>, so whatever the user asks is answered as quickly as if no scan were running.
/// </summary>
/// <param name="clock">The clock the quiet period is measured on.</param>
/// <param name="quiet">How long after the last request a scan waits before it goes on; half a minute unless given.</param>
public sealed class ModelActivity(TimeProvider? clock = null, TimeSpan? quiet = null) : IDisposable
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly Lock _lock = new();
    int _busy;
    DateTimeOffset _lastUsed = DateTimeOffset.MinValue;
    CancellationTokenSource _yield = new();

    public void Dispose()
    {
        lock (_lock) _yield.Dispose();
    }

    /// <summary>How long the AI must have been left alone before a scan goes on.</summary>
    public TimeSpan QuietPeriod { get; } = quiet ?? TimeSpan.FromSeconds(30);

    /// <summary>Raised when a request a person was waiting on ends, so a waiting scan can look again once it is quiet.</summary>
    public event EventHandler? Finished;

    /// <summary>Whether a request a person is waiting on is being answered now.</summary>
    public bool IsBusy
    {
        get { lock (_lock) return _busy > 0; }
    }

    /// <summary>
    /// Marks a request as started, until the result is disposed. Whatever a scan is asking is cancelled now: the model
    /// answers one thing at a time, and the person shouldn't wait behind a chapter.
    /// </summary>
    public IDisposable Begin()
    {
        CancellationTokenSource yielded;
        lock (_lock)
        {
            _busy++;
            _lastUsed = _clock.GetUtcNow();
            yielded = _yield;
            _yield = new CancellationTokenSource();
        }
        // Not disposed: a scan's linked token may still hold it, and it is dropped with that.
        yielded.Cancel();
        return new Use(this);
    }

    /// <summary>
    /// Whether a scan may ask the model now: nothing a person is waiting on is running or has run in the last
    /// <see cref="QuietPeriod"/>. If so, <paramref name="yield"/> is cancelled when one starts; if not,
    /// <paramref name="wait"/> says how long to wait before looking again.
    /// </summary>
    public bool TryQuiet(out CancellationToken yield, out TimeSpan wait)
    {
        lock (_lock)
        {
            yield = _yield.Token;
            if (_busy > 0)
            {
                wait = QuietPeriod;
                return false;
            }
            var left = _lastUsed == DateTimeOffset.MinValue ? TimeSpan.Zero : _lastUsed + QuietPeriod - _clock.GetUtcNow();
            wait = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            return wait == TimeSpan.Zero;
        }
    }

    void End()
    {
        lock (_lock)
        {
            _busy--;
            _lastUsed = _clock.GetUtcNow();
        }
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A model client whose every request counts as the user's.</summary>
    public IModelClient Track(IModelClient client) => client is TrackedModelClient ? client : new TrackedModelClient(client, this);

    /// <summary>An embedding client whose every request counts as the user's.</summary>
    public IEmbeddingClient Track(IEmbeddingClient client) => client is TrackedEmbeddingClient ? client : new TrackedEmbeddingClient(client, this);

    sealed class Use(ModelActivity activity) : IDisposable
    {
        int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) activity.End();
        }
    }

    sealed class TrackedModelClient(IModelClient inner, ModelActivity activity) : IModelClient
    {
        // Listing models doesn't use one, so a scan carries on.
        public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);

        public async Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
        {
            using (activity.Begin()) return await inner.CompleteAsync(request, ct);
        }
    }

    sealed class TrackedEmbeddingClient(IEmbeddingClient inner, ModelActivity activity) : IEmbeddingClient
    {
        public async Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            using (activity.Begin()) return await inner.EmbedAsync(model, texts, ct);
        }
    }
}
