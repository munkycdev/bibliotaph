using Bibliotaph.Presentation.Services;
using Microsoft.Win32;

namespace Bibliotaph.App.Services;

/// <summary>The Windows pickers for files and folders.</summary>
public sealed class WindowsFilePicker : IFilePicker
{
    public string? OpenFile(string title, string filter, string? initialFolder = null)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        if (initialFolder is not null) dialog.InitialDirectory = initialFolder;
        return dialog.ShowDialog(WpfUserPrompts.Owner()) == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string suggestedName, string filter, string defaultExtension, string? initialFolder = null)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = suggestedName, DefaultExt = defaultExtension, Filter = filter };
        if (initialFolder is not null) dialog.InitialDirectory = initialFolder;
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
