using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
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

public sealed partial class HomeViewModel(SourceRootStore roots, LibraryActivity activity, LibraryFolders folders, INavigationService navigation,
    SettingsStore settings, AiService ai)
    : LibraryAwarePageViewModel(roots, activity)
{
    public override Route Route => Route.Home;
    public override string Title => "Home";

    /// <summary>The first-run "AI now or later" step (choice 10): folders added, no endpoint, and not yet answered.</summary>
    [ObservableProperty]
    public partial bool ShowAiStep { get; private set; }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        ShowAiStep = HasFolders && ai.Setup.Endpoint is null && await settings.GetAsync(SettingKeys.AiAsked) is null;
    }

    [RelayCommand]
    void SetUpAi() => navigation.NavigateTo(Route.Ai);

    [RelayCommand]
    async Task AiLaterAsync()
    {
        ShowAiStep = false;
        await settings.SetAsync(SettingKeys.AiAsked, bool.TrueString);
    }

    [RelayCommand]
    void AddFolder() => folders.RequestPick();

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
