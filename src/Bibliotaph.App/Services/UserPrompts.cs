using System.Windows;

namespace Bibliotaph.App.Services;

/// <summary>What a prompt is about, which picks its icon.</summary>
public enum PromptKind
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>Asks the user something, or tells them, in a message box. An interface so view models can be tested without one.</summary>
public interface IUserPrompts
{
    /// <summary>OK or Cancel; true for OK. <paramref name="okByDefault"/> makes Enter mean OK.</summary>
    bool Confirm(string message, string title, PromptKind kind, bool okByDefault);

    /// <summary>A message with OK to close it.</summary>
    void Tell(string message, string title, PromptKind kind);
}

public sealed class WpfUserPrompts : IUserPrompts
{
    public bool Confirm(string message, string title, PromptKind kind, bool okByDefault) =>
        Show(message, title, MessageBoxButton.OKCancel, kind, okByDefault ? MessageBoxResult.OK : MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void Tell(string message, string title, PromptKind kind) => Show(message, title, MessageBoxButton.OK, kind, MessageBoxResult.None);

    /// <summary>
    /// The window a prompt or picker belongs over: the active one, which may be a pop-out reader, else the main window.
    /// </summary>
    internal static Window? Owner() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, PromptKind kind, MessageBoxResult byDefault)
    {
        var icon = kind switch
        {
            PromptKind.Question => MessageBoxImage.Question,
            PromptKind.Warning => MessageBoxImage.Warning,
            PromptKind.Error => MessageBoxImage.Error,
            _ => MessageBoxImage.Information,
        };
        // MessageBox can't take a null owner; without one it sits over whatever window Windows says is active.
        return Owner() is { } owner
            ? MessageBox.Show(owner, message, title, buttons, icon, byDefault)
            : MessageBox.Show(message, title, buttons, icon, byDefault);
    }
}
