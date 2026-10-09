using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core.Layout;

namespace Bibliotaph.App.Views;

/// <summary>
/// A pop-out reader window. It opens at <c>start</c> (in pixels, so a monitor with another scale gets the same
/// rectangle), and keeps track of where its restored rectangle is, for the next pop-out to open there.
/// </summary>
public partial class ReaderWindow
{
    readonly WindowPlacement? _start;
    ScreenRect? _restored;
    bool _maximized;

    public ReaderWindow(ViewerViewModel model, WindowPlacement? start)
    {
        InitializeComponent();
        Model = model;
        DataContext = model;
        Title = string.IsNullOrWhiteSpace(model.Title) ? "Bibliotaph" : model.Title;
        _start = start;
        // The first pop-out opens in the middle of the screen; later ones where the last one was.
        WindowStartupLocation = start is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.Manual;
        if (start is null) Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.92);
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => Close()));
        CommandBindings.Add(new CommandBinding(ShellCommands.FocusSearch, (_, _) => ReaderWindows.FocusMainSearch()));
        LocationChanged += (_, _) => TrackRestored();
        SizeChanged += (_, _) => TrackRestored();
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _maximized = WindowState == WindowState.Maximized;
        };
    }

    public ViewerViewModel Model { get; }

    /// <summary>Where the window was as it closed; null until then.</summary>
    public WindowPlacement? Placement { get; private set; }

    IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_start is { } start)
        {
            // Twice: the first move can land on a monitor with another scale, and Windows then resizes the window
            // to keep its size in device-independent units. The second sets the size in pixels on that monitor.
            ScreenAreas.Move(Handle, start.Bounds);
            ScreenAreas.Move(Handle, start.Bounds);
            if (start.Maximized) WindowState = WindowState.Maximized;
        }
        TrackRestored();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        // A minimized window comes back as it was before, so it is saved that way too.
        var maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _maximized);
        if (_restored is { } restored) Placement = new WindowPlacement(restored, maximized);
    }

    /// <summary>The rectangle to restore to: only taken while the window is neither maximized nor minimized.</summary>
    void TrackRestored()
    {
        var handle = Handle;
        if (handle == IntPtr.Zero || ScreenAreas.IsZoomedOrIconic(handle)) return;
        _restored = ScreenAreas.Bounds(handle) ?? _restored;
    }
}
