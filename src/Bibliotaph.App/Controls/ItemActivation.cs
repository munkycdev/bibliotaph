using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Runs a command with an item when it is clicked, or when it has keyboard focus and Enter is pressed. The list
/// keeps its own keyboard navigation (arrows move between items), which a button per item would lose.
/// Clicks on a button inside an item are the button's own. A Shift+click runs <c>RangeCommand</c> instead, when one is
/// set and can run, as for ticking a range of books in the Library's Select mode.
/// </summary>
public static class ItemActivation
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(ItemActivation), new PropertyMetadata(null, OnCommandChanged));

    public static readonly DependencyProperty RangeCommandProperty = DependencyProperty.RegisterAttached(
        "RangeCommand", typeof(ICommand), typeof(ItemActivation), new PropertyMetadata(null));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    public static ICommand? GetRangeCommand(DependencyObject element) => (ICommand?)element.GetValue(RangeCommandProperty);
    public static void SetRangeCommand(DependencyObject element, ICommand? value) => element.SetValue(RangeCommandProperty, value);

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
        if (e.OriginalSource is DependencyObject source && !Inside<ButtonBase>(source)
            && Activate((ItemsControl)sender, source, range: Keyboard.Modifiers == ModifierKeys.Shift))
            e.Handled = true;
    }

    static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.OriginalSource is ListBoxItem item && Activate((ItemsControl)sender, item, range: false))
            e.Handled = true;
    }

    static bool Activate(ItemsControl list, DependencyObject source, bool range)
    {
        var container = ItemsControl.ContainerFromElement(list, source);
        if (container is null) return false;
        var item = list.ItemContainerGenerator.ItemFromContainer(container);
        var command = range && GetRangeCommand(list) is { } ranged && ranged.CanExecute(item) ? ranged : GetCommand(list);
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
