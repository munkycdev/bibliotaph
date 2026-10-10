using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bibliotaph.App.Controls;

/// <summary>Visible when the bound value is true (or false, with <see cref="Invert"/>); otherwise collapsed.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    /// <summary>For dictionaries loaded before the app's own converters, such as a card's menu in Controls.xaml.</summary>
    public static BoolToVisibility WhenTrue { get; } = new();

    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
