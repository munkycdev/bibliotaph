using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The sidebar's status line: asked of the server every few seconds while the library is busy,
/// and only now and then while it's up to date. A page that just started work (a folder added) asks for it sooner.
/// </summary>
public sealed class LibraryActivity(LibraryClient libraryApi) : IAsyncDisposable
{
    static readonly TimeSpan WhileBusy = TimeSpan.FromSeconds(3);
    static readonly TimeSpan WhileQuiet = TimeSpan.FromMinutes(1);

    readonly CancellationTokenSource _stopped = new();
    CancellationTokenSource _wait = new();
    Task? _loop;

    public LibraryStatus? Current { get; private set; }

    public event Action? Changed;

    public void Start() => _loop ??= LoopAsync();

    /// <summary>Asks again now, as after a change the status will show.</summary>
    public void Refresh() => _wait.Cancel();

    async Task LoopAsync()
    {
        while (!_stopped.IsCancellationRequested)
        {
            try
            {
                Current = await libraryApi.LibraryStatusAsync(_stopped.Token);
                Changed?.Invoke();
            }
            catch (Exception ex) when (ex is HttpRequestException or ApiProblemException)
            {
                // The server restarting, or the connection dropping: the last status stands until the next try.
            }
            catch (OperationCanceledException) when (_stopped.IsCancellationRequested)
            {
                return;
            }

            _wait = CancellationTokenSource.CreateLinkedTokenSource(_stopped.Token);
            try
            {
                await Task.Delay(Current?.Busy == true ? WhileBusy : WhileQuiet, _wait.Token);
            }
            catch (OperationCanceledException) when (!_stopped.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopped.CancelAsync();
        if (_loop is not null) await _loop;
        _stopped.Dispose();
    }
}
