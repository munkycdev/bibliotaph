using System.Windows;
using System.Windows.Controls;

namespace Bibliotaph.App.Controls;

/// <summary>Whether a book menu's context (a book's card) is pinned: the pin directory knows, through the page.</summary>
public interface IPinState
{
    bool IsPinned(object? context);
}

/// <summary>
/// Names a book menu's Pin item each time the menu opens: Pin, or Unpin for a book in the sidebar's Pinned group. Set
/// <c>PinMenu.IsEnabled</c> on a ContextMenu; its items running <see cref="BookCommands.TogglePin"/> are renamed. The
/// nearest <see cref="IPinState"/> (see <see cref="MenuContext"/>), the page's view model, says which.
/// </summary>
public static class PinMenu
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(PinMenu),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContextMenu menu) return;
        menu.Opened -= Name;
        if (e.NewValue is true) menu.Opened += Name;
    }

    static void Name(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        var label = Label(MenuContext.Find<IPinState>(menu), menu.DataContext);
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Command == BookCommands.TogglePin)) item.Header = label;
    }

    /// <summary>The Pin item's name for a menu's context; Pin where nothing says it is pinned.</summary>
    public static string Label(IPinState? pins, object? context) => pins?.IsPinned(context) == true ? "Unpin" : "Pin";
}
