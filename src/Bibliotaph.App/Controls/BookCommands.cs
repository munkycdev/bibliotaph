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

    /// <summary>Open on D&amp;D Beyond: the book's page where the user owns it online, in the browser.</summary>
    public static RoutedUICommand OpenOnline { get; } = new("Open online", nameof(OpenOnline), typeof(BookCommands));

    public static RoutedUICommand Details { get; } = new("Details", nameof(Details), typeof(BookCommands));

    /// <summary>Add to favorites, or Remove from favorites.</summary>
    public static RoutedUICommand ToggleFavorite { get; } = new("Favorite", nameof(ToggleFavorite), typeof(BookCommands));

    /// <summary>Pin, or Unpin: the book in the sidebar's Pinned group.</summary>
    public static RoutedUICommand TogglePin { get; } = new("Pin", nameof(TogglePin), typeof(BookCommands));

    /// <summary>
    /// Add to collection, with a <see cref="CollectionRequest"/>: its target is the card's book, or the details it was
    /// picked in.
    /// </summary>
    public static RoutedUICommand AddToCollection { get; } = new("Add to collection", nameof(AddToCollection), typeof(BookCommands));

    /// <summary>Open in another app, for a PDF whose protection Bibliotaph can't open.</summary>
    public static RoutedUICommand OpenElsewhere { get; } = new("Open in another app", nameof(OpenElsewhere), typeof(BookCommands));

    /// <summary>Forget its text: its pages, search text, cover and AI results go.</summary>
    public static RoutedUICommand ForgetText { get; } = new("Forget its text", nameof(ForgetText), typeof(BookCommands));

    /// <summary>Read it again: undoes Forget its text.</summary>
    public static RoutedUICommand ReadTextAgain { get; } = new("Read it again", nameof(ReadTextAgain), typeof(BookCommands));

    /// <summary>Ask this book: opens a PDF with its Ask panel showing. Offered only while AI is on.</summary>
    public static RoutedUICommand Ask { get; } = new("Ask this book", nameof(Ask), typeof(BookCommands));
    /// <summary>
    /// Cataloged wrong…: the book's editor, with its guesses marked. Left without a change, it leaves a Needs review card
    /// that opens it again.
    /// </summary>
    public static RoutedUICommand CatalogedWrong { get; } = new("Cataloged wrong…", nameof(CatalogedWrong), typeof(BookCommands));

    /// <summary>Remove from library, for a book owned elsewhere: the Library page only, with Undo.</summary>
    public static RoutedUICommand RemoveFromLibrary { get; } = new("Remove from library", nameof(RemoveFromLibrary), typeof(BookCommands));

    /// <summary>Add to binder, with a <see cref="SessionRequest"/>: its target is the card's book, or the details.</summary>
    public static RoutedUICommand AddToSession { get; } = new("Add to binder", nameof(AddToSession), typeof(BookCommands));
}
