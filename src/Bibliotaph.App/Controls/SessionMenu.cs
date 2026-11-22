using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Puts the Add to session items in a menu each time it opens, before any Add to collection items: the current
/// session, then another and a new one. Set <c>SessionMenu.Command</c> on a ContextMenu; each item runs it with a
/// <see cref="SessionRequest"/> carrying the menu's DataContext. The items come from the nearest
/// <see cref="ISessionChoices"/> (see <see cref="MenuContext"/>), the page's view model.
/// </summary>
public static class SessionMenu
{
    static readonly MenuGroup Group = new(Order: 1);

    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(SessionMenu),
        new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContextMenu menu) return;
        menu.Opened -= Fill;
        if (e.NewValue is not null) menu.Opened += Fill;
    }

    static void Fill(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        var command = GetCommand(menu);
        var choices = command is null ? null : MenuContext.Find<ISessionChoices>(menu);
        Group.Refill(menu, choices is null ? [] : choices.MenuChoices.Select(choice => new MenuItem
        {
            Header = choice.Label,
            Command = command,
            CommandParameter = new SessionRequest(menu.DataContext, choice),
        }));
    }
}
