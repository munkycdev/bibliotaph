using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bibliotaph.App.Controls;

/// <summary>
/// What a screen shows when it has nothing yet: what is missing, and one relevant action (spec §11).
/// Its look is the implicit style in Themes/Controls.xaml, after the mockup's .bt-empty.
/// </summary>
public sealed class EmptyState : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(nameof(ActionText), typeof(string), typeof(EmptyState));
    public static readonly DependencyProperty ActionIconProperty = DependencyProperty.Register(nameof(ActionIcon), typeof(Geometry), typeof(EmptyState));
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(EmptyState));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => (string?)GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ActionText { get => (string?)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public Geometry? ActionIcon { get => (Geometry?)GetValue(ActionIconProperty); set => SetValue(ActionIconProperty, value); }
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
}
