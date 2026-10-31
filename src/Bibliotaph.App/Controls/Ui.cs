using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.Core;

namespace Bibliotaph.App.Controls;

/// <summary>Attached properties the control templates in Themes/Controls.xaml read.</summary>
public static class Ui
{
    /// <summary>A Lucide geometry shown before a button's content.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Marks the navigation item for the current screen.</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false));

    /// <summary>A count a navigation item shows after its label.</summary>
    public static readonly DependencyProperty CountProperty = DependencyProperty.RegisterAttached(
        "Count", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(""));

    /// <summary>
    /// Set on the sidebar while it is folded to its icons, and inherited by everything in it: a navigation button then
    /// shows only its icon, with its label as a tooltip.
    /// </summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached(
        "Compact", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>What a box's clear (×) button runs.</summary>
    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.RegisterAttached(
        "ClearCommand", typeof(ICommand), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Which of the tour's callouts points at this element, if any; the tour finds its targets by it.</summary>
    public static readonly DependencyProperty TourAnchorProperty = DependencyProperty.RegisterAttached(
        "TourAnchor", typeof(TourAnchor), typeof(Ui), new FrameworkPropertyMetadata(TourAnchor.None));

    /// <summary>Takes keyboard focus each time the element becomes visible; a text box also selects its text.</summary>
    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached(
        "FocusWhenVisible", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, OnFocusWhenVisibleChanged));

    /// <summary>
    /// A scroller inside another: once it can't scroll further the way the wheel turns, the wheel scrolls the one
    /// around it, so everything in the outer one can still be reached with the wheel.
    /// </summary>
    public static readonly DependencyProperty WheelToParentProperty = DependencyProperty.RegisterAttached(
        "WheelToParent", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, OnWheelToParentChanged));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    public static string GetCount(DependencyObject element) => (string)element.GetValue(CountProperty);
    public static void SetCount(DependencyObject element, string value) => element.SetValue(CountProperty, value);

    public static bool GetCompact(DependencyObject element) => (bool)element.GetValue(CompactProperty);
    public static void SetCompact(DependencyObject element, bool value) => element.SetValue(CompactProperty, value);

    public static ICommand? GetClearCommand(DependencyObject element) => (ICommand?)element.GetValue(ClearCommandProperty);
    public static void SetClearCommand(DependencyObject element, ICommand? value) => element.SetValue(ClearCommandProperty, value);

    public static bool GetWheelToParent(DependencyObject element) => (bool)element.GetValue(WheelToParentProperty);
    public static void SetWheelToParent(DependencyObject element, bool value) => element.SetValue(WheelToParentProperty, value);

    static void OnWheelToParentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer scroller) return;
        scroller.PreviewMouseWheel -= PassWheelAtEnd;
        if (e.NewValue is true) scroller.PreviewMouseWheel += PassWheelAtEnd;
    }

    static void PassWheelAtEnd(object sender, MouseWheelEventArgs e)
    {
        var scroller = (ScrollViewer)sender;
        var atEnd = e.Delta > 0 ? scroller.VerticalOffset <= 0 : scroller.VerticalOffset >= scroller.ScrollableHeight;
        if (!atEnd || VisualTreeHelper.GetParent(scroller) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = scroller });
    }

    public static TourAnchor GetTourAnchor(DependencyObject element) => (TourAnchor)element.GetValue(TourAnchorProperty);
    public static void SetTourAnchor(DependencyObject element, TourAnchor value) => element.SetValue(TourAnchorProperty, value);

    public static bool GetFocusWhenVisible(DependencyObject element) => (bool)element.GetValue(FocusWhenVisibleProperty);
    public static void SetFocusWhenVisible(DependencyObject element, bool value) => element.SetValue(FocusWhenVisibleProperty, value);

    static void OnFocusWhenVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.IsVisibleChanged -= FocusOnShow;
        if ((bool)e.NewValue) element.IsVisibleChanged += FocusOnShow;
    }

    static void FocusOnShow(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || !element.IsVisible) return;
        // Focus once layout has made the element focusable.
        element.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            element.Focus();
            if (element is TextBox box) box.SelectAll();
        });
    }
}
