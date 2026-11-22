namespace Bibliotaph.App.Services;

/// <summary>The app's own small windows a page opens: About, the keyboard shortcuts, and a test of an AI model.</summary>
public interface IAppDialogs
{
    void ShowAbout();

    void ShowShortcuts();

    /// <summary>Tests <paramref name="model"/> on a model server at <paramref name="endpoint"/> with a sample page.</summary>
    void TestModelServer(string endpoint, string model);

    /// <summary>Tests a built-in model by its id with a sample page.</summary>
    void TestBuiltInModel(string model);
}

/// <summary><see cref="IAppDialogs"/> as WPF windows.</summary>
public sealed class WpfAppDialogs(AboutBox about, ShortcutsBox shortcuts, AiTestBox tester) : IAppDialogs
{
    public void ShowAbout() => about.Show();

    public void ShowShortcuts() => shortcuts.Show();

    public void TestModelServer(string endpoint, string model) => tester.Show(endpoint, model);

    public void TestBuiltInModel(string model) => tester.ShowBuiltIn(model);
}
