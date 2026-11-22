using Bibliotaph.Presentation.Services;

namespace Bibliotaph.App.Services;

/// <summary><see cref="IAppDialogs"/> as WPF windows.</summary>
public sealed class WpfAppDialogs(AboutBox about, ShortcutsBox shortcuts, AiTestBox tester) : IAppDialogs
{
    public void ShowAbout() => about.Show();

    public void ShowShortcuts() => shortcuts.Show();

    public void TestModelServer(string endpoint, string model) => tester.Show(endpoint, model);

    public void TestBuiltInModel(string model) => tester.ShowBuiltIn(model);
}
