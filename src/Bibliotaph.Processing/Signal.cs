namespace Bibliotaph.Processing;

/// <summary>An auto-reset signal: any number of Sets before a wait wake it once.</summary>
internal sealed class Signal : IDisposable
{
    readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Set()
    {
        try
        {
            _semaphore.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _semaphore.WaitAsync(timeout, ct);

    public void Dispose() => _semaphore.Dispose();
}
