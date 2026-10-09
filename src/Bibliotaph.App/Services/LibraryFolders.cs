using System.Windows;
using Microsoft.Win32;

namespace Bibliotaph.App.Services;

/// <summary>
/// Choosing folders to add. Adding happens in Settings > Library, after a preview of what each folder holds,
/// so other pages ask for the picker there with <see cref="RequestPick"/>.
/// </summary>
public sealed class LibraryFolders(SettingsLinks settings)
{
    bool _pickRequested;

    /// <summary>The native folder picker; empty when cancelled.</summary>
    public static IReadOnlyList<string> Pick()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add folders to your library",
            Multiselect = true,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FolderNames : [];
    }

    /// <summary>Opens Settings > Library and shows the picker there.</summary>
    public void RequestPick()
    {
        _pickRequested = true;
        settings.Open(SettingsSection.Library);
    }

    /// <summary>Opens Settings > Library, where the folders are listed.</summary>
    public void Manage() => settings.Open(SettingsSection.Library);

    /// <summary>True once after <see cref="RequestPick"/>.</summary>
    public bool TakePickRequest()
    {
        var requested = _pickRequested;
        _pickRequested = false;
        return requested;
    }
}
