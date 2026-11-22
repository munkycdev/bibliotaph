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

    /// <summary>A question with two ways to go ahead: true for Yes, false for No, null for Cancel, which is the default.</summary>
    bool? Choose(string message, string title, PromptKind kind);

    /// <summary>A message with OK to close it.</summary>
    void Tell(string message, string title, PromptKind kind);
}
