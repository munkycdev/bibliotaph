using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core.Search;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App;

public partial class MainWindow : Window
{
    readonly SearchGuideViewModel _guide;
    readonly AboutBox _about;
    readonly IndexingService _indexing;
    readonly ILogger<MainWindow> _log;

    /// <summary>Set while a pick rewrites the box, so the box's own change events don't steer the guide halfway through.</summary>
    bool _applyingGuide;

    public MainWindow(ShellViewModel shell, SearchGuideViewModel guide, AboutBox about, ShortcutsBox shortcuts, IndexingService indexing,
        ILogger<MainWindow> log)
    {
        InitializeComponent();
        _indexing = indexing;
        _log = log;
        SourceInitialized += (_, _) => WatchDrives();
        // Before the window's DataContext, so the guide's bindings never look for its properties on the shell.
        _guide = guide;
        SearchGuide.DataContext = guide;
        DataContext = shell;
        _about = about;
        CommandBindings.Add(new CommandBinding(ShellCommands.FocusSearch, (_, _) => FocusSearch()));
        CommandBindings.Add(new CommandBinding(ShellCommands.ShowShortcuts, (_, _) => shortcuts.Show(this)));
        HookUpSearchGuide();
    }

    void FocusSearch()
    {
        Search.Focus();
        Search.SelectAll();
    }

    // The mouse back button. MouseBinding has no gesture for the X buttons, so it is handled here.
    protected override void OnPreviewMouseUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseUp(e);
        if (e.ChangedButton != MouseButton.XButton1 || DataContext is not ShellViewModel shell) return;
        if (shell.GoBackCommand.CanExecute(null)) shell.GoBackCommand.Execute(null);
        e.Handled = true;
    }

    // Enter searches at once; Esc clears the search and leaves the box. With the field guide open, the arrow keys
    // move its highlight, Enter or Tab picks it, and Esc closes the guide first.
    void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (GuideKey(e))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            _guide.Close();
            shell.SubmitSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            shell.ClearSearchCommand.Execute(null);
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
            e.Handled = true;
        }
    }

    // ---- The search box's field guide -----------------------------------------------------------------------------

    void HookUpSearchGuide()
    {
        Search.GotKeyboardFocus += (_, _) => _guide.Focused(Search.Text, Search.CaretIndex);
        Search.LostKeyboardFocus += (_, _) =>
            // Wait for focus to settle: it may only be passing through, and a click on the guide leaves it in the box.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!Search.IsKeyboardFocused) _guide.Close();
            });
        // Only typing steers the guide: a page clearing the search changes the text too, with the box elsewhere.
        Search.TextChanged += (_, _) =>
        {
            if (!_applyingGuide && Search.IsKeyboardFocused) _guide.TextChanged(Search.Text, Search.CaretIndex);
        };
        Search.SelectionChanged += (_, _) =>
        {
            if (!_applyingGuide && Search.IsKeyboardFocused) _guide.CaretMoved(Search.Text, Search.CaretIndex);
        };
        // A popup doesn't move with its window, so it closes instead. A click elsewhere closes it too, even one on
        // something that doesn't take focus. (Clicks in the popup route here through it, and keep it open.)
        LocationChanged += (_, _) => _guide.Close();
        PreviewMouseDown += (_, _) =>
        {
            if (!Search.IsMouseOver && SearchGuide.Child?.IsMouseOver != true) _guide.Close();
        };
        _guide.Edited += (_, edit) => ApplyGuideEdit(edit);
        _guide.PropertyChanged += OnGuideChanged;
    }

    bool GuideKey(KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Space && modifiers == ModifierKeys.Control)
        {
            _guide.Request(Search.Text, Search.CaretIndex);
            return true;
        }
        if (modifiers != ModifierKeys.None) return false;
        switch (e.Key)
        {
            case Key.Down when !_guide.IsOpen:
                _guide.Request(Search.Text, Search.CaretIndex);
                return _guide.IsOpen;
            case Key.Down:
                return _guide.Move(1);
            case Key.Up:
                return _guide.Move(-1);
            case Key.Enter or Key.Tab:
                return _guide.PickHighlighted();
            case Key.Escape when _guide.IsOpen:
                _guide.Close();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Puts a pick in the box, keeping focus there, then lets the guide look at the new cursor position.</summary>
    void ApplyGuideEdit(GuideEdit edit)
    {
        _applyingGuide = true;
        try
        {
            Search.Text = edit.Text;
            Search.CaretIndex = edit.Caret;
            Search.Focus();
        }
        finally
        {
            _applyingGuide = false;
        }
        _guide.TextChanged(Search.Text, Search.CaretIndex);
    }

    /// <summary>
    /// Shows the highlighted row and tells screen readers about it: the box's help text names it, and the live region
    /// announces it, because focus stays in the box while the highlight moves.
    /// </summary>
    void OnGuideChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchGuideViewModel.Highlighted)) return;
        var announcement = _guide.Announcement;
        AutomationProperties.SetHelpText(Search, announcement);
        SearchGuideAnnouncement.Text = announcement;
        AutomationProperties.SetName(SearchGuideAnnouncement, announcement);
        if (_guide.Highlighted is { } row && SearchGuideRows.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container)
            container.BringIntoView();
        if (announcement.Length == 0) return;
        var peer = UIElementAutomationPeer.FromElement(SearchGuideAnnouncement) ?? UIElementAutomationPeer.CreatePeerForElement(SearchGuideAnnouncement);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
