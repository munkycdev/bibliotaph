using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Views;

/// <summary>
/// Organize mode's dragging: a card picked up from the covers or the list (and every ticked card with it, when it is
/// ticked), dropped on another card, a book in the Books panel or the bar's "Not with a book", with the words beside
/// the pointer saying what letting go will do. The sidebar's pins take the same drag in the main window.
/// </summary>
public partial class LibraryView
{
    // Where the press was, and on which card, until the pointer has moved far enough to be a drag.
    Point? _pressAt;
    LibraryItemViewModel? _pressed;
    LibraryItemViewModel? _over;
    Border? _overBook;

    // How close to a list's top or bottom edge a drag scrolls it, and how far each step goes.
    const double ScrollEdge = 48;
    const double ScrollStep = 24;

    void HookOrganize()
    {
        foreach (var list in new[] { CoverGrid, DetailList })
        {
            list.PreviewMouseLeftButtonDown += List_PreviewMouseLeftButtonDown;
            list.PreviewMouseMove += List_PreviewMouseMove;
            list.DragOver += List_DragOver;
            list.DragLeave += List_DragLeave;
            list.Drop += List_Drop;
            list.GiveFeedback += Drag_GiveFeedback;
        }
        NotWithABookZone.DragOver += Zone_DragOver;
        NotWithABookZone.DragLeave += Zone_DragLeave;
        NotWithABookZone.Drop += Zone_Drop;
    }

    /// <summary>Folder order puts the cards under a heading for each folder; any other order takes the headings away.</summary>
    void ShowGroups()
    {
        foreach (var list in new[] { CoverGrid, DetailList })
        {
            if (list.ItemsSource is null) continue;
            var view = CollectionViewSource.GetDefaultView(list.ItemsSource);
            var grouped = view.GroupDescriptions.Count > 0;
            if (_model?.GroupsByFolder == true && !grouped) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LibraryItemViewModel.FolderGroup)));
            else if (_model?.GroupsByFolder != true && grouped) view.GroupDescriptions.Clear();
        }
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The list is replaced when many books change at once, and the new one's view starts without groups.
        if (e.PropertyName is nameof(LibraryViewModel.GroupsByFolder) or nameof(LibraryViewModel.Items))
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, ShowGroups);
    }

    static LibraryItemViewModel? CardAt(ItemsControl list, object source) =>
        source is DependencyObject element && ItemsControl.ContainerFromElement(list, element) is { } container
            ? list.ItemContainerGenerator.ItemFromContainer(container) as LibraryItemViewModel
            : null;

    static bool OnButton(object source)
    {
        for (var current = source as DependencyObject; current is not null and not ListBoxItem; current = VisualTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }

    void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressAt = null;
        _pressed = null;
        if (_model?.Selection.Books.IsActive != true || OnButton(e.OriginalSource) || CardAt((ItemsControl)sender, e.OriginalSource) is not { } card) return;
        _pressAt = e.GetPosition(this);
        _pressed = card;
    }

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressAt is not { } start || _pressed is not { } card || e.LeftButton != MouseButtonState.Pressed || _model is not { } model) return;
        var moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pressAt = null;
        _pressed = null;
        if (model.Selection.Pick(card) is not { } drag) return;
        drag.PropertyChanged += Drag_PropertyChanged;
        ShowLabel(drag);
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(OrganizeDrag), drag), DragDropEffects.Link);
        }
        finally
        {
            drag.PropertyChanged -= Drag_PropertyChanged;
            ClearMarks();
            DragLabel.IsOpen = false;
        }
    }

    static OrganizeDrag? DragOf(DragEventArgs e) => e.Data.GetData(typeof(OrganizeDrag)) as OrganizeDrag;

    void List_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var list = (ListBox)sender;
        ScrollNearEdge(list, e);
        if (DragOf(e) is not { } drag)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var target = CardAt(list, e.OriginalSource);
        Mark(target);
        if (target is null)
        {
            e.Effects = DragDropEffects.None;
            drag.Plan = null;
            return;
        }
        var plan = LibrarySelectionViewModel.PlanOn(drag, target);
        target.DropMark = plan.CanDrop ? LibraryItemViewModel.DropOk : LibraryItemViewModel.DropNo;
        e.Effects = plan.CanDrop ? DragDropEffects.Link : DragDropEffects.None;
        drag.Plan = plan;
    }

    void List_DragLeave(object sender, DragEventArgs e) => Mark(null);

    void List_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = CardAt((ListBox)sender, e.OriginalSource);
        ClearMarks();
        if (DragOf(e) is not { } drag || target is null || _model is not { } model) return;
        // After the drag has ended, so the menu that asks opens at the pointer and a dialog takes the keyboard.
        Later(() => model.Selection.DropOnAsync(drag, target));
    }

    void Zone_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DragOf(e) is not { } drag) return;
        var plan = LibrarySelectionViewModel.PlanNotWithABook(drag);
        NotWithABookZone.BorderBrush = (Brush)FindResource(plan.CanDrop ? "Bt.Accent" : "Bt.Line");
        e.Effects = plan.CanDrop ? DragDropEffects.Link : DragDropEffects.None;
        drag.Plan = plan;
    }

    void Zone_DragLeave(object sender, DragEventArgs e) => NotWithABookZone.SetResourceReference(Border.BorderBrushProperty, "Bt.Muted");

    void Zone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Zone_DragLeave(sender, e);
        if (DragOf(e) is not { } drag || _model is not { } model) return;
        Later(() => model.Selection.DropNotWithABookAsync(drag));
    }

    void PanelBook_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DragOf(e) is not { } drag || sender is not Border { DataContext: SourceBookChoice book } row) return;
        if (!ReferenceEquals(_overBook, row)) PanelBook_DragLeave(_overBook, e);
        _overBook = row;
        var plan = LibrarySelectionViewModel.PlanOnBook(drag, book);
        row.BorderBrush = (Brush)FindResource(plan.CanDrop ? "Bt.Accent" : "Bt.Muted");
        row.BorderThickness = new Thickness(2);
        e.Effects = plan.CanDrop ? DragDropEffects.Link : DragDropEffects.None;
        drag.Plan = plan;
    }

    void PanelBook_DragLeave(object? sender, DragEventArgs e)
    {
        if (sender is not Border row) return;
        row.SetResourceReference(Border.BorderBrushProperty, "Bt.Line");
        row.BorderThickness = new Thickness(1);
        if (ReferenceEquals(_overBook, row)) _overBook = null;
    }

    void PanelBook_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        PanelBook_DragLeave(sender, e);
        if (DragOf(e) is not { } drag || sender is not Border { DataContext: SourceBookChoice book } || _model is not { } model) return;
        Later(() => model.Selection.DropOnBookAsync(drag, book));
    }

    void Later(Func<Task> drop) => Dispatcher.BeginInvoke(DispatcherPriority.Input, async () => await drop());

    void Mark(LibraryItemViewModel? card)
    {
        if (ReferenceEquals(_over, card)) return;
        _over?.DropMark = null;
        _over = card;
    }

    void ClearMarks()
    {
        Mark(null);
        if (_overBook is { } row)
        {
            row.SetResourceReference(Border.BorderBrushProperty, "Bt.Line");
            row.BorderThickness = new Thickness(1);
            _overBook = null;
        }
        NotWithABookZone.SetResourceReference(Border.BorderBrushProperty, "Bt.Muted");
    }

    /// <summary>A drag near a list's top or bottom edge scrolls it, so a map can be carried to a book further up or down.</summary>
    static void ScrollNearEdge(ListBox list, DragEventArgs e)
    {
        if (FindScrollViewer(list) is not { } scroll) return;
        var y = e.GetPosition(scroll).Y;
        if (y < ScrollEdge) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - ScrollStep);
        else if (y > scroll.ActualHeight - ScrollEdge) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + ScrollStep);
    }

    // Whatever the pointer is over says what letting go does there, the sidebar's pins in the main window too.
    void Drag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is OrganizeDrag drag) ShowLabel(drag);
    }

    /// <summary>The words beside the pointer: how many cards, and what letting go here does, or why it does nothing.</summary>
    void ShowLabel(OrganizeDrag drag)
    {
        DragCount.Text = drag.Count;
        DragWhat.Text = drag.Plan?.Label ?? "";
        DragWhat.Visibility = DragWhat.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        DragLabelBox.Opacity = drag.Plan is { CanDrop: false } ? 0.75 : 1;
        DragLabel.IsOpen = true;
    }

    /// <summary>
    /// Keeps the words just below and right of the pointer wherever it goes, over the sidebar too, as the drag tells its
    /// source how it is going.
    /// </summary>
    void Drag_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (!DragLabel.IsOpen || !GetCursorPos(out var cursor) || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target) return;
        var at = target.TransformFromDevice.Transform(new Point(cursor.X, cursor.Y));
        DragLabel.HorizontalOffset = at.X + 16;
        DragLabel.VerticalOffset = at.Y + 20;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetCursorPos(out CursorPoint point);
}
