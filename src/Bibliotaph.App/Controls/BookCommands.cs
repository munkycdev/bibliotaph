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

    /// <summary>Add to favorites, or Remove from favorites (slice 3 plan, choice 5).</summary>
    public static RoutedUICommand ToggleFavorite { get; } = new("Favorite", nameof(ToggleFavorite), typeof(BookCommands));

    /// <summary>
    /// Add to collection (slice 3 plan, choice 10), with a <see cref="CollectionRequest"/>: its target is the card's book,
    /// or the details it was picked in.
    /// </summary>
    public static RoutedUICommand AddToCollection { get; } = new("Add to collection", nameof(AddToCollection), typeof(BookCommands));

    /// <summary>Add to session (slice 3 plan, choice 13), with a <see cref="SessionRequest"/>: its target is the card's book, or the details.</summary>
    public static RoutedUICommand AddToSession { get; } = new("Add to session", nameof(AddToSession), typeof(BookCommands));
}
