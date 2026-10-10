using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    readonly INavigationService _navigation;
    readonly ThemeService _theme;
    readonly SearchState _search;
    readonly SettingsLinks _settings;
    readonly LibraryPages _library;
    readonly SmartViewDirectory _views;
    readonly SettingsStore _store;
    readonly ILogger<ShellViewModel> _log;
    readonly DispatcherTimer _searchDelay;

    public ShellViewModel(INavigationService navigation, ThemeService theme, LibraryActivity activity, SearchState search, SettingsLinks settings,
        LibraryPages library, SmartViewDirectory views, SettingsStore store, IUpdateService updates, ILogger<ShellViewModel> log)
    {
        Updates = updates;
        _library = library;
        _views = views;
        _store = store;
        _navigation = navigation;
        _theme = theme;
        Activity = activity;
        _search = search;
        _settings = settings;
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
        // Smart Views (slice 3 plan, choices 5 and 17): Favorites first, then the saved views by name.
        SmartViews = [new(Route.Library, LibraryScope.Favorites.Name, Icon("Icon.Heart"), LibraryScope.Favorites)];
        _views.Changed += (_, _) => ShowSmartViews();
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

    /// <summary>The sidebar's Smart Views group: parts of the Library, each opening it scoped.</summary>
    public ObservableCollection<NavItemViewModel> SmartViews { get; }

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

    /// <summary>An update downloaded in the background, which the sidebar offers once it is ready (slice 4l).</summary>
    public IUpdateService Updates { get; }

    /// <summary>The sidebar's "Update ready: restart to update". Nothing restarts until it is clicked.</summary>
    [RelayCommand]
    void RestartToUpdate() => Updates.RestartToUpdate();

    public string AppearanceToggleLabel => _theme.IsDark ? "Switch to light appearance" : "Switch to dark appearance";

    public async Task StartAsync()
    {
        _theme.Changed += (_, _) => OnPropertyChanged(nameof(AppearanceToggleLabel));
        // Settings > Library > Start on (slice 3 plan, choice 7): Home unless the Library was chosen.
        await _views.LoadAsync();
        var start = await StartPageAsync();
        _navigation.NavigateTo(start);
        Activity.Start();
    }

    async Task<Route> StartPageAsync()
    {
        try
        {
            return await _store.GetAsync(SettingKeys.StartPage) == nameof(Route.Library) ? Route.Library : Route.Home;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reading the start page failed");
            return Route.Home;
        }
    }

    [RelayCommand]
    void Navigate(Route route)
    {
        // The Library from the sidebar is the whole library, even while a part of it is shown.
        if (route == Route.Library) _library.Open(null);
        else _navigation.NavigateTo(route);
    }

    /// <summary>A sidebar item: its page, or for a Smart View the Library scoped to it or showing it.</summary>
    [RelayCommand]
    void Open(NavItemViewModel item)
    {
        if (item.ViewId is { } id)
        {
            if (_views.Find(id) is { } view) _library.OpenView(view);
        }
        else if (item.Scope is { } scope) _library.Open(scope);
        else Navigate(item.Route);
    }

    /// <summary>A saved view's right-click Rename…: the view opens, and the Library asks for the new name.</summary>
    [RelayCommand]
    void RenameView(NavItemViewModel item)
    {
        if (ShowView(item) is { } page) page.RenameViewCommand.Execute(null);
    }

    /// <summary>A saved view's right-click Delete: the view opens and is deleted there, where Undo is offered.</summary>
    [RelayCommand]
    void DeleteView(NavItemViewModel item)
    {
        if (ShowView(item) is { } page) page.DeleteViewCommand.Execute(null);
    }

    LibraryViewModel? ShowView(NavItemViewModel item)
    {
        Open(item);
        return CurrentPage is LibraryViewModel { ActiveView: { } shown } page && shown.Id == item.ViewId ? page : null;
    }

    /// <summary>The saved views after Favorites, in the order the directory lists them.</summary>
    void ShowSmartViews()
    {
        while (SmartViews.Count > 1) SmartViews.RemoveAt(SmartViews.Count - 1);
        foreach (var view in _views.All)
            SmartViews.Add(new NavItemViewModel(Route.Library, view.Name, Icon("Icon.SlidersHorizontal"), viewId: view.Id));
        ShowActiveItem();
    }

    [RelayCommand]
    void GoBack() => _navigation.GoBack();

    /// <summary>The sidebar's status line: Settings > Processing, where the details and pause buttons are.</summary>
    [RelayCommand]
    void OpenProcessing() => _settings.Open(SettingsSection.Processing);

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
            _shown?.PropertyChanged -= OnPagePropertyChanged;
            _shown = CurrentPage;
            _shown?.PropertyChanged += OnPagePropertyChanged;
        }
        ShowActiveItem();
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

    /// <summary>
    /// Marks the sidebar item for the page shown: Favorites, not Library, for the Library scoped to favorites, and
    /// Collections for a collection's.
    /// </summary>
    void ShowActiveItem()
    {
        foreach (var item in NavItems.Concat(SmartViews).Append(Settings))
            item.IsActive = item.Route == CurrentPage?.NavRoute && item.ScopeKey == CurrentPage?.NavScope;
    }

    void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.NavScope) or nameof(PageViewModel.NavRoute) or nameof(PageViewModel.Title) or nameof(PageViewModel.Section))
        {
            ShowActiveItem();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Section));
            OpenSectionCommand.NotifyCanExecuteChanged();
        }
    }

    static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
}
