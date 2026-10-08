using System.Windows;
using Bibliotaph.Catalog;
using Microsoft.Win32;

namespace Bibliotaph.App.Services;

/// <summary>Adding folders to the library: the native folder picker, then the catalog. Nothing is scanned yet.</summary>
public sealed class LibraryFolders(SourceRootStore roots)
{
    /// <returns>True when at least one folder was added.</returns>
    public async Task<bool> PickAndAddAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Add folders to your library",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Application.Current.MainWindow) != true) return false;
        foreach (var folder in dialog.FolderNames) await roots.AddAsync(folder);
        return dialog.FolderNames.Length > 0;
    }
}
