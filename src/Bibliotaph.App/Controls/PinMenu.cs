using System.Windows;
using System.Windows.Controls;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.App.Controls;

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
        var label = MenuChoiceSource.PinLabel(MenuContext.Find<IPinState>(menu), menu.DataContext);
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Command == BookCommands.TogglePin)) item.Header = label;
    }
}
