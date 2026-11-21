using System.Runtime.CompilerServices;

namespace Bibliotaph.App.Services;

/// <summary>Where work waits among the UI thread's other work.</summary>
public enum UiPriority
{
    /// <summary>Ahead of everything but input, as a reply to typing is.</summary>
    Input,

    /// <summary>In turn with other posted work.</summary>
    Normal,

    /// <summary>Behind input and layout, so a stream of updates never makes the window lag.</summary>
    Background,
}

/// <summary>
/// The thread a view model's state belongs to. View models post to it rather than to a Dispatcher so that they hold no
/// WPF types and a window of another kind can drive them; a test runs the work inline.
/// </summary>
public interface IUiThread
{
    /// <summary>True when the caller is on the UI thread.</summary>
    bool IsCurrent { get; }

    /// <summary>Queues <paramref name="work"/> on the UI thread, even when the caller is already on it.</summary>
    void Post(Action work, UiPriority priority = UiPriority.Normal);

    /// <summary>
    /// Queues work that isn't waited for, logged if it fails (<see cref="BackgroundWork.FireAndForget"/>). It is a
    /// member rather than an extension so that an async lambda binds here, not to the Action overload as async void.
    /// </summary>
    void Post(Func<Task> work, UiPriority priority = UiPriority.Normal, [CallerArgumentExpression(nameof(work))] string what = "");

    /// <summary>Runs <paramref name="work"/> on the UI thread: at once when the caller is on it, otherwise in turn.</summary>
    Task RunAsync(Func<Task> work);
}

/// <summary>A UI thread for tests and work with no window: everything runs at once, on the caller's thread.</summary>
public sealed class ImmediateUiThread : IUiThread
{
    public static readonly ImmediateUiThread Instance = new();

    public bool IsCurrent => true;

    public void Post(Action work, UiPriority priority = UiPriority.Normal) => work();

    public void Post(Func<Task> work, UiPriority priority = UiPriority.Normal, [CallerArgumentExpression(nameof(work))] string what = "") =>
        work().FireAndForget(what);

    public Task RunAsync(Func<Task> work) => work();
}
