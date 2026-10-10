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

    /// <summary>Open in another app (slice 4i plan, choice 4), for a PDF whose protection Bibliotaph can't open.</summary>
    public static RoutedUICommand OpenElsewhere { get; } = new("Open in another app", nameof(OpenElsewhere), typeof(BookCommands));

    /// <summary>Forget its text (slice 4i plan, choice 6): its pages, search text, cover and AI results go.</summary>
    public static RoutedUICommand ForgetText { get; } = new("Forget its text", nameof(ForgetText), typeof(BookCommands));

    /// <summary>Read it again: undoes Forget its text.</summary>
    public static RoutedUICommand ReadTextAgain { get; } = new("Read it again", nameof(ReadTextAgain), typeof(BookCommands));

    /// <summary>Add to binder (slice 3 plan, choice 13), with a <see cref="SessionRequest"/>: its target is the card's book, or the details.</summary>
    public static RoutedUICommand AddToSession { get; } = new("Add to binder", nameof(AddToSession), typeof(BookCommands));
}
