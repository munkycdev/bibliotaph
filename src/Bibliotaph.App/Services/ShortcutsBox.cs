using System.Windows;
using Bibliotaph.App.Views;

namespace Bibliotaph.App.Services;

/// <summary>Opens the Keyboard shortcuts popup over the window it was asked from: the main window or a pop-out reader.</summary>
public sealed class ShortcutsBox(ThemeService theme)
{
    /// <summary>Opens the popup over <paramref name="owner"/>, or over the main window.</summary>
    public void Show(Window? owner = null)
    {
        var dialog = new ShortcutsDialog { Owner = owner ?? Application.Current.MainWindow };
        theme.Track(dialog);
        dialog.ShowDialog();
    }
}
