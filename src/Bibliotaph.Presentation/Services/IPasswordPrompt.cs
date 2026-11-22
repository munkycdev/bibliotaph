namespace Bibliotaph.Presentation.Services;

/// <summary>
/// A password typed by the reader, whether to remember it, and whether indexing may use it this sitting to make the
/// book's text searchable. A remembered password always does.
/// </summary>
public sealed record EnteredPassword(string Password, bool Remember, bool Searchable = true);

/// <summary>Asks the reader for a PDF's password. An interface so the smoke test can answer for them.</summary>
public interface IPasswordPrompt
{
    /// <summary>Null when the reader cancels.</summary>
    EnteredPassword? Ask(string title, bool retry);
}
