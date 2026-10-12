using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bibliotaph.App.Controls;

/// <summary>Visible when the bound value is set (or not, with <see cref="Invert"/>); collapsed when it is null or an empty string.</summary>
public sealed class PresenceToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is null or "") == Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
