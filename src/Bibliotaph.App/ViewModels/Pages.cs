using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

public sealed partial class HomeViewModel(SourceRootStore roots, LibraryActivity activity, LibraryFolders folders)
    : LibraryAwarePageViewModel(roots, activity)
{
    public override Route Route => Route.Home;
    public override string Title => "Home";

    [RelayCommand]
    void AddFolder() => folders.RequestPick();

    [RelayCommand]
    void ManageFolders() => folders.Manage();
}

public sealed partial class CollectionsViewModel(INavigationService navigation) : PageViewModel
{
    public override Route Route => Route.Collections;
    public override string Title => "Collections";

    [RelayCommand]
    void BrowseLibrary() => navigation.NavigateTo(Route.Library);
}

public sealed partial class SessionsViewModel(INavigationService navigation) : PageViewModel
{
    public override Route Route => Route.Sessions;
    public override string Title => "Sessions";

    [RelayCommand]
    void BrowseLibrary() => navigation.NavigateTo(Route.Library);
}
