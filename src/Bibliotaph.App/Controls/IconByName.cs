using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Bibliotaph.App.Controls;

/// <summary>
/// A Lucide icon by its name in Icons.xaml ("Link" is Icon.Link), for marks whose icon the shared code names, so the
/// Windows app and the web pick the same one.
/// </summary>
public sealed class IconByName : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string name ? Application.Current?.TryFindResource($"Icon.{name}") as Geometry : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
