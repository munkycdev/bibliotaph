using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bibliotaph.App.Controls;

public enum SessionMenuKind
{
    /// <summary>"Add to The midnight bell": the current session pack, or another listed one.</summary>
    Session,

    /// <summary>"Add to a new session…": names one, then adds to it.</summary>
    New,

    /// <summary>"Add to another session…": picks one from all of them.</summary>
    Choose,
}

/// <summary>One of the Add to session items in a menu (slice 3 plan, choice 13).</summary>
public sealed record SessionMenuChoice(SessionMenuKind Kind, long? PackId, string Label);

/// <summary>What an Add to session item runs with: the menu's context (a book, the details, the page) and the item.</summary>
public sealed record SessionRequest(object? Target, SessionMenuChoice Choice);

/// <summary>
/// Puts the Add to session items in a menu each time it opens, before any Add to collection items: the current
/// session, then another and a new one. Set <c>SessionMenu.Command</c> on a ContextMenu; each item runs it with a
/// <see cref="SessionRequest"/> carrying the menu's DataContext.
/// </summary>
public static class SessionMenu
{
    static readonly MenuGroup Group = new(Order: 1);

    /// <summary>The items every menu shows, kept current by the session directory on the UI thread.</summary>
    public static IReadOnlyList<SessionMenuChoice> Choices { get; set; } = [];

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
        Group.Refill(menu, command is null ? [] : Choices.Select(choice => new MenuItem
        {
            Header = choice.Label,
            Command = command,
            CommandParameter = new SessionRequest(menu.DataContext, choice),
        }));
    }
}
