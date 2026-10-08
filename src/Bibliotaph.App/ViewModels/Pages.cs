using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

// Slice 0 screens: each shows its empty state from the mockup. Nothing scans yet, so the library is
// always empty; the folders the user adds are saved for slice 1 to scan.

/// <summary>Shared by pages whose empty state depends on whether any folders have been added.</summary>
public abstract partial class LibraryAwarePageViewModel(SourceRootStore roots) : PageViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolders), nameof(FolderSummary))]
    public partial int FolderCount { get; set; }

    public bool HasFolders => FolderCount > 0;

    public string FolderSummary => FolderCount == 1 ? "One folder is waiting" : $"{FolderCount} folders are waiting";

    public override async Task LoadAsync() => FolderCount = (await roots.ListAsync()).Count;
}

public sealed partial class HomeViewModel(SourceRootStore roots, LibraryFolders folders, INavigationService navigation)
    : LibraryAwarePageViewModel(roots)
{
    public override Route Route => Route.Home;
    public override string Title => "Home";

    [RelayCommand]
    async Task AddFolder()
    {
        if (await folders.PickAndAddAsync()) navigation.NavigateTo(Route.LibraryFolders);
    }

    [RelayCommand]
    void ManageFolders() => navigation.NavigateTo(Route.LibraryFolders);
}

public sealed partial class LibraryViewModel(SourceRootStore roots, LibraryFolders folders, INavigationService navigation)
    : LibraryAwarePageViewModel(roots)
{
    public override Route Route => Route.Library;
    public override string Title => "Library";

    [RelayCommand]
    async Task AddFolder()
    {
        if (await folders.PickAndAddAsync()) await LoadAsync();
    }

    [RelayCommand]
    void ManageFolders() => navigation.NavigateTo(Route.LibraryFolders);
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

public sealed partial class NeedsReviewViewModel(INavigationService navigation) : PageViewModel
{
    public override Route Route => Route.NeedsReview;
    public override string Title => "Needs review";

    [RelayCommand]
    void ReturnToLibrary() => navigation.NavigateTo(Route.Library);
}
