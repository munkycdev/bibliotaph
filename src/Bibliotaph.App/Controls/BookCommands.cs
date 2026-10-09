using System.Windows.Input;

namespace Bibliotaph.App.Controls;

/// <summary>
/// A book card's menu. The menu is a popup outside the page, so it can't bind to the page's commands; these route
/// from the card to the Library page, which runs them with the book as the parameter.
/// </summary>
public static class BookCommands
{
    public static RoutedUICommand Open { get; } = new("Open", nameof(Open), typeof(BookCommands));

    public static RoutedUICommand OpenInNewWindow { get; } = new("Open in new window", nameof(OpenInNewWindow), typeof(BookCommands));

    public static RoutedUICommand Details { get; } = new("Details", nameof(Details), typeof(BookCommands));
}
