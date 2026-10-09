using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Middle-click, or Shift+Enter, opens something in a new window, as in a browser. On a list it runs the command with
/// the item under the pointer or with keyboard focus; on a button, with the button's own CommandParameter.
/// </summary>
public static class NewWindowGesture
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(NewWindowGesture), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.MouseUp -= OnMouseUp;
        element.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is null) return;
        element.MouseUp += OnMouseUp;
        element.PreviewKeyDown += OnPreviewKeyDown;
    }

    static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && Run((DependencyObject)sender, e.OriginalSource)) e.Handled = true;
    }

    // Preview, so a button or a list item doesn't take Shift+Enter as a plain Enter first.
    static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift && Run((DependencyObject)sender, e.OriginalSource)) e.Handled = true;
    }

    static bool Run(DependencyObject element, object source)
    {
        var parameter = element switch
        {
            ItemsControl list when source is DependencyObject inner && ItemsControl.ContainerFromElement(list, inner) is { } container
                => list.ItemContainerGenerator.ItemFromContainer(container),
            ItemsControl => null,
            ICommandSource button => button.CommandParameter,
            _ => null,
        };
        var command = GetCommand(element);
        if (parameter is null || parameter == DependencyProperty.UnsetValue || command is null || !command.CanExecute(parameter)) return false;
        command.Execute(parameter);
        return true;
    }
}
