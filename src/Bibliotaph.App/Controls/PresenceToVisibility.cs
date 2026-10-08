using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bibliotaph.App.Controls;

/// <summary>Visible when the bound value is set; collapsed when it is null or an empty string.</summary>
public sealed class PresenceToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
