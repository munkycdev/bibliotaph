using System.Windows;
using System.Windows.Controls;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Names a book menu's Pin item each time the menu opens: Pin, or Unpin for a book in the sidebar's Pinned group
/// (Pinned Views plan, choice 7). Set <c>PinMenu.IsEnabled</c> on a ContextMenu; its items running
/// <see cref="BookCommands.TogglePin"/> are renamed.
/// </summary>
public static class PinMenu
{
    /// <summary>Whether the menu's context (a book's card) is pinned, kept current by the pin directory.</summary>
    public static Func<object?, bool> IsPinned { get; set; } = _ => false;

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
        var label = IsPinned(menu.DataContext) ? "Unpin" : "Pin";
        foreach (var item in menu.Items.OfType<MenuItem>().Where(i => i.Command == BookCommands.TogglePin)) item.Header = label;
    }
}
