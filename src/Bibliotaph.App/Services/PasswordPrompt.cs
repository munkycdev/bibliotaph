using Bibliotaph.App.Views;
using Bibliotaph.Presentation.Services;

namespace Bibliotaph.App.Services;

public sealed class PasswordPrompt : IPasswordPrompt
{
    public EnteredPassword? Ask(string title, bool retry)
    {
        // Over the window being read, which may be a pop-out.
        var owner = WpfUserPrompts.Owner();
        var dialog = new PasswordDialog(title, retry) { Owner = owner };
        return dialog.ShowDialog() == true ? new EnteredPassword(dialog.EnteredPassword, dialog.RememberPassword, dialog.MakeSearchable) : null;
    }
}

/// <summary>The smoke test's answer to a password prompt: what it set before opening the book, or Cancel.</summary>
public sealed class SmokePasswordPrompt : IPasswordPrompt
{
    public EnteredPassword? Answer { get; set; }

    public int Asked { get; private set; }

    public EnteredPassword? Ask(string title, bool retry)
    {
        Asked++;
        return retry ? null : Answer;
    }
}
