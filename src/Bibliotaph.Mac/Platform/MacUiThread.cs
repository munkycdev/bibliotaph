using System.Runtime.CompilerServices;
using Bibliotaph.Presentation.Services;
using CoreFoundation;
using Foundation;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// AppKit's main thread, which every window and view runs on. The main queue has no priorities, so posts run in the
/// order they were made whatever priority they ask for.
/// </summary>
public sealed class MacUiThread : IUiThread
{
    public bool IsCurrent => NSThread.IsMain;

    public void Post(Action work, UiPriority priority = UiPriority.Normal) => DispatchQueue.MainQueue.DispatchAsync(work);

    public void Post(Func<Task> work, UiPriority priority = UiPriority.Normal, [CallerArgumentExpression(nameof(work))] string what = "") =>
        Post(() => work().FireAndForget(what), priority);

    public Task RunAsync(Func<Task> work)
    {
        if (IsCurrent) return work();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatchQueue.MainQueue.DispatchAsync(async () =>
        {
            try
            {
                await work();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }
}
