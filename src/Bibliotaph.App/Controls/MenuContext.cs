using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Where a menu filled as it opens finds what it lists: its own DataContext, or else the nearest one above the element
/// it opened on. A card's menu has the card's book as its DataContext, so it reaches the page's view model this way; a
/// toolbar's menu has the page's view model as its own.
/// </summary>
public static class MenuContext
{
    public static T? Find<T>(ContextMenu menu) where T : class => Nearest<T>(Contexts(menu));

    /// <summary>The first of these data contexts, nearest first, that is a <typeparamref name="T"/>.</summary>
    public static T? Nearest<T>(IEnumerable<object?> contexts) where T : class => contexts.OfType<T>().FirstOrDefault();

    static IEnumerable<object?> Contexts(ContextMenu menu)
    {
        yield return menu.DataContext;
        for (DependencyObject? at = menu.PlacementTarget; at is not null; at = Parent(at))
        {
            if (at is FrameworkElement element) yield return element.DataContext;
        }
    }

    // Parts of a template, such as a card's cover, have a visual parent but no logical one.
    static DependencyObject? Parent(DependencyObject child) =>
        child is Visual or Visual3D ? VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child);
}
