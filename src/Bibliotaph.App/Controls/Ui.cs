using System.Windows;
using System.Windows.Media;

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

    public static Geometry? GetIcon(DependencyObject element) => (Geometry?)element.GetValue(IconProperty);
    public static void SetIcon(DependencyObject element, Geometry? value) => element.SetValue(IconProperty, value);

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
