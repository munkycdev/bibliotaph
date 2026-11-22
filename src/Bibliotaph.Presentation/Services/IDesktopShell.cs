namespace Bibliotaph.Presentation.Services;

/// <summary>File Explorer, the browser and the user's other apps. An interface so view models can be tested without them.</summary>
public interface IDesktopShell
{
    /// <summary>Opens File Explorer with <paramref name="path"/> selected in the folder it is in.</summary>
    void ShowInFolder(string path);

    /// <summary>Opens File Explorer at <paramref name="folder"/>.</summary>
    void OpenFolder(string folder);

    /// <summary>
    /// Opens a web address in the browser, or a file in the app Windows has for its type. Returns why Windows couldn't,
    /// or null once it has it.
    /// </summary>
    Exception? Open(string target);
}
