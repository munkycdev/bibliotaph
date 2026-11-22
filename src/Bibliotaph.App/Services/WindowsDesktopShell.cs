using System.ComponentModel;
using System.Diagnostics;
using Bibliotaph.Presentation.Services;

namespace Bibliotaph.App.Services;

/// <summary>File Explorer and the apps Windows has for each type of file.</summary>
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
