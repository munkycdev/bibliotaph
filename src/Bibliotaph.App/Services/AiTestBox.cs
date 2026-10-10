using System.Windows;
using Bibliotaph.App.ViewModels;
using Bibliotaph.App.Views;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>Opens Test with a book over the main window, for the endpoint and model on screen or the built-in AI.</summary>
public sealed class AiTestBox(AiService ai, ThemeService theme)
{
    public void Show(string endpoint, string model) => Show(new AiTestViewModel(ai, (progress, ct) => ai.TestAsync(endpoint, model, progress, ct), model));

    /// <summary>Tests the built-in AI on <paramref name="model"/>, a model's id from <see cref="BuiltInModels"/>.</summary>
    public void ShowBuiltIn(string model) =>
        Show(new AiTestViewModel(ai, (progress, ct) => ai.TestBuiltInAsync(model, progress, ct), BuiltInModels.NameFor(model)));

    void Show(AiTestViewModel test)
    {
        var dialog = new AiTestDialog(test) { Owner = Application.Current.MainWindow };
        theme.Track(dialog);
        dialog.ShowDialog();
    }
}
