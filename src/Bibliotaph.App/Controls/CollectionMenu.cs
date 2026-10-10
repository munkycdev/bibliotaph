using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bibliotaph.App.Controls;

public enum CollectionMenuKind
{
    /// <summary>"Add to Maps": one of the recent collections.</summary>
    Collection,

    /// <summary>"Add to a new collection…": names one, then adds to it.</summary>
    New,

    /// <summary>"Add to another collection…": picks one from all of them.</summary>
    Choose,
}

/// <summary>One of the Add to collection items in a menu (slice 3 plan, choice 10).</summary>
public sealed record CollectionMenuChoice(CollectionMenuKind Kind, long? CollectionId, string Label, string? Path);

/// <summary>What an Add to collection item runs with: the menu's context (a book, the details, the page) and the item.</summary>
public sealed record CollectionRequest(object? Target, CollectionMenuChoice Choice);

/// <summary>
/// Puts the Add to collection items at the end of a menu each time it opens, so they follow the collections as they
/// change: the recent ones, then New and Choose. Set <c>CollectionMenu.Command</c> on a ContextMenu; each item runs it
/// with a <see cref="CollectionRequest"/> carrying the menu's DataContext, so a card's menu knows its book.
/// </summary>
public static class CollectionMenu
{
    static readonly object Marker = new();

    /// <summary>The items every menu shows, kept current by the collection directory on the UI thread.</summary>
    public static IReadOnlyList<CollectionMenuChoice> Choices { get; set; } = [];

    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached("Command", typeof(ICommand), typeof(CollectionMenu),
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
        foreach (var stale in menu.Items.OfType<MenuItem>().Where(i => ReferenceEquals(i.Tag, Marker)).ToList()) menu.Items.Remove(stale);
        if (GetCommand(menu) is not { } command) return;
        foreach (var choice in Choices)
        {
            menu.Items.Add(new MenuItem
            {
                Header = choice.Label,
                ToolTip = choice.Path,
                Command = command,
                CommandParameter = new CollectionRequest(menu.DataContext, choice),
                Tag = Marker,
            });
        }
    }
}
