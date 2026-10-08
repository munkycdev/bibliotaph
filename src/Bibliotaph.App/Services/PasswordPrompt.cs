using System.Windows;
using Bibliotaph.App.Views;

namespace Bibliotaph.App.Services;

/// <summary>A password typed by the reader, and whether to remember it.</summary>
public sealed record EnteredPassword(string Password, bool Remember);

/// <summary>Asks the reader for a PDF's password. An interface so the smoke test can answer for them.</summary>
public interface IPasswordPrompt
{
    /// <summary>Null when the reader cancels.</summary>
    EnteredPassword? Ask(string title, bool retry);
}

public sealed class PasswordPrompt : IPasswordPrompt
{
    public EnteredPassword? Ask(string title, bool retry)
    {
        var dialog = new PasswordDialog(title, retry) { Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? new EnteredPassword(dialog.EnteredPassword, dialog.RememberPassword) : null;
    }
}
