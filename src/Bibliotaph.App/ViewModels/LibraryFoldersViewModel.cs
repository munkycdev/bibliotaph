using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

public sealed record LibraryFolderItem(long Id, string Path, string Detail);

/// <summary>The folders Bibliotaph reads from. Removing one keeps its catalog rows and the user's work.</summary>
public sealed partial class LibraryFoldersViewModel(SourceRootStore roots, LibraryFolders folders) : PageViewModel
{
    public override Route Route => Route.LibraryFolders;
    public override string Section => "Settings";
    public override string Title => "Library folders";

    public ObservableCollection<LibraryFolderItem> Folders { get; } = [];

    [ObservableProperty]
    public partial bool HasFolders { get; set; }

    public override async Task LoadAsync()
    {
        Folders.Clear();
        foreach (var root in await roots.ListAsync())
        {
            var added = root.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
            Folders.Add(new LibraryFolderItem(root.Id, root.Path, $"Added {added} · Not scanned yet"));
        }
        HasFolders = Folders.Count > 0;
    }

    [RelayCommand]
    async Task AddFolder()
    {
        if (await folders.PickAndAddAsync()) await LoadAsync();
    }

    [RelayCommand]
    async Task Remove(LibraryFolderItem folder)
    {
        await roots.RemoveAsync(folder.Id);
        await LoadAsync();
    }
}
