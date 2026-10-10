using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

// Each screen shows its empty state from the mockup. The library and search are in LibraryViewModel.

/// <summary>Shared by pages whose empty state depends on whether any folders have been added.</summary>
public abstract partial class LibraryAwarePageViewModel(SourceRootStore roots, LibraryActivity activity) : PageViewModel
{
    public LibraryActivity Activity { get; } = activity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolders))]
    public partial int FolderCount { get; set; }

    public bool HasFolders => FolderCount > 0;

    public override async Task LoadAsync() => FolderCount = (await roots.ListAsync()).Count;
}
