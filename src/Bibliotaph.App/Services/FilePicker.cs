using Microsoft.Win32;

namespace Bibliotaph.App.Services;

/// <summary>
/// The Windows pickers for files and folders. An interface so view models can be tested without them. A filter is in
/// the pickers' own form, such as <c>ZIP files (*.zip)|*.zip</c>.
/// </summary>
public interface IFilePicker
{
    /// <summary>A file to open; null when cancelled.</summary>
    string? OpenFile(string title, string filter, string? initialFolder = null);

    /// <summary>Where to save a file, offering <paramref name="suggestedName"/>; null when cancelled.</summary>
    string? SaveFile(string title, string suggestedName, string filter, string defaultExtension);

    /// <summary>One folder; null when cancelled.</summary>
    string? PickFolder(string title);

    /// <summary>Any number of folders; empty when cancelled.</summary>
    IReadOnlyList<string> PickFolders(string title);
}

public sealed class WindowsFilePicker : IFilePicker
{
    public string? OpenFile(string title, string filter, string? initialFolder = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        if (initialFolder is not null) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(WpfUserPrompts.Owner()) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string suggestedName, string filter, string defaultExtension)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggestedName, DefaultExt = defaultExtension, Filter = filter };
        return dialog.ShowDialog(WpfUserPrompts.Owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog(WpfUserPrompts.Owner()) == true ? dialog.FolderName : null;
    }

    public IReadOnlyList<string> PickFolders(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = true };
        return dialog.ShowDialog(WpfUserPrompts.Owner()) == true ? dialog.FolderNames : [];
    }
}
