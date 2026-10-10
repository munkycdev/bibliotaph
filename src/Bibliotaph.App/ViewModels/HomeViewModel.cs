using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Home (slice 3 plan, choice 7): the books opened last and added last, a cover each, and ways into the library such
/// as Favorites. Before any folder is added it explains what Bibliotaph does. Continue preparing joins it with
/// session packs (3c).
/// </summary>
public sealed partial class HomeViewModel(SourceRootStore roots, LibraryActivity activity, LibraryFolders folders, SettingsLinks links,
    SettingsStore settings, AiService ai, LibraryStore library, LibraryQueries queries, CoverImages covers, LibraryPages pages,
    ReaderWindows readers, FavoritesService favorites, ILogger<HomeViewModel> log)
    : LibraryAwarePageViewModel(roots, activity)
{
    /// <summary>How many covers each row shows.</summary>
    public const int RowLength = 8;

    readonly Dictionary<EntryId, LibraryItemViewModel> _known = [];

    public override Route Route => Route.Home;
    public override string Title => "Home";

    /// <summary>The first-run "AI now or later" step (choice 10): folders added, no endpoint, and not yet answered.</summary>
    [ObservableProperty]
    public partial bool ShowAiStep { get; private set; }

    /// <summary>The books opened most recently, newest first.</summary>
    public ObservableCollection<LibraryItemViewModel> RecentlyOpened { get; } = [];

    /// <summary>The books that joined the library most recently.</summary>
    public ObservableCollection<LibraryItemViewModel> RecentlyAdded { get; } = [];

    [ObservableProperty]
    public partial bool HasRecentlyOpened { get; private set; }

    /// <summary>Some book is in the library: the rows show instead of the indexing note.</summary>
    [ObservableProperty]
    public partial bool HasBooks { get; private set; }

    /// <summary>Some book is a favorite, so Home offers the way in.</summary>
    [ObservableProperty]
    public partial bool HasFavorites { get; private set; }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        ShowAiStep = HasFolders && ai.Setup.Endpoint is null && await settings.GetAsync(SettingKeys.AiAsked) is null;
        Activity.Refreshed -= OnActivityRefreshed;
        Activity.Refreshed += OnActivityRefreshed;
        await RefreshAsync();
    }

    public override void Unload() => Activity.Refreshed -= OnActivityRefreshed;

    async void OnActivityRefreshed(object? sender, EventArgs e)
    {
        await base.LoadAsync();
        await RefreshAsync();
    }

    /// <summary>Fills the rows from the index, keeping the covers already shown.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var scope = await library.GetVisibleEntryIdsAsync();
            var (opened, added, favorite) = await Task.Run(async () => (
                await queries.ListAsync(new LibraryFilter(scope, Sort: LibrarySort.RecentlyOpened, OnlyOpened: true), limit: RowLength),
                await queries.ListAsync(new LibraryFilter(scope, Sort: LibrarySort.RecentlyAdded), limit: RowLength),
                await queries.ListAsync(new LibraryFilter(scope, Group: ScopeKeys.Favorites), limit: 1)));
            Show(RecentlyOpened, opened);
            Show(RecentlyAdded, added);
            HasRecentlyOpened = opened.Count > 0;
            HasBooks = added.Count > 0;
            HasFavorites = favorite.Count > 0;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Refreshing Home failed");
        }
    }

    void Show(ObservableCollection<LibraryItemViewModel> row, IReadOnlyList<LibraryEntry> entries)
    {
        var next = entries.Select(Item).ToList();
        if (row.SequenceEqual(next)) return;
        row.Clear();
        foreach (var item in next) row.Add(item);
    }

    LibraryItemViewModel Item(LibraryEntry entry)
    {
        if (_known.TryGetValue(entry.EntryId, out var item))
        {
            item.Update(entry);
            return item;
        }
        return _known[entry.EntryId] = new LibraryItemViewModel(entry, covers);
    }

    /// <summary>A cover: the Library with that book's details open.</summary>
    [RelayCommand]
    void OpenDetails(LibraryItemViewModel item) => pages.ShowDetails(item.EntryId);

    /// <summary>Opens a book where it was left (choice 6), from a cover's menu or Enter on it.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenBook))]
    async Task OpenBook(LibraryItemViewModel item)
    {
        if (await RequestAsync(item) is { } request) readers.OpenInMainWindow(request);
    }

    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanOpenBook))]
    async Task OpenBookInNewWindow(LibraryItemViewModel item)
    {
        if (await RequestAsync(item) is { } request) await readers.OpenInNewWindowAsync(request);
    }

    static bool CanOpenBook(LibraryItemViewModel? item) => item is { CanOpen: true };

    /// <summary>A book at the page it was left at; a pack at its first image, with the rest to step through.</summary>
    async Task<ViewerRequest?> RequestAsync(LibraryItemViewModel item)
    {
        if (!item.IsPack) return new ViewerRequest(item.DocumentId, item.Title) { Resume = true };
        var images = await Task.Run(() => queries.GetPackImagesAsync(item.EntryId));
        if (images.Count == 0) return null;
        var pack = images.Select(i => new PackStep(i.DocumentId, System.IO.Path.GetFileNameWithoutExtension(i.Name))).ToList();
        return new ViewerRequest(pack[0].DocumentId, pack[0].Title) { Pack = pack, PackTitle = item.Title };
    }

    /// <summary>The heart on a cover or in its menu.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task ToggleFavorite(LibraryItemViewModel item)
    {
        var favorite = !item.IsFavorite;
        item.ShowFavorite(favorite);
        try
        {
            await Task.Run(() => favorites.SetAsync([item.EntryId], favorite));
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Marking entry {EntryId} as a favorite failed", item.EntryId);
            item.ShowFavorite(!favorite);
            return;
        }
        await RefreshAsync();
    }

    [RelayCommand]
    void BrowseLibrary() => pages.Open(null);

    [RelayCommand]
    void OpenFavorites() => pages.Open(LibraryScope.Favorites);

    [RelayCommand]
    void SetUpAi() => links.Open(SettingsSection.Ai);

    [RelayCommand]
    async Task AiLaterAsync()
    {
        ShowAiStep = false;
        await settings.SetAsync(SettingKeys.AiAsked, bool.TrueString);
    }

    [RelayCommand]
    void AddFolder() => folders.RequestPick();

    [RelayCommand]
    void ManageFolders() => folders.Manage();
}
