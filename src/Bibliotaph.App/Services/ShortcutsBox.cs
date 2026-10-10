using System.Windows;
using Bibliotaph.App.Views;

namespace Bibliotaph.App.Services;

/// <summary>Opens the Keyboard shortcuts popup over the window it was asked from: the main window or a pop-out reader.</summary>
public sealed class ShortcutsBox(ThemeService theme)
{
    public void Show(Window owner)
    {
        var dialog = new ShortcutsDialog { Owner = owner };
        theme.Track(dialog);
        dialog.ShowDialog();
    }
}
