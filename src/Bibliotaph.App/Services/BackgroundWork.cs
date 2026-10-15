using System.Runtime.CompilerServices;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>Work a view model starts and doesn't wait for.</summary>
public static class BackgroundWork
{
    /// <summary>
    /// Lets <paramref name="task"/> run on without waiting for it, and logs it if it fails. A discarded task's exception
    /// is otherwise seen by nobody: it isn't thrown anywhere, and the unobserved-task log only notices it at the next
    /// garbage collection, if ever. Cancellation is the caller's own doing and isn't logged.
    /// </summary>
    public static void FireAndForget(this Task task, [CallerArgumentExpression(nameof(task))] string what = "")
    {
        ArgumentNullException.ThrowIfNull(task);
        task.ContinueWith(
            static (done, state) =>
            {
                var failure = done.Exception!.InnerExceptions is [var single] ? single : done.Exception;
                Log.Error(failure, "{What} failed in the background", state);
            },
            what, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
