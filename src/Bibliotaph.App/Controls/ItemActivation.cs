using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Runs a command with an item when it is clicked, or when it has keyboard focus and Enter is pressed. The list
/// keeps its own keyboard navigation (arrows move between items), which a button per item would lose.
/// Clicks on a button inside an item are the button's own.
/// </summary>
public static class ItemActivation
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(ItemActivation), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list) return;
        list.MouseLeftButtonUp -= OnMouseUp;
        list.KeyDown -= OnKeyDown;
        if (e.NewValue is null) return;
        list.MouseLeftButtonUp += OnMouseUp;
        list.KeyDown += OnKeyDown;
    }

    static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && !Inside<ButtonBase>(source) && Activate((ItemsControl)sender, source))
            e.Handled = true;
    }

    static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem item && Activate((ItemsControl)sender, item))
            e.Handled = true;
    }

    static bool Activate(ItemsControl list, DependencyObject source)
    {
        var container = ItemsControl.ContainerFromElement(list, source);
        if (container is null) return false;
        var item = list.ItemContainerGenerator.ItemFromContainer(container);
        var command = GetCommand(list);
        if (command is null || item == DependencyProperty.UnsetValue || !command.CanExecute(item)) return false;
        command.Execute(item);
        return true;
    }

    static bool Inside<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null and not ListBoxItem; current = Parent(current))
            if (current is T) return true;
        return false;
    }

    static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
