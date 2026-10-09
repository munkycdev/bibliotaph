using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    readonly INavigationService _navigation;
    readonly ThemeService _theme;
    readonly SearchState _search;
    readonly ILogger<ShellViewModel> _log;
    readonly DispatcherTimer _searchDelay;

    public ShellViewModel(INavigationService navigation, ThemeService theme, LibraryActivity activity, SearchState search, ILogger<ShellViewModel> log)
    {
        _navigation = navigation;
        _theme = theme;
        Activity = activity;
        _search = search;
        _log = log;
        // Search as you type, once typing pauses.
        _searchDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Input, (_, _) => SubmitSearch(), Dispatcher.CurrentDispatcher);
        _searchDelay.Stop();
        _search.PropertyChanged += OnSearchChanged;

        NavItems =
        [
            new(Route.Home, "Home", Icon("Icon.House")),
            new(Route.Library, "Library", Icon("Icon.LibraryBig")),
            new(Route.Collections, "Collections", Icon("Icon.Folders")),
            new(Route.Sessions, "Sessions", Icon("Icon.NotebookTabs")),
            new(Route.NeedsReview, "Needs review", Icon("Icon.Inbox")),
        ];
        Settings = new NavItemViewModel(Route.Settings, "Settings", Icon("Icon.Settings2"));
        var review = NavItems.Single(n => n.Route == Route.NeedsReview);
        Activity.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryActivity.ReviewCount))
                review.Count = Activity.ReviewCount > 0 ? Activity.ReviewCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture) : "";
        };
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

    partial void OnSearchTextChanged(string value)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    /// <summary>Searches now (Enter, or a pause in typing) and shows the results in the Library.</summary>
    [RelayCommand]
    void SubmitSearch()
    {
        _searchDelay.Stop();
        _search.Search(SearchText);
        if (_search.IsSearching && CurrentPage?.Route != Route.Library) _navigation.NavigateTo(Route.Library);
    }

    /// <summary>Empties the box and ends the search at once (the box's ×, or Esc).</summary>
    [RelayCommand]
    void ClearSearch()
    {
        SearchText = "";
        SubmitSearch();
    }

    /// <summary>Keeps the box in step when a page clears the search ("Clear search").</summary>
    void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchState.Text) || SearchText.Trim() == _search.Text) return;
        SearchText = _search.Text;
        _searchDelay.Stop();
    }

    /// <summary>Indexing progress for the sidebar status line.</summary>
    public LibraryActivity Activity { get; }

    public string AppearanceToggleLabel => _theme.IsDark ? "Switch to light appearance" : "Switch to dark appearance";

    public Task StartAsync()
    {
        _theme.Changed += (_, _) => OnPropertyChanged(nameof(AppearanceToggleLabel));
        _navigation.NavigateTo(Route.Home);
        Activity.Start();
        return Task.CompletedTask;
    }

    [RelayCommand]
    void Navigate(Route route) => _navigation.NavigateTo(route);

    [RelayCommand]
    void GoBack() => _navigation.GoBack();

    /// <summary>The breadcrumb's parent, as it was left if it is in the history.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSection))]
    void OpenSection()
    {
        if (CurrentPage?.SectionRoute is { } route) _navigation.NavigateUp(route);
    }

    bool CanOpenSection() => CurrentPage?.SectionRoute is not null;

    [RelayCommand]
    Task ToggleAppearance() => _theme.ToggleAsync();

    PageViewModel? _shown;

    async void OnNavigated()
    {
        if (!ReferenceEquals(_shown, CurrentPage))
        {
            _shown?.Unload();
            _shown = CurrentPage;
        }
        foreach (var item in NavItems.Append(Settings))
            item.IsActive = item.Route == CurrentPage?.Route
                || (item.Route == Route.Settings && CurrentPage?.Route is Route.LibraryFolders or Route.Vocabulary);
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.NotifyCanExecuteChanged();
        OpenSectionCommand.NotifyCanExecuteChanged();

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
