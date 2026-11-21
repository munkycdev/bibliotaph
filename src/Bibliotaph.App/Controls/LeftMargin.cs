using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bibliotaph.App.Controls;

/// <summary>A view model's indent, in pixels, as a margin on the left only.</summary>
public sealed class LeftMargin : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double left ? new Thickness(left, 0, 0, 0) : new Thickness();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
