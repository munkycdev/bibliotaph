using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Bibliotaph.App.Services;
using Bibliotaph.Index;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    readonly INavigationService _navigation;
    readonly ThemeService _theme;
    readonly IndexQueries _index;
    readonly ILogger<ShellViewModel> _log;

    public ShellViewModel(INavigationService navigation, ThemeService theme, IndexQueries index, ILogger<ShellViewModel> log)
    {
        _navigation = navigation;
        _theme = theme;
        _index = index;
        _log = log;

        NavItems =
        [
            new(Route.Home, "Home", Icon("Icon.House")),
            new(Route.Library, "Library", Icon("Icon.LibraryBig")),
            new(Route.Collections, "Collections", Icon("Icon.Folders")),
            new(Route.Sessions, "Sessions", Icon("Icon.NotebookTabs")),
            new(Route.NeedsReview, "Needs review", Icon("Icon.Inbox")),
        ];
        Settings = new NavItemViewModel(Route.Settings, "Settings", Icon("Icon.Settings2"));
        _navigation.Navigated += (_, _) => OnNavigated();
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    public NavItemViewModel Settings { get; }

    public PageViewModel? CurrentPage => _navigation.Current;

    public string Section => CurrentPage?.Section ?? "Workspace";

    public string Title => CurrentPage?.Title ?? "";

    public bool CanGoBack => _navigation.CanGoBack;

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string ProcessingStatus { get; set; } = "Nothing to process";

    public string AppearanceToggleLabel => _theme.IsDark ? "Switch to light appearance" : "Switch to dark appearance";

    public async Task StartAsync()
    {
        _theme.Changed += (_, _) => OnPropertyChanged(nameof(AppearanceToggleLabel));
        _navigation.NavigateTo(Route.Home);
        await RefreshProcessingStatusAsync();
    }

    [RelayCommand]
    void Navigate(Route route) => _navigation.NavigateTo(route);

    [RelayCommand]
    void GoBack() => _navigation.GoBack();

    [RelayCommand]
    Task ToggleAppearance() => _theme.ToggleAsync();

    async Task RefreshProcessingStatusAsync()
    {
        var queue = await _index.GetQueueSummaryAsync();
        ProcessingStatus = queue.IsIdle ? "Nothing to process" : $"{queue.Pending:N0} waiting";
    }

    async void OnNavigated()
    {
        foreach (var item in NavItems.Append(Settings))
            item.IsActive = item.Route == CurrentPage?.Route
                || (item.Route == Route.Settings && CurrentPage?.Route == Route.LibraryFolders);
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.NotifyCanExecuteChanged();

        try
        {
            if (CurrentPage is { } page) await page.LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading {Route} failed", CurrentPage?.Route);
        }
    }

    static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
}
