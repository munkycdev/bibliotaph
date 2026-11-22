using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Bibliotaph.Presentation.Services;

namespace Bibliotaph.App.Services;

/// <summary>The WPF app's UI thread: its one Dispatcher, at the priorities the view models used to pass it.</summary>
public sealed class DispatcherUiThread(Dispatcher dispatcher) : IUiThread
{
    public bool IsCurrent => dispatcher.CheckAccess();

    public void Post(Action work, UiPriority priority = UiPriority.Normal) =>
        _ = dispatcher.InvokeAsync(work, Priority(priority));

    public void Post(Func<Task> work, UiPriority priority = UiPriority.Normal, [CallerArgumentExpression(nameof(work))] string what = "") =>
        _ = dispatcher.InvokeAsync(() => work().FireAndForget(what), Priority(priority));

    public Task RunAsync(Func<Task> work) => IsCurrent ? work() : dispatcher.InvokeAsync(work).Task.Unwrap();

    internal static DispatcherPriority Priority(UiPriority priority) => priority switch
    {
        UiPriority.Input => DispatcherPriority.Input,
        UiPriority.Background => DispatcherPriority.Background,
        _ => DispatcherPriority.Normal,
    };
}
