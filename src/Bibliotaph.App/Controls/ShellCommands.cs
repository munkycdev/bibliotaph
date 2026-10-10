using System.Windows.Input;

namespace Bibliotaph.App.Controls;

/// <summary>Window-level commands whose behaviour is purely about the view (focus), so they live outside view models.</summary>
public static class ShellCommands
{
    public static RoutedUICommand FocusSearch { get; } = new("Search", nameof(FocusSearch), typeof(ShellCommands));

    /// <summary>Ctrl+/ (the / key, or the number pad's): the Keyboard shortcuts popup, over the window it was pressed in.</summary>
    public static RoutedUICommand ShowShortcuts { get; } = new("Keyboard shortcuts", nameof(ShowShortcuts), typeof(ShellCommands));
}
