using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
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

    /// <summary>Where the mouse went down on a pin, until it moves far enough to drag it or comes up.</summary>
    Point? _pinDragStart;

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
        shell.FocusPinRequested += (_, target) => FocusPin(target);
        shell.PlainSearch.PropertyChanged += OnPlainSearchChanged;
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
    // move its highlight, Enter or Tab picks it, and Esc closes the guide first. In Ask mode, Enter reads the request,
    // Ctrl+Enter answers it from the library's pages, and Esc leaves Ask, keeping the text.
    void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell) return;
        if (shell.PlainSearch.IsOn)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                _ = shell.PlainSearch.AnswerAsync(shell.SearchText);
                e.Handled = true;
                return;
            }
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Enter)
            {
                _ = shell.PlainSearch.InterpretAsync(shell.SearchText);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                shell.PlainSearch.IsOn = false;
                e.Handled = true;
            }
            return;
        }
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

    /// <summary>The guide offers search syntax, which a request in plain English doesn't use, so it stays shut in Ask mode.</summary>
    bool InPlainSearch => DataContext is ShellViewModel { PlainSearch.IsOn: true };

    void HookUpSearchGuide()
    {
        Search.GotKeyboardFocus += (_, _) =>
        {
            if (!InPlainSearch) _guide.Focused(Search.Text, Search.CaretIndex);
        };
        Search.LostKeyboardFocus += (_, _) =>
            // Wait for focus to settle: it may only be passing through, and a click on the guide leaves it in the box.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!Search.IsKeyboardFocused) _guide.Close();
            });
        // Only typing steers the guide: a page clearing the search changes the text too, with the box elsewhere.
        Search.TextChanged += (_, _) =>
        {
            if (!_applyingGuide && !InPlainSearch && Search.IsKeyboardFocused) _guide.TextChanged(Search.Text, Search.CaretIndex);
        };
        Search.SelectionChanged += (_, _) =>
        {
            if (!_applyingGuide && !InPlainSearch && Search.IsKeyboardFocused) _guide.CaretMoved(Search.Text, Search.CaretIndex);
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

    /// <summary>Ask switched on puts the keyboard in the box for the request, with the guide shut.</summary>
    void OnPlainSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlainSearchViewModel.IsOn) || !InPlainSearch) return;
        _guide.Close();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            Search.Focus();
            Search.SelectAll();
        });
    }

    // ---- Pins: dragging to a new place, and the keyboard staying on a pin that moved ----

    void FocusPin(PinTarget target) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (DataContext is ShellViewModel shell && shell.Pinned.FirstOrDefault(p => p.Target == target) is { } pin
                && Pinned.ItemContainerGenerator.ContainerFromItem(pin) is System.Windows.Controls.ContentPresenter presenter
                && presenter.ContentTemplate?.FindName("Pin", presenter) is System.Windows.Controls.Button button)
            {
                button.BringIntoView();
                button.Focus();
            }
        });

    void Pin_MouseDown(object sender, MouseButtonEventArgs e) => _pinDragStart = e.GetPosition(this);

    void Pin_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pinDragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: PinnedItemViewModel pin } button) return;
        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pinDragStart = null;
        try
        {
            DragDrop.DoDragDrop(button, new DataObject(typeof(PinnedItemViewModel), pin), DragDropEffects.Move);
        }
        finally
        {
            DropLine.Hide();
        }
    }

    /// <summary>Over the top half of a pin, the dragged one lands above it; over the bottom half, below it.</summary>
    static bool IsBelow(FrameworkElement target, DragEventArgs e) => e.GetPosition(target).Y > target.ActualHeight / 2;

    void Pin_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(PinnedItemViewModel)) is not PinnedItemViewModel dragged || sender is not FrameworkElement { DataContext: PinnedItemViewModel over } target
            || ReferenceEquals(dragged, over))
        {
            e.Effects = DragDropEffects.None;
            DropLine.Hide();
            return;
        }
        e.Effects = DragDropEffects.Move;
        DropLine.Show(target, IsBelow(target, e));
    }

    void Pin_DragLeave(object sender, DragEventArgs e) => DropLine.Hide();

    async void Pin_Drop(object sender, DragEventArgs e)
    {
        DropLine.Hide();
        if (e.Data.GetData(typeof(PinnedItemViewModel)) is not PinnedItemViewModel dragged || sender is not FrameworkElement { DataContext: PinnedItemViewModel over } target
            || DataContext is not ShellViewModel shell)
            return;
        e.Handled = true;
        var from = shell.Pinned.IndexOf(dragged);
        var to = shell.Pinned.IndexOf(over) + (IsBelow(target, e) ? 1 : 0);
        if (from < 0 || to < 0) return;
        // Its place once it has been taken out of the list.
        if (from < to) to--;
        try
        {
            await shell.MovePinAsync(dragged, to);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Moving a pin failed");
        }
    }
}
