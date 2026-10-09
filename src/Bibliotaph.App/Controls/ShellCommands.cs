using System.Windows.Input;

namespace Bibliotaph.App.Controls;

/// <summary>Window-level commands whose behaviour is purely about the view (focus), so they live outside view models.</summary>
public static class ShellCommands
{
    public static RoutedUICommand FocusSearch { get; } = new("Search", nameof(FocusSearch), typeof(ShellCommands));

    /// <summary>Opens the About popup; the main window handles it, so a page's link only has to raise it.</summary>
    public static RoutedUICommand ShowAbout { get; } = new("About Bibliotaph", nameof(ShowAbout), typeof(ShellCommands));
}
