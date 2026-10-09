using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

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

    /// <summary>What a box's clear (×) button runs.</summary>
    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.RegisterAttached(
        "ClearCommand", typeof(ICommand), typeof(Ui), new FrameworkPropertyMetadata(null));

    /// <summary>Takes keyboard focus each time the element becomes visible; a text box also selects its text.</summary>
    public static readonly DependencyProperty FocusWhenVisibleProperty = DependencyProperty.RegisterAttached(
        "FocusWhenVisible", typeof(bool), typeof(Ui), new FrameworkPropertyMetadata(false, OnFocusWhenVisibleChanged));

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

    public static ICommand? GetClearCommand(DependencyObject element) => (ICommand?)element.GetValue(ClearCommandProperty);
    public static void SetClearCommand(DependencyObject element, ICommand? value) => element.SetValue(ClearCommandProperty, value);

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
