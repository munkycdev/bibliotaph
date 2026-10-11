using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
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
    readonly PinDirectory _pins;
    readonly PinOpener _opener;
    readonly CollectionDirectory _collections;
    readonly SessionDirectory _binders;
    readonly SettingsStore _store;
    readonly LibrarySync _sync;
    readonly ILogger<ShellViewModel> _log;
    readonly DispatcherTimer _searchDelay;

    /// <summary>The pin just moved, which gets the keyboard back once the sidebar shows it in its new place.</summary>
    PinTarget? _focusAfterMove;

    public ShellViewModel(INavigationService navigation, ThemeService theme, LibraryActivity activity, SearchState search, SettingsLinks settings,
        LibraryPages library, SmartViewDirectory views, PinDirectory pins, PinOpener opener, CollectionDirectory collections, SessionDirectory binders,
        SettingsStore store, IUpdateService updates, LibrarySync sync, ILogger<ShellViewModel> log)
    {
        _sync = sync;
        _pins = pins;
        _opener = opener;
        _collections = collections;
        _binders = binders;
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
            new(Route.Sessions, "Binders", Icon("Icon.NotebookTabs")),
            new(Route.NeedsReview, "Needs review", Icon("Icon.Inbox")),
        ];
        _pins.Changed += (_, _) =>
        {
            ShowActiveItem();
            // The reload after a move has the pins in their new places, so the keyboard can go back to the one moved.
            if (_focusAfterMove is { } moved)
            {
                _focusAfterMove = null;
                FocusPinRequested?.Invoke(this, moved);
            }
            MovePinUpCommand.NotifyCanExecuteChanged();
            MovePinDownCommand.NotifyCanExecuteChanged();
        };
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

    /// <summary>The sidebar's Pinned group (Pinned Views plan): books, Favorites, collections, binders and saved searches, in order.</summary>
    public ObservableCollection<PinnedItemViewModel> Pinned => _pins.Items;

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

    async Task ReloadMergedAsync()
    {
        try
        {
            await _views.LoadAsync();
            await _collections.LoadAsync();
            await _binders.LoadAsync();
            await _pins.LoadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Showing the work merged from another PC failed");
        }
    }

    public async Task StartAsync()
    {
        _theme.Changed += (_, _) => OnPropertyChanged(nameof(AppearanceToggleLabel));
        // Settings > Library > Start on (slice 3 plan, choice 7): Home unless the Library was chosen.
        // The pins show their targets' names, so those load first.
        await _views.LoadAsync();
        await _collections.LoadAsync();
        await _binders.LoadAsync();
        await _pins.LoadAsync();
        // Another PC's collections, binders, saved searches and pins, once merged in; the library refreshes on its own.
        var dispatcher = Dispatcher.CurrentDispatcher;
        _sync.Merged += (_, _) => dispatcher.InvokeAsync(ReloadMergedAsync);
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

    /// <summary>A sidebar item: its page.</summary>
    [RelayCommand]
    void Open(NavItemViewModel item) => Navigate(item.Route);

    /// <summary>A pin (Pinned Views plan, choice 6).</summary>
    [RelayCommand]
    Task OpenPin(PinnedItemViewModel item) => _opener.OpenAsync(item);

    /// <summary>A pin's right-click Unpin.</summary>
    [RelayCommand]
    Task Unpin(PinnedItemViewModel item) => _pins.UnpinAsync(item.Target);

    /// <summary>Raised after a pin moved, so the sidebar puts the keyboard back on it in its new place.</summary>
    public event EventHandler<PinTarget>? FocusPinRequested;

    /// <summary>A pin's Move up, from its menu or Alt+Up (Pinned Views plan, choice 8).</summary>
    [RelayCommand(CanExecute = nameof(CanMovePinUp))]
    Task MovePinUp(PinnedItemViewModel? item) => item is null ? Task.CompletedTask : MovePinAsync(item, Pinned.IndexOf(item) - 1);

    bool CanMovePinUp(PinnedItemViewModel? item) => item is not null && Pinned.IndexOf(item) > 0;

    /// <summary>A pin's Move down, from its menu or Alt+Down.</summary>
    [RelayCommand(CanExecute = nameof(CanMovePinDown))]
    Task MovePinDown(PinnedItemViewModel? item) => item is null ? Task.CompletedTask : MovePinAsync(item, Pinned.IndexOf(item) + 1);

    bool CanMovePinDown(PinnedItemViewModel? item) => item is not null && Pinned.IndexOf(item) is >= 0 and var at && at < Pinned.Count - 1;

    /// <summary>Moves a pin to that place in the sidebar, counted once it has been taken out, and keeps the keyboard on it.</summary>
    public async Task MovePinAsync(PinnedItemViewModel item, int index)
    {
        _focusAfterMove = item.Target;
        if (!await _pins.MoveAsync(item.Target, index)) _focusAfterMove = null;
    }

    /// <summary>A saved search's right-click Rename…: the search opens, and the Library asks for the new name.</summary>
    [RelayCommand]
    async Task RenameView(PinnedItemViewModel item)
    {
        if (await ShowViewAsync(item) is { } page) page.RenameViewCommand.Execute(null);
    }

    /// <summary>A saved search's right-click Delete: the search opens and is deleted there, where Undo is offered.</summary>
    [RelayCommand]
    async Task DeleteView(PinnedItemViewModel item)
    {
        if (await ShowViewAsync(item) is { } page) page.DeleteViewCommand.Execute(null);
    }

    async Task<LibraryViewModel?> ShowViewAsync(PinnedItemViewModel item)
    {
        await OpenPin(item);
        return CurrentPage is LibraryViewModel { ActiveView: { } shown } page && shown.Id == item.Target.Id ? page : null;
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
    /// Marks the sidebar item for the page shown: its pin when it has one, as for Favorites, a pinned collection or a
    /// saved search; otherwise its route's item, as Collections for a collection that isn't pinned.
    /// </summary>
    void ShowActiveItem()
    {
        var key = CurrentPage?.PinKey;
        var pinned = false;
        foreach (var pin in Pinned) pinned |= pin.IsActive = key is not null && pin.Key == key;
        foreach (var item in NavItems.Append(Settings))
            item.IsActive = !pinned && item.Route == CurrentPage?.NavRoute;
    }

    void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.NavRoute) or nameof(PageViewModel.PinKey) or nameof(PageViewModel.Title) or nameof(PageViewModel.Section))
        {
            ShowActiveItem();
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Section));
            OpenSectionCommand.NotifyCanExecuteChanged();
        }
    }

    static Geometry Icon(string key) => (Geometry)Application.Current.FindResource(key);
}
