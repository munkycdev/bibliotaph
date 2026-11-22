using System.Windows;
using Bibliotaph.Presentation.Services;

namespace Bibliotaph.App.Services;

public sealed class WpfUserPrompts : IUserPrompts
{
    public bool Confirm(string message, string title, PromptKind kind, bool okByDefault) =>
        Show(message, title, MessageBoxButton.OKCancel, kind, okByDefault ? MessageBoxResult.OK : MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public bool? Choose(string message, string title, PromptKind kind) =>
        Show(message, title, MessageBoxButton.YesNoCancel, kind, MessageBoxResult.Cancel) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

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
