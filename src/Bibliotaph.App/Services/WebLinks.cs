using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace Bibliotaph.App.Services;

/// <summary>
/// Opening a web address in the browser, and only once the user says yes: a PDF's web links and a book's Get it from
/// links ask the same question.
/// </summary>
public static class WebLinks
{
    /// <summary>
    /// Asks whether to open <paramref name="uri"/>, then opens it. False when the user said no, or when Windows couldn't
    /// open it, which <paramref name="failed"/> then says.
    /// </summary>
    public static bool AskAndOpen(string uri, out Exception? failed)
    {
        failed = null;
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var answer = MessageBox.Show(owner, $"This link goes to a web page:\n\n{uri}\n\nOpen it in your browser?", "Open a web link?",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            failed = ex;
            return false;
        }
    }
}
