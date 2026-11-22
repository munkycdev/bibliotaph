namespace Bibliotaph.Presentation.Services;

/// <summary>
/// The pickers for files and folders. An interface so view models can be tested without them. A filter is in
/// the Windows pickers' form, such as <c>ZIP files (*.zip)|*.zip</c>.
/// </summary>
public interface IFilePicker
{
    /// <summary>A file to open; null when cancelled.</summary>
    string? OpenFile(string title, string filter, string? initialFolder = null);

    /// <summary>Where to save a file, offering <paramref name="suggestedName"/>; null when cancelled.</summary>
    string? SaveFile(string title, string suggestedName, string filter, string defaultExtension, string? initialFolder = null);

    /// <summary>One folder; null when cancelled.</summary>
    string? PickFolder(string title);

    /// <summary>Any number of folders; empty when cancelled.</summary>
    IReadOnlyList<string> PickFolders(string title);
}
