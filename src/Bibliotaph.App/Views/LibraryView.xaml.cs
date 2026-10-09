using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

public partial class LibraryView
{
    IInputElement? _focusBeforeDrawer;
    LibraryViewModel? _model;

    public LibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as LibraryViewModel);
        Unloaded += (_, _) =>
        {
            // Leaving for another page (opening a book): remember where the list was, for Back.
            if (_model is not null && VisibleList() is { } scroll) _model.SavedScrollOffset = scroll.VerticalOffset;
            Attach(null);
        };
        Loaded += (_, _) => Attach(DataContext as LibraryViewModel);
    }

    void Attach(LibraryViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        _model?.RestoreScroll -= OnRestoreScroll;
        _model = model;
        model?.RestoreScroll += OnRestoreScroll;
    }

    void OnRestoreScroll(object? sender, EventArgs e) =>
        // After the refreshed list has been laid out, so the offset exists again.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_model?.SavedScrollOffset is not { } offset) return;
            _model.SavedScrollOffset = null;
            VisibleList()?.ScrollToVerticalOffset(offset);
        });

    ScrollViewer? VisibleList()
    {
        // Visibility, not IsVisible: when the view unloads, nothing in it is visible any more.
        var list = new[] { CoverGrid, DetailList, PageHits }.FirstOrDefault(l => l.Visibility == Visibility.Visible);
        return list is null ? null : FindScrollViewer(list);
    }

    static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>The arrow beside Reprocess opens its menu below it, by click or by keyboard, as a split button's does.</summary>
    void ReprocessMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>Keyboard focus goes into the inspector when it opens and back to where it was when it closes.</summary>
    void Drawer_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Drawer.IsVisible)
        {
            _focusBeforeDrawer = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => CloseDrawer.Focus());
        }
        else if (_focusBeforeDrawer is UIElement { IsVisible: true } previous)
        {
            previous.Focus();
            _focusBeforeDrawer = null;
        }
    }
}
