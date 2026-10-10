using System.ComponentModel;
using System.Windows;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>
/// Run mode (slice 3d). Full screen (F11) is the window's business, so it is done here: the frame goes and the window
/// fills the screen, and both come back as they were when it's left or the page goes.
/// </summary>
public partial class RunSessionView
{
    RunSessionViewModel? _model;
    (WindowStyle Style, WindowState State, ResizeMode Resize)? _before;

    public RunSessionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as RunSessionViewModel);
        Loaded += (_, _) => Attach(DataContext as RunSessionViewModel);
        Unloaded += (_, _) =>
        {
            Attach(null);
            ShowFullScreen(false);
        };
    }

    void Attach(RunSessionViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        _model?.PropertyChanged -= OnModelChanged;
        _model = model;
        _model?.PropertyChanged += OnModelChanged;
        ShowFullScreen(_model?.IsFullScreen == true);
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RunSessionViewModel.IsFullScreen)) ShowFullScreen(_model?.IsFullScreen == true);
    }

    void ShowFullScreen(bool on)
    {
        if ((Window.GetWindow(this) ?? Application.Current?.MainWindow) is not { } window) return;
        if (on && _before is null)
        {
            _before = (window.WindowStyle, window.WindowState, window.ResizeMode);
            // Normal first, so a window that was already maximized covers the taskbar once maximized again without its frame.
            window.WindowState = WindowState.Normal;
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
        }
        else if (!on && _before is { } before)
        {
            _before = null;
            window.WindowStyle = before.Style;
            window.ResizeMode = before.Resize;
            window.WindowState = before.State;
        }
    }
}
