using System.Windows;
using Bibliotaph.App.Views;

namespace Bibliotaph.App.Services;

/// <summary>Opens the About popup over the main window, from its system menu or the Settings page.</summary>
public sealed class AboutBox(ThemeService theme)
{
    public void Show()
    {
        var dialog = new AboutDialog(AboutInfo.ForThisApp()) { Owner = Application.Current.MainWindow };
        theme.Track(dialog);
        dialog.ShowDialog();
    }
}
