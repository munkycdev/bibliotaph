using System.ComponentModel;
using System.Diagnostics;

namespace Bibliotaph.App.Services;

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

public sealed class WindowsDesktopShell : IDesktopShell
{
    public void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();

    public Exception? Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ex;
        }
    }
}
