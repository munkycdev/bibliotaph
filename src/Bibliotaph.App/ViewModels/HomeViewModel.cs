using System.Collections.ObjectModel;
using Bibliotaph.App.Controls;
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
/// Home (slice 3 plan, choice 7): the books opened last and added last, a cover each, Continue preparing: the session
/// pack worked on last (3c), and the sidebar's pins in the same order (Pinned Views plan, choice 12). Before any folder
/// is added it explains what Bibliotaph does.
/// </summary>
public sealed partial class HomeViewModel(SourceRootStore roots, LibraryActivity activity, LibraryFolders folders, SettingsLinks links,
    SettingsStore settings, AiService ai, LibraryStore library, LibraryQueries queries, CoverImages covers, LibraryPages pages,
    ReaderWindows readers, FavoritesService favorites, CollectionActions collections, SessionActions sessions, SessionPages sessionPages,
    PinDirectory pins, PinOpener opener, BookTextActions text, ILogger<HomeViewModel> log)
    : LibraryAwarePageViewModel(roots, activity)
{
    /// <summary>How many covers each row shows.</summary>
    public const int RowLength = 8;

    readonly Dictionary<EntryId, LibraryItemViewModel> _known = [];
    /// <summary>The books whose files are offline or missing as of the last refresh, so their covers are marked.</summary>
    IReadOnlyDictionary<EntryId, EntryAvailability> _away = new Dictionary<EntryId, EntryAvailability>();
    int _version;

    public override Route Route => Route.Home;
    public override string Title => "Home";

    /// <summary>Add to collection from a cover's menu, with its dialog and note.</summary>
    public CollectionActions Collections { get; } = collections;

    /// <summary>Add to session from a cover's menu, with its dialog and note.</summary>
    public SessionActions Sessions { get; } = sessions;

    /// <summary>Continue preparing (3c): the session pack worked on last, as a card; null before there is one.</summary>
    [ObservableProperty]
    public partial SessionCardViewModel? ContinuePreparing { get; private set; }

    /// <summary>The sidebar's pins, in its order (Pinned Views plan, choice 12).</summary>
    public PinDirectory Pins { get; } = pins;

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


    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        ShowAiStep = HasFolders && ai.Setup.Endpoint is null && await settings.GetAsync(SettingKeys.AiAsked) is null;
        Activity.Refreshed -= OnActivityRefreshed;
        Activity.Refreshed += OnActivityRefreshed;
        Collections.Directory.Changed -= OnActivityRefreshed;
        Collections.Directory.Changed += OnActivityRefreshed;
        await Collections.Directory.LoadAsync();
        Sessions.Directory.Changed -= OnActivityRefreshed;
        await Sessions.Directory.LoadAsync();
        Sessions.Directory.Changed += OnActivityRefreshed;
        await RefreshAsync();
    }

    public override void Unload()
    {
        Activity.Refreshed -= OnActivityRefreshed;
        Collections.Directory.Changed -= OnActivityRefreshed;
        Collections.Close();
        Sessions.Directory.Changed -= OnActivityRefreshed;
        Sessions.Close();
    }

    /// <summary>A pin, as the sidebar opens it.</summary>
    [RelayCommand]
    Task OpenPin(PinnedItemViewModel item) => opener.OpenAsync(item);

    /// <summary>A cover menu's Pin or Unpin.</summary>
    [RelayCommand]
    Task TogglePinBook(LibraryItemViewModel item) => Pins.ToggleAsync(PinTarget.Entry(item.EntryId));

    async void OnActivityRefreshed(object? sender, EventArgs e)
    {
        await base.LoadAsync();
        await RefreshAsync();
    }

    /// <summary>Fills the rows from the index, keeping the covers already shown.</summary>
    public async Task RefreshAsync()
    {
        var version = ++_version;
        try
        {
            var scope = await library.GetVisibleEntryIdsAsync();
            _away = await library.GetUnavailableAsync();
            var (opened, added) = await Task.Run(async () => (
                await queries.ListAsync(new LibraryFilter(scope, Sort: LibrarySort.RecentlyOpened, OnlyOpened: true), limit: RowLength),
                await queries.ListAsync(new LibraryFilter(scope, Sort: LibrarySort.RecentlyAdded), limit: RowLength)));
            Show(RecentlyOpened, opened);
            Show(RecentlyAdded, added);
            var preparing = Sessions.Directory.Preparing is { } current ? await Sessions.Directory.CardsAsync([current]) : [];
            if (version != _version) return;
            ContinuePreparing = preparing.Count > 0 ? preparing[0] : null;
            HasRecentlyOpened = opened.Count > 0;
            HasBooks = added.Count > 0;
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
        if (_known.TryGetValue(entry.EntryId, out var item)) item.Update(entry);
        else item = _known[entry.EntryId] = new LibraryItemViewModel(entry, covers);
        item.ShowAvailability(_away.GetValueOrDefault(entry.EntryId));
        return item;
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

    /// <summary>Open in another app, from the menu of a cover whose file Bibliotaph can't read (slice 4i plan, choice 4).</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanOpenElsewhere))]
    Task OpenElsewhere(LibraryItemViewModel item) => text.OpenElsewhereAsync(item.DocumentId);

    static bool CanOpenElsewhere(LibraryItemViewModel? item) => BookTextActions.HasFile(item);

    /// <summary>Forget its text, from a cover's menu (slice 4i plan, choice 6).</summary>
    [RelayCommand(CanExecute = nameof(CanForgetText))]
    async Task ForgetText(LibraryItemViewModel item)
    {
        await text.ForgetAsync(item.EntryId);
        await RefreshAsync();
    }

    static bool CanForgetText(LibraryItemViewModel? item) => item is { CanForgetText: true };

    [RelayCommand(CanExecute = nameof(CanReadTextAgain))]
    async Task ReadTextAgain(LibraryItemViewModel item)
    {
        await text.ReadAgainAsync(item.EntryId);
        await RefreshAsync();
    }

    static bool CanReadTextAgain(LibraryItemViewModel? item) => item is { CanReadAgain: true };

    /// <summary>A book at the page it was left at; a pack at its first image, with the rest to step through.</summary>
    async Task<ViewerRequest?> RequestAsync(LibraryItemViewModel item)
    {
        if (!item.IsPack) return item.BookRequest();
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
    void BrowseCollections() => pages.OpenCollections();

    /// <summary>A cover menu's Add to collection.</summary>
    public void AddToCollection(CollectionRequest request)
    {
        if (request.Target is LibraryItemViewModel item) Collections.Add([item.EntryId], item.Title, request.Choice);
    }

    /// <summary>A cover menu's Add to session.</summary>
    public void AddToSession(SessionRequest request)
    {
        if (request.Target is LibraryItemViewModel item) Sessions.Add([item.EntryId], item.Title, request.Choice);
    }

    /// <summary>Continue preparing's card: the pack's page.</summary>
    [RelayCommand]
    void OpenSession(SessionCardViewModel card) => sessionPages.Open(card.Id);

    [RelayCommand]
    void BrowseSessions() => sessionPages.OpenList();

    [RelayCommand]
    void Escape()
    {
        if (Sessions.Dialog is { } session) session.CancelCommand.Execute(null);
        else Collections.Dialog?.CancelCommand.Execute(null);
    }

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
