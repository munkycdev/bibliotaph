using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>
/// A binder's reader (Binders plan, choices 1 to 8). Renaming a page in the list is the view's business: a double-click
/// starts it, the name's box takes the keyboard, and Enter, Esc or leaving the box ends it. On a narrow window (the
/// minimum size, or 200% scaling) the notes panel folds away by itself to leave the reader its room, and comes back
/// when there's room again, unless it was shown or hidden by hand in between.
/// </summary>
public partial class SessionReaderView
{
    /// <summary>Below this width the list, the reader and the notes don't all fit.</summary>
    const double NotesFitWidth = 960;

    SessionReaderViewModel? _model;
    bool _foldedForRoom;
    bool _folding;

    public SessionReaderView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as SessionReaderViewModel);
        Loaded += (_, _) => Attach(DataContext as SessionReaderViewModel);
        Unloaded += (_, _) => Attach(null);
        SizeChanged += (_, _) => FitNotes();
        // Wired here rather than in the item template: a double-click on an item, and keys and focus leaving its name's box.
        AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
        AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus));
    }

    void Attach(SessionReaderViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        _model?.RenameStarted -= OnRenameStarted;
        _model?.PropertyChanged -= OnModelChanged;
        _model = model;
        _foldedForRoom = false;
        _model?.RenameStarted += OnRenameStarted;
        _model?.PropertyChanged += OnModelChanged;
        FitNotes();
    }

    void FitNotes()
    {
        if (_model is null || ActualWidth <= 0) return;
        _folding = true;
        try
        {
            if (ActualWidth < NotesFitWidth && _model.ShowNotes)
            {
                _model.ShowNotes = false;
                _foldedForRoom = true;
            }
            else if (ActualWidth >= NotesFitWidth && _foldedForRoom)
            {
                _model.ShowNotes = true;
                _foldedForRoom = false;
            }
        }
        finally
        {
            _folding = false;
        }
    }

    /// <summary>Notes shown or hidden by hand stay that way.</summary>
    void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionReaderViewModel.ShowNotes) && !_folding) _foldedForRoom = false;
    }

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || _model is null) return;
        if (Named(e.OriginalSource as DependencyObject, "RunItem") is { DataContext: SessionItemRow row })
        {
            _model.RenameItemCommand.Execute(row);
            e.Handled = true;
        }
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TextBox { Name: "RenameBox", DataContext: SessionItemRow row } || _model is null) return;
        switch (e.Key)
        {
            case Key.Enter:
                SessionReaderViewModel.CommitRename(row);
                FocusItem(row);
                e.Handled = true;
                break;
            case Key.Escape:
                SessionReaderViewModel.CancelRename(row);
                FocusItem(row);
                e.Handled = true;
                break;
        }
    }

    void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox { Name: "RenameBox", DataContext: SessionItemRow row } && _model is not null) SessionReaderViewModel.CommitRename(row);
    }

    /// <summary>The name's box takes the keyboard, with its text selected to type over, once the list has drawn it.</summary>
    void OnRenameStarted(object? sender, SessionItemRow row) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (InRow(row, "RenameBox") is TextBox box)
            {
                box.Focus();
                box.SelectAll();
            }
        });

    void FocusItem(SessionItemRow row) => InRow(row, "RunItem")?.Focus();

    /// <summary>The element called <paramref name="name"/> in the list's row for <paramref name="row"/>.</summary>
    FrameworkElement? InRow(SessionItemRow row, string name) =>
        RunItems.ItemContainerGenerator.ContainerFromItem(row) is DependencyObject container ? Find(container, name) : null;

    static FrameworkElement? Find(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var childName } element && childName == name) return element;
            if (Find(child, name) is { } found) return found;
        }
        return null;
    }

    /// <summary>The nearest element called <paramref name="name"/> at or above <paramref name="source"/>.</summary>
    static FrameworkElement? Named(DependencyObject? source, string name)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is FrameworkElement { Name: var nodeName } element && nodeName == name) return element;
        return null;
    }
}
