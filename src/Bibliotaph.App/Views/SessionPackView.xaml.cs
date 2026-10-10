using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>
/// A session pack's page (slice 3c). Items are dragged by their grip and dropped on another item, which they take the
/// place of, or on a section's heading; the keyboard stays on an item that Move up or Move down moved.
/// </summary>
public partial class SessionPackView
{
    Point? _dragStart;

    public SessionPackView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SessionPackViewModel old) old.FocusItemRequested -= OnFocusItemRequested;
            if (e.NewValue is SessionPackViewModel page) page.FocusItemRequested += OnFocusItemRequested;
        };
    }

    SessionPackViewModel? Page => DataContext as SessionPackViewModel;

    void OnFocusItemRequested(object? sender, long itemId) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var row = Page?.Rows.OfType<SessionItemRow>().FirstOrDefault(r => r.Id == itemId);
            if (row is not null && SessionItems.ItemContainerGenerator.ContainerFromItem(row) is ContentPresenter container
                && FindRow(container) is { } border) border.Focus();
        });

    static Border? FindRow(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border { Name: "ItemRow" } border) return border;
            if (FindRow(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Enter in a label or note saves it and returns the keyboard to the item, rather than opening it.</summary>
    void InlineBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (FindAncestor<Border>(box, "ItemRow") is { } row) row.Focus();
        e.Handled = true;
    }

    static T? FindAncestor<T>(DependencyObject child, string name) where T : FrameworkElement
    {
        for (var at = VisualTreeHelper.GetParent(child); at is not null; at = VisualTreeHelper.GetParent(at))
            if (at is T element && element.Name == name) return element;
        return null;
    }

    void ItemRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border { DataContext: SessionItemRow row, ContextMenu: { } menu } || Page is not { } page) return;
        menu.Items.Clear();
        static MenuItem Item(string header, ICommand command, object parameter, string? gesture = null) =>
            new() { Header = header, Command = command, CommandParameter = parameter, InputGestureText = gesture ?? "" };
        menu.Items.Add(Item("Open", page.OpenItemCommand, row, "Enter"));
        menu.Items.Add(Item("Open in new window", page.OpenItemInNewWindowCommand, row, "Shift+Enter"));
        menu.Items.Add(Item("Move up", page.MoveUpCommand, row, "Alt+Up"));
        menu.Items.Add(Item("Move down", page.MoveDownCommand, row, "Alt+Down"));
        foreach (var move in row.MoveTargets) menu.Items.Add(Item(move.Label, page.MoveToSectionCommand, move));
        menu.Items.Add(Item("Remove from binder", page.RemoveItemCommand, row, "Delete"));
    }

    void Grip_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { DataContext: SessionItemRow row } grip) return;
        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        DragDrop.DoDragDrop(grip, new DataObject(typeof(SessionItemRow), row), DragDropEffects.Move);
    }

    void Row_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SessionItemRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    async void Row_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(SessionItemRow)) is not SessionItemRow dragged || sender is not FrameworkElement { DataContext: { } target } || Page is not { } page) return;
        e.Handled = true;
        await page.DropAsync(dragged, target);
    }

    void DropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
