using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.App.Views;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Layout;
using Bibliotaph.Pdf.Host;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Makes readers, each with its request, and hosts them: in the main window's Reading route, or in pop-out windows.
/// A pop-out is a top-level window with its own taskbar button and no owner, so it doesn't minimise or stay on top
/// with the main window and can live on another monitor. Each one opens where the last one was (saved in the
/// settings table, in pixels), pulled back onto a screen if that monitor is gone. Closing the main window closes
/// every pop-out; none is reopened at the next launch.
/// </summary>
public sealed class ReaderWindows(ViewerServices viewer, INavigationService navigation, ThemeService theme, ShortcutsBox shortcuts,
    SettingsStore settings, ILogger<ReaderWindows> log)
{
    const int CascadeStep = 32;
    const int MaxCascade = 10;

    readonly List<ReaderWindow> _windows = [];
    ViewerLease? _mainLease;
    WindowPlacement? _saved;
    bool _savedRead;
    bool _closingAll;
    bool _watchingMain;
    ReaderWindow? _lastActive;

    /// <summary>The pop-outs open now, oldest first.</summary>
    public IReadOnlyList<ReaderWindow> Windows => _windows;

    /// <summary>
    /// The viewer worker the main window's readers share. Leased with the main window's first PDF and kept until the
    /// app exits, so going Back and forth in the main window doesn't restart a worker each time.
    /// </summary>
    public WorkerClient MainWorker => (_mainLease ??= viewer.Workers.LeaseViewer()).Worker;

    /// <summary>
    /// A reader for <paramref name="request"/>, or an empty one; <paramref name="poppedOut"/> for a pop-out window,
    /// <paramref name="inRunMode"/> for the one inside run mode.
    /// </summary>
    public ViewerViewModel Create(ViewerRequest? request, bool poppedOut = false, bool inRunMode = false) =>
        new(request, viewer, this, poppedOut) { InRunMode = inRunMode };

    /// <summary>Opens a book in the main window's reader, and brings the main window forward.</summary>
    public void OpenInMainWindow(ViewerRequest request)
    {
        navigation.Show(Create(request));
        if (Application.Current.MainWindow is { } main) BringForward(main);
    }

    /// <summary>Opens a book in a pop-out window of its own. The book loads once the window is showing.</summary>
    public async Task OpenInNewWindowAsync(ViewerRequest request)
    {
        var placement = await NextPlacementAsync();
        var model = Create(request, poppedOut: true);
        var window = new ReaderWindow(model, placement);
        theme.Track(window);
        window.CommandBindings.Add(new CommandBinding(ShellCommands.ShowShortcuts, (_, _) => shortcuts.Show(window)));
        window.Activated += (_, _) => _lastActive = window;
        window.Closed += async (_, _) => await OnClosedAsync(window);
        _windows.Add(window);
        WatchMainWindow();
        window.Show();
        _ = LoadAsync(model);
    }

    /// <summary>Moves the main window's book to a pop-out at the same page and zoom; the main window goes Back.</summary>
    public async Task PopOutAsync(ViewerViewModel reader)
    {
        if (reader.Here() is not { } request) return;
        if (ReferenceEquals(navigation.Current, reader) && !navigation.GoBack()) navigation.NavigateTo(Route.Library);
        await OpenInNewWindowAsync(request);
    }

    /// <summary>Moves a pop-out's book back to the main window's reader, at the same page and zoom, and closes the pop-out.</summary>
    public void ReturnToMainWindow(ViewerViewModel reader)
    {
        var window = _windows.Find(w => ReferenceEquals(w.Model, reader));
        // The main window comes forward first, so closing the pop-out doesn't hand the focus to another app.
        if (reader.Here() is { } request) OpenInMainWindow(request);
        window?.Close();
    }

    /// <summary>Ctrl+K in a pop-out: the main window comes forward with its search box focused.</summary>
    public static void FocusMainSearch()
    {
        if (Application.Current.MainWindow is not { } main) return;
        BringForward(main);
        ShellCommands.FocusSearch.Execute(null, main);
    }

    /// <summary>Closes every pop-out, remembering where the one used last was.</summary>
    public void CloseAll()
    {
        if (_windows.Count == 0) return;
        var last = _lastActive ?? _windows[^1];
        _closingAll = true;
        try
        {
            ReaderWindow[] open = [.. _windows];
            foreach (var window in open) window.Close();
        }
        finally
        {
            _closingAll = false;
        }
        // The app is about to exit, so wait for the setting to be written; it runs on the thread pool, not this thread.
        if (last.Placement is { } placement && !SaveAsync(placement).Wait(TimeSpan.FromSeconds(2)))
            log.LogWarning("Saving where the reader window was took too long");
    }

    static void BringForward(Window window)
    {
        ScreenAreas.RestoreIfMinimized(new WindowInteropHelper(window).Handle);
        window.Activate();
    }

    void WatchMainWindow()
    {
        if (_watchingMain || Application.Current.MainWindow is not { } main) return;
        _watchingMain = true;
        main.Closed += (_, _) => CloseAll();
    }

    async Task LoadAsync(ViewerViewModel model)
    {
        try
        {
            await model.LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Loading a book in a reader window failed");
        }
    }

    async Task OnClosedAsync(ReaderWindow window)
    {
        _windows.Remove(window);
        if (ReferenceEquals(_lastActive, window)) _lastActive = null;
        if (!_closingAll && window.Placement is { } placement) _ = SaveAsync(placement);
        try
        {
            // Closes the book, then gives the window's viewer worker back, which stops it if no other window uses it.
            await window.Model.CloseWindowAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogWarning(ex, "Closing a reader window's book failed");
        }
    }

    /// <summary>
    /// Where the next pop-out opens: where the last one closed, moved down and right past any pop-out already there,
    /// and pulled onto a screen. Null for the first one ever, which opens in the middle of the screen.
    /// </summary>
    async Task<WindowPlacement?> NextPlacementAsync()
    {
        if (!_savedRead)
        {
            _savedRead = true;
            try
            {
                _saved ??= WindowPlacement.Parse(await settings.GetAsync(SettingKeys.ReaderWindow));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Reading where the last reader window was failed");
            }
        }
        if (_saved is not { } saved) return null;

        var screens = ScreenAreas.WorkAreas();
        var taken = _windows.Select(w => ScreenAreas.Bounds(new WindowInteropHelper(w).Handle)).OfType<ScreenRect>().ToList();
        var placement = saved.Fit(screens);
        for (var i = 1; i <= MaxCascade && taken.Any(t => t.Left == placement.Bounds.Left && t.Top == placement.Bounds.Top); i++)
            placement = saved.Cascade(CascadeStep * i).Fit(screens);
        return placement;
    }

    Task SaveAsync(WindowPlacement placement)
    {
        _saved = placement;
        return Task.Run(async () =>
        {
            try
            {
                await settings.SetAsync(SettingKeys.ReaderWindow, placement.Format());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.LogWarning(ex, "Saving where the reader window was failed");
            }
        });
    }
}
