using System.Windows;
using Bibliotaph.App.ViewModels;
using Bibliotaph.App.Views;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>Opens Test with a book over the main window, for the endpoint and model on screen.</summary>
public sealed class AiTestBox(AiService ai, ThemeService theme)
{
    public void Show(string endpoint, string model)
    {
        var dialog = new AiTestDialog(new AiTestViewModel(ai, endpoint, model)) { Owner = Application.Current.MainWindow };
        theme.Track(dialog);
        dialog.ShowDialog();
    }
}
