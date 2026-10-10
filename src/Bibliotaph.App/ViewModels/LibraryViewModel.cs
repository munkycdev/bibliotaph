using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

public enum LibraryLayout
{
    Grid,
    List,
}

public enum ResultsTab
{
    Documents,
    Pages,
}

/// <summary>
/// The Library: every book in a cover grid or detail list, with format and folder filters and a sort; and, while
/// the top bar has a search, its results in two tabs (Documents: titles and file details; Inside documents: page
/// text). Results update as indexing finds more, without losing the scroll position.
/// </summary>
public sealed partial class LibraryViewModel : LibraryAwarePageViewModel
{
    /// <summary>More changes than this between refreshes replace the list instead of editing it in place.</summary>
    const int InPlaceLimit = 50;

    static readonly Choice<LibrarySort> BestMatch = new(LibrarySort.Relevance, "Best match");
    static readonly Choice<LibrarySort> RecentlyAdded = new(LibrarySort.RecentlyAdded, "Recently added");
    static readonly Choice<LibrarySort> TitleOrder = new(LibrarySort.Title, "Title A–Z");
    static readonly Choice<LibrarySort> PublisherOrder = new(LibrarySort.Publisher, "Publisher A–Z");
    static readonly Choice<LibrarySort> RecentlyOpened = new(LibrarySort.RecentlyOpened, "Recently opened");
    static readonly Choice<long?> AllFolders = new(null, "All folders");
    static readonly Choice<string?> AllSystems = new(null, "All systems");
    static readonly Choice<string?> AllTypes = new(null, "All types");
    static readonly Choice<int?> AnyLevel = new(null, "Any level");
    static readonly Choice<string?> AnyOwn = new(null, "Also own: any");
    static readonly IReadOnlyList<Choice<LibrarySort>> SearchSorts = [BestMatch, RecentlyAdded, RecentlyOpened, TitleOrder, PublisherOrder];
    static readonly IReadOnlyList<Choice<LibrarySort>> BrowseSorts = [RecentlyAdded, RecentlyOpened, TitleOrder, PublisherOrder];

    readonly SourceRootStore _roots;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly LibraryFolders _folders;
    readonly MetadataService _metadata;
    readonly INavigationService _navigation;
    readonly ReaderWindows _readers;
    readonly IndexingService _indexing;
    readonly CopiesService _copies;
    readonly PackService _packs;
    readonly ElsewhereService _elsewhere;
    readonly FavoritesService _favorites;
    readonly LibraryPages _pages;
    readonly ILogger<LibraryViewModel> _log;
    readonly Dictionary<EntryId, LibraryItemViewModel> _known = [];
    /// <summary>The books whose files are offline or missing as of the last refresh, so their covers are marked.</summary>
    IReadOnlyDictionary<EntryId, EntryAvailability> _away = new Dictionary<EntryId, EntryAvailability>();
    readonly DispatcherTimer _staleTimer;
    readonly NotesService _notes;
    readonly ExportService _export;
    int _version;
    bool _loaded;
    bool _stale;
    bool _holdRefresh;

    public LibraryViewModel(SourceRootStore roots, LibraryStore library, LibraryQueries queries, LibraryActivity activity, SearchState search,
        CoverImages covers, LibraryFolders folders, MetadataService metadata, INavigationService navigation, ReaderWindows readers,
        IndexingService indexing, CopiesService copies, PackService packs, ElsewhereService elsewhere, FavoritesService favorites,
        CollectionActions collections, SessionActions sessions, SmartViewDirectory views, NotesService notes, LibraryPages pages, ExportService export, ILogger<LibraryViewModel> log)
        : base(roots, activity)
    {
        _export = export;
        Views = views;
        _notes = notes;
        Collections = collections;
        Sessions = sessions;
        _pages = pages;
        _favorites = favorites;
        _elsewhere = elsewhere;
        _indexing = indexing;
        _copies = copies;
        _packs = packs;
        _roots = roots;
        _library = library;
        _queries = queries;
        Search = search;
        _covers = covers;
        _folders = folders;
        _metadata = metadata;
        _navigation = navigation;
        _readers = readers;
        _log = log;
        SortChoice = search.IsSearching ? BestMatch : RecentlyAdded;
        KindChoice = KindChoices[0];
        AiChoice = AiChoices[0];
        CopiesChoice = CopiesChoices[0];
        FolderChoice = AllFolders;
        SystemChoice = AllSystems;
        TypeChoice = AllTypes;
        LevelChoice = AnyLevel;
        OwnChoice = AnyOwn;
        // While indexing runs, a search is re-run at most this often rather than on every progress tick.
        _staleTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => await RefreshIfStaleAsync(),
            Dispatcher.CurrentDispatcher);
        _staleTimer.Stop();
        Selection.Changed += (_, _) => OnSelectionChanged();
    }

    public override Route Route => Route.Library;
    public override string Title => ActiveView?.Name ?? Scope?.Name ?? "Library";
    public override bool ScrollsItself => true;

    /// <summary>A collection's page sits under Collections, in the breadcrumb and the sidebar.</summary>
    public override string Section => IsCollection ? "Collections" : base.Section;

    public override Route? SectionRoute => IsCollection ? Route.Collections : null;

    public override Route NavRoute => IsCollection && ActiveView is null ? Route.Collections : Route;

    /// <summary>The part of the library shown, such as Favorites, or null for all of it (slice 3 plan, choice 3).</summary>
    public LibraryScope? Scope { get; private set; }

    public bool HasScope => Scope is not null;

    public override string? NavScope => ActiveView is { } view ? SmartViewKey(view.Id) : IsCollection ? null : Scope?.Key;

    /// <summary>Shows only a part of the library, or all of it. Set before the page is shown, or by the scope chip's ×.</summary>
    public void ShowScope(LibraryScope? scope)
    {
        if (Scope == scope) return;
        Scope = scope;
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(HasScope));
        OnPropertyChanged(nameof(NavScope));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ScopeChip));
        OnPropertyChanged(nameof(IsCollection));
        OnPropertyChanged(nameof(IsWholeLibrary));
        OnPropertyChanged(nameof(ShowsLibraryActions));
        OnPropertyChanged(nameof(ShowsCollectionActions));
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(SectionRoute));
        OnPropertyChanged(nameof(NavRoute));
        OnPropertyChanged(nameof(PinLabel));
        RemoveSelectedFromCollectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>"In Favorites" or "In Campaign › Maps", on the chip whose × shows the whole library again.</summary>
    public string ScopeChip => Scope is { } scope ? $"In {scope.Path ?? scope.Name}" : "";

    /// <summary>The scope chip's ×: the whole library, with the same search and filters.</summary>
    [RelayCommand]
    async Task ClearScope()
    {
        Inspector = null;
        ShowScope(null);
        await RefreshAsync();
    }

    /// <summary>A book whose details open once the page has loaded, as when a cover on Home is clicked.</summary>
    public EntryId? DetailsOnLoad { get; set; }

    public SearchState Search { get; }

    public bool IsSearching => Search.IsSearching;

    public string Heading => ActiveView?.Name ?? (IsSearching ? "Search results" : Scope?.Name ?? "Your library");

    public string Subtitle => ActiveView is not null ? "A Smart View: its search, filters and order, run again each time." : IsSearching
        ? Scope is { } scope ? $"In {scope.Name}, across titles, file details and the pages within." : "Across titles, file details and the pages within."
        : Scope?.Subtitle ?? "Good stories begin with something you already own.";

    [ObservableProperty]
    public partial ObservableCollection<LibraryItemViewModel> Items { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DocumentHitsViewModel> Hits { get; set; } = [];

    public ObservableCollection<Choice<long?>> FolderChoices { get; } = [AllFolders];

    /// <summary>Books, single images, image packs (F4 plan, choice 10) or books owned elsewhere (F5 plan, choice 8).</summary>
    public IReadOnlyList<Choice<KindFilter>> KindChoices { get; } =
        [new(KindFilter.All, "All kinds"), new(KindFilter.Books, "Books"), new(KindFilter.Images, "Images"), new(KindFilter.Packs, "Image packs"),
            new(KindFilter.Elsewhere, "Owned elsewhere")];

    /// <summary>
    /// Where else books are owned (F5 plan, choice 8): "Also own: any", then Print, Foundry VTT and so on with how many
    /// books each has. Shown once some book has a value, or while one is chosen.
    /// </summary>
    public ObservableCollection<Choice<string?>> OwnChoices { get; } = [AnyOwn];

    [ObservableProperty]
    public partial Choice<string?> OwnChoice { get; set; }

    [ObservableProperty]
    public partial bool ShowOwnChoice { get; private set; }

    /// <summary>Books a model has read (2c): shown once there are some, or while a choice is made.</summary>
    public IReadOnlyList<Choice<AiFilter>> AiChoices { get; } =
        [new(AiFilter.All, "All books"), new(AiFilter.Read, "Read by AI"), new(AiFilter.NotRead, "Not read by AI")];

    [ObservableProperty]
    public partial Choice<AiFilter> AiChoice { get; set; }

    [ObservableProperty]
    public partial bool ShowAiChoice { get; private set; }

    /// <summary>
    /// Books in more than one file (F2), so the copies Bibliotaph joined can be checked: shown once there are some, or
    /// while it is chosen.
    /// </summary>
    public IReadOnlyList<Choice<bool>> CopiesChoices { get; } = [new(false, "All books"), new(true, "Books with copies")];

    [ObservableProperty]
    public partial Choice<bool> CopiesChoice { get; set; }

    [ObservableProperty]
    public partial bool ShowCopiesChoice { get; private set; }

    public IReadOnlyList<Choice<LibrarySort>> SortChoices => IsSearching ? SearchSorts : BrowseSorts;

    [ObservableProperty]
    public partial Choice<LibrarySort> SortChoice { get; set; }

    [ObservableProperty]
    public partial Choice<KindFilter> KindChoice { get; set; }

    [ObservableProperty]
    public partial Choice<long?> FolderChoice { get; set; }

    /// <summary>"All systems", then each game system with how many books have it, then Unknown.</summary>
    public ObservableCollection<Choice<string?>> SystemChoices { get; } = [AllSystems];

    public ObservableCollection<Choice<string?>> TypeChoices { get; } = [AllTypes];

    public IReadOnlyList<Choice<int?>> LevelChoices { get; } =
        [AnyLevel, .. Enumerable.Range(1, 20).Select(l => new Choice<int?>(l, $"Level {l.ToString(CultureInfo.CurrentCulture)}"))];

    [ObservableProperty]
    public partial Choice<string?> SystemChoice { get; set; }

    [ObservableProperty]
    public partial Choice<string?> TypeChoice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLevelChoice))]
    public partial Choice<int?> LevelChoice { get; set; }

    /// <summary>Whether a level filter also keeps books whose levels nobody knows yet (A12).</summary>
    [ObservableProperty]
    public partial bool IncludeUnknownLevels { get; set; }

    public bool HasLevelChoice => LevelChoice.Value is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocumentsTab), nameof(IsPagesTab), nameof(ShowGrid), nameof(ShowList), nameof(ShowHits), nameof(ShowLayoutChoice))]
    public partial ResultsTab Tab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridLayout), nameof(IsListLayout), nameof(ShowGrid), nameof(ShowList))]
    public partial LibraryLayout Layout { get; set; }

    [ObservableProperty]
    public partial bool ShowFilters { get; set; }

    public bool IsDocumentsTab { get => Tab == ResultsTab.Documents; set { if (value) Tab = ResultsTab.Documents; } }

    public bool IsPagesTab { get => Tab == ResultsTab.Pages; set { if (value) Tab = ResultsTab.Pages; } }

    public bool IsGridLayout { get => Layout == LibraryLayout.Grid; set { if (value) Layout = LibraryLayout.Grid; } }

    public bool IsListLayout { get => Layout == LibraryLayout.List; set { if (value) Layout = LibraryLayout.List; } }

    bool ShowingHits => IsSearching && Tab == ResultsTab.Pages;

    /// <summary>Covers or list: the matching pages have one layout of their own.</summary>
    public bool ShowLayoutChoice => !ShowingHits;

    public bool ShowGrid => !ShowingHits && Layout == LibraryLayout.Grid && !IsEmpty;

    public bool ShowList => !ShowingHits && Layout == LibraryLayout.List && !IsEmpty;

    public bool ShowHits => ShowingHits && !IsEmpty;

    [ObservableProperty]
    public partial string DocumentsTabLabel { get; set; } = "Documents";

    [ObservableProperty]
    public partial string PagesTabLabel { get; set; } = "Inside documents";

    /// <summary>The bold number in "24 documents for “dragon”".</summary>
    [ObservableProperty]
    public partial string CountText { get; set; } = "";

    [ObservableProperty]
    public partial string CountLabel { get; set; } = "";

    /// <summary>"Searching text in 70 of 84 documents", while some are still being read (A02).</summary>
    [ObservableProperty]
    public partial string? Coverage { get; set; }

    /// <summary>Something in the query that was ignored, such as a metadata field that arrives later.</summary>
    [ObservableProperty]
    public partial string? IssueText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowList), nameof(ShowHits))]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = "";

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = "";

    [ObservableProperty]
    public partial string? EmptyActionText { get; set; }

    [ObservableProperty]
    public partial IRelayCommand? EmptyCommand { get; set; }

    [ObservableProperty]
    public partial InspectorViewModel? Inspector { get; set; }

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
        _loaded = true;
        Activity.Refreshed -= OnActivityRefreshed;
        Activity.Refreshed += OnActivityRefreshed;
        Search.PropertyChanged -= OnSearchChanged;
        Search.PropertyChanged += OnSearchChanged;
        Collections.Directory.Changed -= OnCollectionsChanged;
        await Collections.Directory.LoadAsync();
        Collections.Directory.Changed += OnCollectionsChanged;
        await Sessions.Directory.LoadAsync();
        if (_viewOnLoad is { } view)
        {
            _viewOnLoad = null;
            ApplyView(view);
        }
        _staleTimer.Start();
        if (IsCollection) await ShowCollectionAsync();
        await LoadFolderChoicesAsync();
        if (_folderOnLoad is { } folder)
        {
            _folderOnLoad = null;
            _holdRefresh = true;
            FolderChoice = FolderChoices.FirstOrDefault(c => c.Value == folder) ?? AllFolders;
            _holdRefresh = false;
        }
        await RefreshAsync();
        if (SavedScrollOffset is not null) RestoreScroll?.Invoke(this, EventArgs.Empty);
        if (DetailsOnLoad is { } details)
        {
            DetailsOnLoad = null;
            var item = _known.GetValueOrDefault(details) ?? (await Task.Run(() => _queries.GetEntriesAsync([details]))).Select(Item).FirstOrDefault();
            if (item is not null) await OpenDetails(item);
        }
    }

    public override void Unload()
    {
        Activity.Refreshed -= OnActivityRefreshed;
        Collections.Directory.Changed -= OnCollectionsChanged;
        Collections.Close();
        Sessions.Close();
        ViewDialog?.CancelCommand.Execute(null);
        Inspector?.Notes?.Flush();
        ViewMessage = null;
        ViewUndo = null;
        Search.PropertyChanged -= OnSearchChanged;
        // A view's search is the view's: it goes when the view is left, and comes back with the page on Back.
        if (ActiveView is not null)
        {
            _viewOnLoad = CurrentView();
            if (Search.Text.Length > 0) Search.Search("");
        }
        _staleTimer.Stop();
        // A bulk edit can be undone until the Library is left (plan choice 7), and a split likewise.
        BulkUndo = null;
        SplitUndo = null;
        RemoveUndo = null;
        BulkMessage = null;
        AddElsewhereDialog = null;
        if (BulkEdit is { IsApplyingStep: false }) BulkEdit = null;
    }

    async void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchState.Text)) return;
        Inspector = null;
        // Best match is the order for a search, and means nothing without one. The sort is chosen before the menu
        // changes, so the menu never sees its selection disappear.
        var resort = SortChoice == BestMatch != IsSearching;
        if (resort)
        {
            _holdRefresh = true;
            SortChoice = IsSearching ? BestMatch : RecentlyAdded;
            _holdRefresh = false;
        }
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(SortChoices));
        OnPropertyChanged(nameof(ShowGrid));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowHits));
        OnPropertyChanged(nameof(ShowLayoutChoice));
        await RefreshAsync();
    }

    async void OnActivityRefreshed(object? sender, EventArgs e)
    {
        // The open inspector follows its book's stages, as while it is reprocessed.
        if (Inspector is { } inspector)
        {
            try
            {
                await inspector.RefreshAsync();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Refreshing details for document {DocumentId} failed", inspector.Item.DocumentId);
            }
        }
        // Browsing is cheap to refresh; a search waits for the stale timer.
        if (IsSearching) _stale = true;
        else
        {
            FolderCount = (await _roots.ListAsync()).Count;
            await RefreshAsync();
        }
    }

    async Task RefreshIfStaleAsync()
    {
        if (!_stale) return;
        _stale = false;
        await RefreshAsync();
    }

    async void Refresh()
    {
        if (!_holdRefresh) await RefreshAsync();
    }

    // A menu whose items are replaced can briefly select nothing; the filters never hold null.
    partial void OnSortChoiceChanged(Choice<LibrarySort> value)
    {
        if (value is null) SortChoice = IsSearching ? BestMatch : RecentlyAdded;
        else Refresh();
    }

    partial void OnKindChoiceChanged(Choice<KindFilter> value)
    {
        if (value is null) KindChoice = KindChoices[0];
        else Refresh();
    }

    partial void OnAiChoiceChanged(Choice<AiFilter> value)
    {
        if (value is null) AiChoice = AiChoices[0];
        else Refresh();
    }

    partial void OnCopiesChoiceChanged(Choice<bool> value)
    {
        if (value is null) CopiesChoice = CopiesChoices[0];
        else Refresh();
    }

    partial void OnFolderChoiceChanged(Choice<long?> value)
    {
        if (value is null) FolderChoice = AllFolders;
        else Refresh();
    }

    partial void OnSystemChoiceChanged(Choice<string?> value)
    {
        if (value is null) SystemChoice = AllSystems;
        else Refresh();
    }

    partial void OnTypeChoiceChanged(Choice<string?> value)
    {
        if (value is null) TypeChoice = AllTypes;
        else Refresh();
    }

    partial void OnOwnChoiceChanged(Choice<string?> value)
    {
        if (value is null) OwnChoice = AnyOwn;
        else Refresh();
    }

    partial void OnLevelChoiceChanged(Choice<int?> value)
    {
        if (value is null) LevelChoice = AnyLevel;
        else Refresh();
    }

    partial void OnIncludeUnknownLevelsChanged(bool value)
    {
        if (LevelChoice.Value is not null) Refresh();
    }

    partial void OnTabChanged(ResultsTab value) => Refresh();

    async Task LoadFolderChoicesAsync()
    {
        var roots = await _roots.ListAsync();
        FolderCount = roots.Count;
        var choices = roots.Select(r => new Choice<long?>(r.Id, System.IO.Path.GetFileName(r.Path) is { Length: > 0 } name ? name : r.Path)).ToList();
        if (FolderChoices.Skip(1).SequenceEqual(choices)) return;
        var selected = FolderChoice;
        _holdRefresh = true;
        while (FolderChoices.Count > 1) FolderChoices.RemoveAt(1);
        foreach (var choice in choices) FolderChoices.Add(choice);
        FolderChoice = FolderChoices.Contains(selected) ? selected : AllFolders;
        _holdRefresh = false;
    }

    /// <summary>Runs the current view's query. A newer refresh supersedes an older one still running.</summary>
    public async Task RefreshAsync()
    {
        // The constructor sets the filters, which would otherwise query before the page is shown.
        if (!_loaded) return;
        var version = ++_version;
        try
        {
            var scope = await _library.GetVisibleEntryIdsAsync(FolderChoice.Value);
            var away = await _library.GetUnavailableAsync();
            var filter = new LibraryFilter(scope, KindChoice.Value, SortChoice.Value, Selected(SystemChoice), Selected(TypeChoice), LevelChoice.Value,
                IncludeUnknownLevels, AiChoice.Value, CopiesChoice.Value, Selected(OwnChoice), Group);
            var aiRead = await Task.Run(() => _queries.CountAiReadAsync());
            var withCopies = await Task.Run(() => _queries.CountWithCopiesAsync());
            if (version == _version)
            {
                ShowAiChoice = aiRead > 0 || AiChoice.Value != AiFilter.All;
                ShowCopiesChoice = withCopies > 0 || CopiesChoice.Value;
            }
            if (!IsSearching)
            {
                var entries = await Task.Run(() => _queries.ListAsync(filter));
                var browseFacets = await CountFacetsAsync(filter, null);
                if (version != _version) return;
                _away = away;
                ShowFacets(browseFacets);
                ShowResults(entries, null, null, SearchQuery.Parse(""));
                NotifyViewState();
                return;
            }

            var query = Search.Query;
            var plan = SearchPlan.From(query);
            var documents = Task.Run(() => _queries.SearchDocumentsAsync(plan, filter));
            var pages = Task.Run(() => _queries.SearchPagesAsync(plan, filter));
            var found = await documents;
            var pageResults = await pages;
            var facets = await CountFacetsAsync(filter, plan);
            if (version != _version) return;
            _away = away;
            ShowFacets(facets);
            ShowResults(found, pageResults, plan, query);
            NotifyViewState();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Refreshing the library failed");
        }
    }

    static string[]? Selected(Choice<string?> choice) => choice.Value is { } value ? [value] : null;

    /// <summary>
    /// How many books each system and type would show. Each menu is counted without its own choice, so picking a
    /// system still shows how many books the other systems have.
    /// </summary>
    Task<(IReadOnlyList<FacetCount> Systems, IReadOnlyList<FacetCount> Types, IReadOnlyList<FacetCount> Owns)> CountFacetsAsync(LibraryFilter filter, SearchPlan? plan) =>
        Task.Run(async () =>
            (await _queries.GetFacetCountsAsync("system", filter with { Systems = null }, plan),
                await _queries.GetFacetCountsAsync("type", filter with { Types = null }, plan),
                await _queries.GetFacetCountsAsync("own", filter with { Owns = null }, plan)));

    void ShowFacets((IReadOnlyList<FacetCount> Systems, IReadOnlyList<FacetCount> Types, IReadOnlyList<FacetCount> Owns) facets)
    {
        _holdRefresh = true;
        SystemChoice = ShowChoices(SystemChoices, AllSystems, facets.Systems, SystemChoice);
        TypeChoice = ShowChoices(TypeChoices, AllTypes, facets.Types, TypeChoice);
        OwnChoice = ShowChoices(OwnChoices, AnyOwn, facets.Owns, OwnChoice);
        ShowOwnChoice = facets.Owns.Any(c => c.Value != SearchQuery.Unknown) || OwnChoice.Value is not null;
        _holdRefresh = false;
    }

    /// <summary>
    /// Replaces a menu's counted choices, and returns the choice to select: the one with the same value, which is
    /// kept with a count of none when no book has it any more, so the menu never changes what was picked.
    /// </summary>
    static Choice<string?> ShowChoices(ObservableCollection<Choice<string?>> menu, Choice<string?> all, IReadOnlyList<FacetCount> counts,
        Choice<string?> selected)
    {
        var choices = counts.Select(c => new Choice<string?>(c.Value, $"{c.Label} ({c.Count.ToString("N0", CultureInfo.CurrentCulture)})")).ToList();
        if (selected.Value is { } value && choices.All(c => c.Value != value))
            choices.Add(new Choice<string?>(value, Uncounted(selected.Label) + " (0)"));
        if (menu.Skip(1).SequenceEqual(choices)) return menu.FirstOrDefault(c => c.Value == selected.Value) ?? all;
        while (menu.Count > 1) menu.RemoveAt(1);
        foreach (var choice in choices) menu.Add(choice);
        return menu.FirstOrDefault(c => c.Value == selected.Value) ?? all;
    }

    static string Uncounted(string label) => label.LastIndexOf(" (", StringComparison.Ordinal) is var i and > 0 ? label[..i] : label;

    void ShowResults(IReadOnlyList<LibraryEntry> documents, PageResults? pages, SearchPlan? plan, SearchQuery query)
    {
        var filtered = KindChoice.Value != KindFilter.All || FolderChoice.Value is not null || SystemChoice.Value is not null
            || TypeChoice.Value is not null || LevelChoice.Value is not null || AiChoice.Value != AiFilter.All || CopiesChoice.Value || OwnChoice.Value is not null;
        IssueText = query.Issues.Count == 0 ? null : Describe(query, query.Issues[0]);

        if (pages is null || plan is null)
        {
            ShowItems(documents);
            Selection.SetShown(Items);
            Hits = [];
            CountText = documents.Count.ToString("N0", CultureInfo.CurrentCulture);
            CountLabel = documents.Count == 1 ? "document" : "documents";
            Coverage = null;
            if (documents.Count > 0) SetEmpty(null);
            else if (!HasFolders) SetEmpty(("No folders yet.", "Add a folder of RPG books, maps or handouts to start your library. Bibliotaph reads them where they are.",
                "Add a folder", AddFolderCommand));
            else if (filtered) SetEmpty(("Nothing here. Yet.", "No books match these filters.", "Clear filters", ClearFiltersCommand));
            else if (Scope is { } scope) SetEmpty((scope.EmptyTitle, scope.EmptyMessage, "Show your library", ClearScopeCommand));
            else SetEmpty(("Indexing your library.", $"{Activity.Summary}. Books appear here as they are read.", "Manage folders", ManageFoldersCommand));
            return;
        }

        DocumentsTabLabel = $"Documents · {documents.Count.ToString("N0", CultureInfo.CurrentCulture)}";
        PagesTabLabel = plan.TextMatch is null ? "Inside documents" : $"Inside documents · {pages.MatchingPages.ToString("N0", CultureInfo.CurrentCulture)}";
        var progress = Activity.Progress;
        Coverage = Tab == ResultsTab.Pages && progress.Searchable < progress.Documents
            ? $"Searching text in {progress.Searchable.ToString("N0", CultureInfo.CurrentCulture)} of {progress.Documents.ToString("N0", CultureInfo.CurrentCulture)} documents. The rest are still being read."
            : null;
        var forQuery = $"for “{Search.Text}”";

        if (Tab == ResultsTab.Documents)
        {
            ShowItems(documents);
            Selection.SetShown(Items);
            Hits = [];
            CountText = documents.Count.ToString("N0", CultureInfo.CurrentCulture);
            CountLabel = $"{(documents.Count == 1 ? "document" : "documents")} {forQuery}";
            SetEmpty(documents.Count > 0 ? null : NoResults(filtered));
            return;
        }

        Hits = [.. pages.Entries.Select(d => new DocumentHitsViewModel(Item(d.Entry), [.. d.Pages.Select(p => new PageHitViewModel(p))], d.MatchingPages))];
        CountText = pages.MatchingPages.ToString("N0", CultureInfo.CurrentCulture);
        CountLabel = $"matching {(pages.MatchingPages == 1 ? "page" : "pages")} in {pages.MatchingEntries.ToString("N0", CultureInfo.CurrentCulture)} "
            + $"{(pages.MatchingEntries == 1 ? "document" : "documents")} {forQuery}";
        if (pages.Entries.Count < pages.MatchingEntries)
            CountLabel += $", best {pages.Entries.Count.ToString("N0", CultureInfo.CurrentCulture)} shown";
        if (plan.TextMatch is null)
            SetEmpty(("Inside documents looks for words.", "Add a word to search the pages, or see the matching books on the Documents tab.",
                "Show documents", ShowDocumentsCommand));
        else SetEmpty(Hits.Count > 0 ? null : NoResults(filtered));
    }

    (string, string, string, IRelayCommand) NoResults(bool filtered) => filtered
        ? ("Nothing here. Yet.", "Try fewer or shorter words, or clear the filters.", "Clear search and filters", ClearAllCommand)
        : ("Nothing here. Yet.", "Try fewer or shorter words. Books still being read are searched as soon as they're done.",
            "Clear search", ClearAllCommand);

    void SetEmpty((string Title, string Message, string Action, IRelayCommand Command)? empty)
    {
        IsEmpty = empty is not null;
        if (empty is not { } e) return;
        EmptyTitle = e.Title;
        EmptyMessage = e.Message;
        EmptyActionText = e.Action;
        EmptyCommand = e.Command;
    }

    /// <summary>"Searching by type arrives once books have metadata. (“type:adventure”)".</summary>
    static string Describe(SearchQuery query, QueryIssue issue)
    {
        var start = Math.Clamp(issue.Start, 0, query.Text.Length);
        var token = query.Text.Substring(start, Math.Clamp(issue.Length, 0, query.Text.Length - start)).Trim();
        return token.Length == 0 ? issue.Message : $"{issue.Message} (“{token}”)";
    }

    LibraryItemViewModel Item(LibraryEntry entry)
    {
        if (_known.TryGetValue(entry.EntryId, out var item)) item.Update(entry);
        else item = _known[entry.EntryId] = new LibraryItemViewModel(entry, _covers);
        item.ShowAvailability(_away.GetValueOrDefault(entry.EntryId));
        return item;
    }

    /// <summary>
    /// Shows a new list. When only a few books arrived or left and the rest kept their order, as while indexing
    /// runs, the list is edited in place so the grid keeps its scroll position; otherwise it is replaced.
    /// </summary>
    void ShowItems(IReadOnlyList<LibraryEntry> entries)
    {
        var next = entries.Select(Item).ToList();
        var current = Items;
        if (current.Count == next.Count && current.SequenceEqual(next)) return;

        var nextSet = next.ToHashSet();
        var currentSet = current.ToHashSet();
        var kept = current.Where(nextSet.Contains).ToList();
        var changes = current.Count - kept.Count + next.Count(i => !currentSet.Contains(i));
        if (changes > InPlaceLimit || !kept.SequenceEqual(next.Where(currentSet.Contains)))
        {
            Items = [.. next];
            return;
        }
        for (var i = current.Count - 1; i >= 0; i--)
            if (!nextSet.Contains(current[i])) current.RemoveAt(i);
        for (var i = 0; i < next.Count; i++)
            if (i >= current.Count || !ReferenceEquals(current[i], next[i])) current.Insert(i, next[i]);
    }

    [RelayCommand]
    void AddFolder() => _folders.RequestPick();

    [RelayCommand]
    void ManageFolders() => _folders.Manage();

    [RelayCommand]
    void ToggleFilters() => ShowFilters = !ShowFilters;

    [RelayCommand]
    void ClearFilters()
    {
        _holdRefresh = true;
        KindChoice = KindChoices[0];
        AiChoice = AiChoices[0];
        CopiesChoice = CopiesChoices[0];
        FolderChoice = AllFolders;
        SystemChoice = AllSystems;
        TypeChoice = AllTypes;
        LevelChoice = AnyLevel;
        OwnChoice = AnyOwn;
        IncludeUnknownLevels = false;
        _holdRefresh = false;
        Refresh();
    }

    [RelayCommand]
    void ClearAll()
    {
        ClearFilters();
        Search.Search("");
    }

    [RelayCommand]
    void ShowDocuments() => Tab = ResultsTab.Documents;

    [RelayCommand]
    async Task OpenDetails(LibraryItemViewModel item)
    {
        try
        {
            var inspector = await InspectorViewModel.LoadAsync(item, _queries, _library, _metadata, _indexing, _copies, _packs, _elsewhere, _covers);
            inspector.Notes = new EntryNotesViewModel(item.EntryId, _notes, _log);
            await inspector.Notes.LoadAsync();
            inspector.Removed += async (_, removed) => await ShowRemovedAsync(item, removed);
            inspector.MetadataChanged += async (_, _) => await RefreshAsync();
            inspector.CopiesChanged += async (_, entryId) => await ShowCopiesChangedAsync(entryId);
            inspector.PackSplit += async (_, images) => await ShowPackSplitAsync(item, images);
            Inspector = inspector;
            await ShowInspectorCollectionsAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading details for document {DocumentId} failed", item.DocumentId);
        }
    }

    [RelayCommand]
    void CloseDetails() => Inspector = null;

    /// <summary>What was typed in the details' Notes tab is saved when they close, whatever closed them.</summary>
    partial void OnInspectorChanged(InspectorViewModel? oldValue, InspectorViewModel? newValue) => oldValue?.Notes?.Flush();

    /// <summary>A page note in the details' Notes tab: the book opens at its pages, in the copy it was made in.</summary>
    [RelayCommand]
    void OpenPageNote(PageNoteRow note)
    {
        var title = _known.TryGetValue(note.Note.EntryId, out var item) ? item.Title : Inspector?.Item.Title ?? "";
        _readers.OpenInMainWindow(new ViewerRequest(note.Note.Range.DocumentId, title, note.Note.Range.FirstPdfPage));
    }

    /// <summary>
    /// After Make current or Not the same book: the library again, and the inspector on the card the user was on, or
    /// on the copy's own card when it was split off.
    /// </summary>
    async Task ShowCopiesChangedAsync(EntryId entryId)
    {
        await RefreshAsync();
        if (_known.TryGetValue(entryId, out var item)) await OpenDetails(item);
        else Inspector = null;
    }

    /// <summary>
    /// Opens a book at a page that matched, with the search's words marked. Always in the main window's reader, even
    /// when the book is popped out, so a hit never jumps to another window.
    /// </summary>
    [RelayCommand]
    void OpenPage(PageHitViewModel page) => _readers.OpenInMainWindow(PageRequest(page));

    /// <summary>Opens a page that matched in a window of its own (its button, Shift+Enter or middle-click).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    Task OpenPageInNewWindow(PageHitViewModel page) => _readers.OpenInNewWindowAsync(PageRequest(page));

    ViewerRequest PageRequest(PageHitViewModel page)
    {
        var title = _known.TryGetValue(page.Hit.EntryId, out var item) ? item.Title : "";
        return new ViewerRequest(page.Hit.DocumentId, title, page.Hit.PdfPage, Search.Query);
    }

    /// <summary>Opens a book at its first page, or a pack at its first image, from the inspector or a card's menu.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenBook))]
    async Task OpenBook(LibraryItemViewModel item)
    {
        if (await RequestAsync(item) is { } request) _readers.OpenInMainWindow(request);
    }

    /// <summary>
    /// The heart on a cover, in a card's menu or in the details (slice 3 plan, choice 5): marks the book as a favorite,
    /// or unmarks it. The heart changes at once; in Favorites an unmarked book leaves with the next refresh.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task ToggleFavorite(LibraryItemViewModel item)
    {
        var favorite = !item.IsFavorite;
        item.ShowFavorite(favorite);
        try
        {
            await Task.Run(() => _favorites.SetAsync([item.EntryId], favorite));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Marking entry {EntryId} as a favorite failed", item.EntryId);
            item.ShowFavorite(!favorite);
            return;
        }
        await RefreshAsync();
    }

    /// <summary>A book owned elsewhere has no file to open.</summary>
    static bool CanOpenBook(LibraryItemViewModel? item) => item is { CanOpen: true };

    /// <summary>Opens a book in a window of its own, from a card's menu, the inspector, Shift+Enter or middle-click.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanOpenBook))]
    async Task OpenBookInNewWindow(LibraryItemViewModel item)
    {
        if (await RequestAsync(item) is { } request) await _readers.OpenInNewWindowAsync(request);
    }

    /// <summary>Opens one of a pack's images, from its inspector's grid; the viewer steps through the rest (choice 7).</summary>
    [RelayCommand]
    async Task OpenPackImage(PackImageViewModel image)
    {
        if (Inspector is { } inspector && await RequestAsync(inspector.Item, image.Image.DocumentId) is { } request) _readers.OpenInMainWindow(request);
    }

    /// <summary>
    /// What opens a card: its document, or for a pack one of its images with all of them to step through. The image is
    /// <paramref name="documentId"/>, else the one a search found the pack by, else the first.
    /// </summary>
    async Task<ViewerRequest?> RequestAsync(LibraryItemViewModel item, long? documentId = null)
    {
        if (!item.Entry.IsPack) return new ViewerRequest(item.DocumentId, item.Title) { Resume = true };
        documentId ??= item.Entry.MatchedDocumentId;
        var images = await Task.Run(() => _queries.GetPackImagesAsync(item.EntryId));
        if (images.Count == 0) return null;
        var pack = images.Select(i => new PackStep(i.DocumentId, System.IO.Path.GetFileNameWithoutExtension(i.Name))).ToList();
        var at = documentId is { } id ? Math.Max(0, pack.FindIndex(p => p.DocumentId == id)) : 0;
        return new ViewerRequest(pack[at].DocumentId, pack[at].Title) { Pack = pack, PackTitle = item.Title };
    }

    /// <summary>Check a download (F5b): which files in a folder or ZIP the library already has, on a page of its own.</summary>
    [RelayCommand]
    void CheckDownload() => _navigation.NavigateTo(Route.DownloadCheck);

    // Books owned elsewhere (F5a).

    /// <summary>The "Add a book I own elsewhere" dialog, while it is open over the Library.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddElsewhereCommand))]
    public partial AddElsewhereViewModel? AddElsewhereDialog { get; private set; }

    bool CanAddElsewhere => AddElsewhereDialog is null;

    /// <summary>"Add a book I own elsewhere" (choice 1): a dialog for its title and the few fields most books need.</summary>
    [RelayCommand(CanExecute = nameof(CanAddElsewhere))]
    async Task AddElsewhere()
    {
        try
        {
            var dialog = await AddElsewhereViewModel.LoadAsync(_elsewhere);
            dialog.Closed += async (_, entryId) => await AddElsewhereClosedAsync(entryId);
            Inspector = null;
            AddElsewhereDialog = dialog;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Opening Add a book I own elsewhere failed");
        }
    }

    /// <summary>After Save, the new card's inspector opens, where every other field can be filled in.</summary>
    async Task AddElsewhereClosedAsync(EntryId? entryId)
    {
        AddElsewhereDialog = null;
        if (entryId is not { } added) return;
        await RefreshAsync();
        var item = _known.GetValueOrDefault(added) ?? (await Task.Run(() => _queries.GetEntriesAsync([added]))).Select(Item).FirstOrDefault();
        if (item is not null) await OpenDetails(item);
    }

    /// <summary>After "Remove from library": the library without it, and Undo beside a note saying so.</summary>
    async Task ShowRemovedAsync(LibraryItemViewModel item, RemovedEntry removed)
    {
        Inspector = null;
        BulkUndo = null;
        SplitUndo = null;
        RemoveUndo = removed;
        BulkMessage = $"Removed {item.Title} from your library.";
        await RefreshAsync();
    }

    /// <summary>The book owned elsewhere the last Remove took away, until the next edit or until the Library is left.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoveUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoRemoveCommand))]
    public partial RemovedEntry? RemoveUndo { get; private set; }

    public bool HasRemoveUndo => RemoveUndo is not null;

    /// <summary>Brings the removed book back, with everything that was set on it.</summary>
    [RelayCommand(CanExecute = nameof(HasRemoveUndo))]
    async Task UndoRemove()
    {
        if (RemoveUndo is not { } removed) return;
        RemoveUndo = null;
        BulkMessage = "Undoing…";
        try
        {
            await Task.Run(() => _elsewhere.UndoRemoveAsync(removed));
            BulkMessage = null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Undoing the removal of entry {EntryId} failed", removed.EntryId);
            BulkMessage = "The book couldn't be brought back.";
        }
        await RefreshAsync();
    }

    /// <summary>After "Split into separate images": the library with the images back, and Undo beside a note saying so.</summary>
    async Task ShowPackSplitAsync(LibraryItemViewModel pack, int images)
    {
        Inspector = null;
        BulkUndo = null;
        RemoveUndo = null;
        SplitUndo = pack.EntryId;
        BulkMessage = $"Split {pack.Title} into {images.ToString("N0", CultureInfo.CurrentCulture)} {(images == 1 ? "image" : "images")}.";
        await RefreshAsync();
    }

    /// <summary>The pack the last split took apart, until the next edit or until the Library is left.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSplitUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoSplitCommand))]
    public partial EntryId? SplitUndo { get; private set; }

    public bool HasSplitUndo => SplitUndo is not null;

    /// <summary>Makes the split folder or ZIP one card again.</summary>
    [RelayCommand(CanExecute = nameof(HasSplitUndo))]
    async Task UndoSplit()
    {
        if (SplitUndo is not { } pack) return;
        SplitUndo = null;
        BulkMessage = "Undoing…";
        try
        {
            await Task.Run(() => _packs.RepackAsync(pack));
            BulkMessage = null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Undoing the split of pack {EntryId} failed", pack);
            BulkMessage = "The split couldn't be undone.";
        }
        await RefreshAsync();
    }

    /// <summary>
    /// How far the visible list was scrolled when the reader left the Library, so Back returns to the same place.
    /// The view saves it when it unloads and restores it once the list has refreshed.
    /// </summary>
    public double? SavedScrollOffset { get; set; }

    /// <summary>Raised after a refresh that followed coming Back, when the view can restore its scroll position.</summary>
    public event EventHandler? RestoreScroll;

    // Select mode and bulk metadata editing (slice 4e).

    /// <summary>The books ticked in Select mode, kept while the search and filters change.</summary>
    public BookSelection Selection { get; } = new();

    /// <summary>The bulk editor, while it is open over the Library.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditSelectedCommand), nameof(SelectAllCommand))]
    public partial BulkEditViewModel? BulkEdit { get; private set; }

    /// <summary>"Edited 12 books.", after a bulk edit, beside its Undo.</summary>
    [ObservableProperty]
    public partial string? BulkMessage { get; private set; }

    /// <summary>What undoes the last bulk edit: until the next one, or until the Library is left.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBulkUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoBulkEditCommand))]
    public partial IReadOnlyList<FieldSnapshot>? BulkUndo { get; private set; }

    public bool HasBulkUndo => BulkUndo is not null;

    void OnSelectionChanged()
    {
        RemoveSelectedFromCollectionCommand.NotifyCanExecuteChanged();
        EditSelectedCommand.NotifyCanExecuteChanged();
        ExportSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
        SelectRangeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Select: a checkbox on every book, and a click ticks a book instead of opening its details.</summary>
    [RelayCommand]
    void StartSelecting() => Selection.Start();

    /// <summary>Done, or Esc: leaves Select mode, and the ticks with it.</summary>
    [RelayCommand]
    void StopSelecting() => Selection.Stop();

    /// <summary>A click or Enter on a book: in Select mode it ticks the book, otherwise it opens its details.</summary>
    [RelayCommand]
    async Task Activate(LibraryItemViewModel item)
    {
        if (Selection.IsActive) Selection.Toggle(item);
        else await OpenDetails(item);
    }

    /// <summary>Shift+click in Select mode: ticks every book from the last one clicked to this one.</summary>
    [RelayCommand(CanExecute = nameof(IsSelecting))]
    void SelectRange(LibraryItemViewModel item) => Selection.SelectRange(Items, item);

    bool IsSelecting() => Selection.IsActive;

    bool CanSelectAll => Selection.IsActive && !ShowingHits && BulkEdit is null;

    /// <summary>Ctrl+A in Select mode: ticks every book in the current results.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    void SelectAll() => Selection.SelectAll(Items);

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    void ClearSelection() => Selection.Clear();

    bool CanClearSelection => Selection.HasAny;

    bool CanEditSelected => Selection.HasAny && BulkEdit is null;

    /// <summary>Opens the bulk editor for the ticked books, shown or not.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    async Task EditSelected()
    {
        try
        {
            var bulk = await BulkEditViewModel.LoadAsync(Selection.EntryIds, _metadata);
            bulk.Closed += async (_, result) => await BulkEditClosedAsync(result);
            Inspector = null;
            BulkEdit = bulk;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading metadata for {Count} selected documents failed", Selection.Count);
        }
    }

    /// <summary>Export these (slice 4j plan, choice 7): the ticked books as CSV, the rows Settings > Backup's Export CSV writes.</summary>
    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    async Task ExportSelected()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export these books for a spreadsheet",
            FileName = "Bibliotaph books.csv",
            DefaultExt = ".csv",
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(System.Windows.Application.Current.MainWindow) != true) return;
        SplitUndo = null;
        RemoveUndo = null;
        BulkUndo = null;
        try
        {
            var rows = await _export.WriteCsvAsync(dialog.FileName, Selection.EntryIds);
            BulkMessage = $"Exported {BulkEditViewModel.Books(rows)} to {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _log.LogError(ex, "Exporting {Count} selected books to {Path} failed", Selection.Count, dialog.FileName);
            BulkMessage = $"The export couldn't be saved: {ex.Message}";
        }
    }

    async Task BulkEditClosedAsync(BulkEditResult? result)
    {
        BulkEdit = null;
        if (result is null) return;
        // Leaving the Library while it applied ends the Undo, as leaving afterwards would.
        SplitUndo = null;
        RemoveUndo = null;
        BulkUndo = result.Books > 0 && ReferenceEquals(_navigation.Current, this) ? result.Undo : null;
        BulkMessage = result.Books == 0 ? "Those books already had these values, so nothing changed."
            : $"Edited {BulkEditViewModel.Books(result.Books)}.";
        await RefreshAsync();
    }

    /// <summary>Puts back exactly what the fields held before the last bulk edit.</summary>
    [RelayCommand(CanExecute = nameof(HasBulkUndo))]
    async Task UndoBulkEdit()
    {
        if (BulkUndo is not { } undo) return;
        BulkUndo = null;
        BulkMessage = "Undoing…";
        try
        {
            await Task.Run(() => _metadata.UndoManyAsync(undo));
            BulkMessage = null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Undoing a bulk edit of {Count} fields failed", undo.Count);
            BulkMessage = "The edit couldn't be undone.";
        }
        await RefreshAsync();
    }

    /// <summary>Esc closes the topmost thing: a dialog, the bulk editor (or its summary), the details, then Select mode.</summary>
    [RelayCommand]
    void Escape()
    {
        if (ViewDialog is { } naming) naming.CancelCommand.Execute(null);
        else if (Sessions.Dialog is { } session) session.CancelCommand.Execute(null);
        else if (Collections.Dialog is { } collection) collection.CancelCommand.Execute(null);
        else if (AddElsewhereDialog is { } dialog) dialog.CancelCommand.Execute(null);
        else if (BulkEdit is { } bulk) bulk.EscapeCommand.Execute(null);
        else if (Inspector is not null) Inspector = null;
        else Selection.Stop();
    }

    // Collections (slice 3b).

    /// <summary>Adding books to collections and changing the collection shown, with the dialog and the note they show.</summary>
    public CollectionActions Collections { get; }

    /// <summary>The Library scoped to a collection: its sub-collections above the books, and its actions in the header.</summary>
    public bool IsCollection => Scope?.CollectionId is not null;

    /// <summary>The whole Library or Favorites: Add folder and the other library actions show in the header.</summary>
    public bool IsWholeLibrary => !IsCollection;

    /// <summary>Add folder and the other library actions, in the header unless a Smart View's own actions are there.</summary>
    public bool ShowsLibraryActions => IsWholeLibrary && !IsView;

    /// <summary>A collection's actions, in the header unless a Smart View scoped to it is shown.</summary>
    public bool ShowsCollectionActions => IsCollection && !IsView;

    CollectionInfo? Collection => Scope?.CollectionId is { } id ? Collections.Directory.Find(id) : null;

    /// <summary>The group the books shown are in: the scope's, or for "Only books added here" the collection's own.</summary>
    string? Group => Scope is { CollectionId: { } id } && OnlyAddedHere ? ScopeKeys.CollectionOwn(id) : Scope?.Key;

    /// <summary>"Only books added here" (choice 9): leaves out the books that are only in its sub-collections.</summary>
    [ObservableProperty]
    public partial bool OnlyAddedHere { get; set; }

    partial void OnOnlyAddedHereChanged(bool value) => Refresh();

    /// <summary>The collections inside the one shown, as cards above its books.</summary>
    public ObservableCollection<CollectionCardViewModel> SubCollections { get; } = [];

    [ObservableProperty]
    public partial bool HasSubCollections { get; private set; }

    public string PinLabel => Collection?.Pinned == true ? "Unpin" : "Pin";

    int _collectionVersion;

    async void OnCollectionsChanged(object? sender, EventArgs e)
    {
        try
        {
            if (IsCollection) await ShowCollectionAsync();
            await ShowInspectorCollectionsAsync();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Showing the changed collections failed");
        }
    }

    /// <summary>The collection's name and description as they are now, and its sub-collections' cards.</summary>
    async Task ShowCollectionAsync()
    {
        if (Collection is not { } collection) return;
        ShowScope(Collections.Directory.ScopeFor(collection));
        var version = ++_collectionVersion;
        var cards = await Collections.Directory.CardsAsync(Collections.Directory.ChildrenOf(collection.Id));
        if (version != _collectionVersion) return;
        SubCollections.Clear();
        foreach (var card in cards) SubCollections.Add(card);
        HasSubCollections = SubCollections.Count > 0;
        if (!HasSubCollections && OnlyAddedHere) OnlyAddedHere = false;
        OnPropertyChanged(nameof(PinLabel));
    }

    /// <summary>The collections the open book was added to, as chips in its details.</summary>
    async Task ShowInspectorCollectionsAsync()
    {
        if (Inspector is not { } inspector) return;
        var ids = await Task.Run(() => Collections.Directory.CollectionsOfAsync(inspector.Item.EntryId));
        if (!ReferenceEquals(inspector, Inspector)) return;
        inspector.ShowCollections([.. ids.Select(Collections.Directory.Find).OfType<CollectionInfo>()
            .Select(c => new CollectionChip(c.Id, c.Name, Collections.Directory.PathOf(c.Id)))
            .OrderBy(c => c.Path, StringComparer.CurrentCultureIgnoreCase)]);
    }

    [RelayCommand]
    void OpenCollection(CollectionCardViewModel card) => _pages.Open(Collections.Directory.ScopeFor(card.Collection));

    /// <summary>A collection's chip in the details: the Library scoped to it.</summary>
    [RelayCommand]
    void OpenInspectorCollection(CollectionChip chip)
    {
        if (Collections.Directory.Find(chip.Id) is { } collection) _pages.Open(Collections.Directory.ScopeFor(collection));
    }

    /// <summary>New collection, inside the one shown.</summary>
    [RelayCommand]
    void NewSubCollection()
    {
        if (Scope?.CollectionId is { } id) Collections.Create(id);
    }

    [RelayCommand]
    void RenameCollection()
    {
        if (Collection is { } collection) Collections.Rename(collection);
    }

    [RelayCommand]
    void MoveCollection()
    {
        if (Collection is { } collection) Collections.Move(collection);
    }

    [RelayCommand]
    async Task TogglePin()
    {
        if (Collection is { } collection) await Collections.SetPinnedAsync(collection, !collection.Pinned);
    }

    /// <summary>
    /// Deletes the collection shown, never its books (choice 8), and goes to Collections, where a note says so with
    /// Undo.
    /// </summary>
    [RelayCommand]
    async Task DeleteCollection()
    {
        if (Collection is not { } collection || await Collections.DeleteAsync(collection) is not { } deleted) return;
        _navigation.NavigateUp(Route.Collections);
        if (_navigation.Current is CollectionsViewModel page) page.ShowDeleted(deleted);
    }

    /// <summary>A card's menu or the details' Add to collection: the book they are for.</summary>
    public void AddToCollection(CollectionRequest request)
    {
        var item = request.Target switch
        {
            LibraryItemViewModel book => book,
            InspectorViewModel details => details.Item,
            _ => null,
        };
        if (item is not null) Collections.Add([item.EntryId], item.Title, request.Choice);
    }

    /// <summary>Select mode's Add to collection: every ticked book, shown or not.</summary>
    [RelayCommand]
    void AddSelectedToCollection(CollectionRequest request) =>
        Collections.Add(Selection.EntryIds, BulkEditViewModel.Books(Selection.Count), request.Choice);

    bool CanRemoveSelected => IsCollection && Selection.HasAny;

    /// <summary>Select mode's Remove from collection, while a collection is shown: the ticked books leave it.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    async Task RemoveSelectedFromCollection()
    {
        if (Scope?.CollectionId is not { } id) return;
        await Collections.RemoveAsync(Selection.EntryIds, BulkEditViewModel.Books(Selection.Count), id);
        Selection.Clear();
    }

    /// <summary>A collection chip's × in the details: the book leaves that collection.</summary>
    [RelayCommand]
    async Task RemoveFromInspectorCollection(CollectionChip chip)
    {
        if (Inspector is { } inspector) await Collections.RemoveAsync([inspector.Item.EntryId], inspector.Item.Title, chip.Id);
    }

    // Session packs (slice 3c).

    /// <summary>Adding books and pages to session packs, with the dialog and the note they show.</summary>
    public SessionActions Sessions { get; }

    /// <summary>A card's menu or the details' Add to session: the book they are for.</summary>
    public void AddToSession(SessionRequest request)
    {
        var item = request.Target switch
        {
            LibraryItemViewModel book => book,
            InspectorViewModel details => details.Item,
            _ => null,
        };
        if (item is not null) Sessions.Add([item.EntryId], item.Title, request.Choice);
    }

    /// <summary>Select mode's Add to session: every ticked book, shown or not.</summary>
    [RelayCommand]
    void AddSelectedToSession(SessionRequest request) =>
        Sessions.Add(Selection.EntryIds, BulkEditViewModel.Books(Selection.Count), request.Choice);

    /// <summary>A matching page's Add: that page to the current session pack (slice 3 plan, choice 13).</summary>
    [RelayCommand]
    void AddPageToSession(PageHitViewModel page) =>
        Sessions.AddPage(page.Hit.DocumentId, page.Hit.PdfPage,
            $"page {(string.IsNullOrWhiteSpace(page.Hit.Label) ? (page.Hit.PdfPage + 1).ToString(CultureInfo.CurrentCulture) : page.Hit.Label)}");

    // Smart Views (slice 3e).

    SmartViewDefinition? _viewOnLoad;
    SmartViewDefinition? _viewSaved;
    long? _folderOnLoad;

    public SmartViewDirectory Views { get; }

    /// <summary>The sidebar key of a Smart View's item, which the Library marks while it shows that view.</summary>
    public static string SmartViewKey(long viewId) => $"view:{viewId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The Smart View shown, if the page was opened from one or saved as one.</summary>
    public SmartViewInfo? ActiveView { get; private set; }

    public bool IsView => ActiveView is not null;

    /// <summary>The search, filters, order, layout or tab differ from the view's: Update view and Save as new show.</summary>
    public bool IsViewChanged => ActiveView is not null && _viewSaved is { } saved && saved != CurrentView();

    /// <summary>Save view, while a search, a filter or a scope is in use and no view is shown.</summary>
    public bool CanSaveView => ActiveView is null && (IsSearching || HasScope || CurrentView() with { Sort = Unfiltered.Sort, Layout = Unfiltered.Layout, Tab = Unfiltered.Tab } != Unfiltered);

    /// <summary>The whole library with no search or filter, which isn't worth saving as a view.</summary>
    static readonly SmartViewDefinition Unfiltered = new();

    /// <summary>Why the view shows less than it was saved with, as when its collection is gone.</summary>
    [ObservableProperty]
    public partial string? ViewNote { get; private set; }

    /// <summary>"Saved Haunted places. It's in the sidebar.", beside its Undo.</summary>
    [ObservableProperty]
    public partial string? ViewMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasViewUndo))]
    [NotifyCanExecuteChangedFor(nameof(UndoViewCommand))]
    public partial Func<Task>? ViewUndo { get; private set; }

    public bool HasViewUndo => ViewUndo is not null;

    /// <summary>Naming a view, while the dialog is open over the page.</summary>
    [ObservableProperty]
    public partial SmartViewDialogViewModel? ViewDialog { get; private set; }

    /// <summary>Opens the page as a Smart View: its search, filters and scope are put back when the page loads.</summary>
    public void OpenView(SmartViewInfo view)
    {
        _viewOnLoad = SmartViewDefinition.Parse(view.Definition);
        ShowView(view, _viewOnLoad);
    }

    /// <summary>What the Library shows now, as a Smart View would keep it.</summary>
    public SmartViewDefinition CurrentView() => new(Search.Text, Scope?.Key, Scope?.CollectionId is not null && OnlyAddedHere, KindChoice.Value, FolderChoice.Value,
        SystemChoice.Value, TypeChoice.Value, LevelChoice.Value, LevelChoice.Value is not null && IncludeUnknownLevels, AiChoice.Value, CopiesChoice.Value,
        OwnChoice.Value, SortChoice.Value, Layout, Tab);

    void ShowView(SmartViewInfo? view, SmartViewDefinition? saved)
    {
        ActiveView = view;
        _viewSaved = saved;
        OnPropertyChanged(nameof(ActiveView));
        OnPropertyChanged(nameof(IsView));
        OnPropertyChanged(nameof(ShowsLibraryActions));
        OnPropertyChanged(nameof(ShowsCollectionActions));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(NavScope));
        OnPropertyChanged(nameof(NavRoute));
        NotifyViewState();
    }

    void NotifyViewState()
    {
        OnPropertyChanged(nameof(IsViewChanged));
        OnPropertyChanged(nameof(CanSaveView));
    }

    partial void OnLayoutChanged(LibraryLayout value) => NotifyViewState();

    /// <summary>
    /// Puts a view's search, filters, scope, order, layout and tab back, before the page's first refresh. A collection
    /// or session that has gone is left out, and the note says so; a query that no longer reads shows its problem as
    /// any search does, with its text kept.
    /// </summary>
    void ApplyView(SmartViewDefinition view)
    {
        _holdRefresh = true;
        try
        {
            var scope = ResolveScope(view.Scope);
            ViewNote = view.Scope is not null && scope is null
                ? "The collection or session this view was saved in is gone, so it looks through your whole library." : null;
            ShowScope(scope);
            OnlyAddedHere = scope?.CollectionId is not null && view.OnlyAddedHere;
            if (Search.Text != view.Query.Trim())
            {
                Search.PropertyChanged -= OnSearchChanged;
                Search.Search(view.Query);
                Search.PropertyChanged += OnSearchChanged;
                OnPropertyChanged(nameof(IsSearching));
                OnPropertyChanged(nameof(SortChoices));
            }
            SortChoice = SortChoices.FirstOrDefault(c => c.Value == view.Sort) ?? (IsSearching ? BestMatch : RecentlyAdded);
            KindChoice = KindChoices.FirstOrDefault(c => c.Value == view.Kind) ?? KindChoices[0];
            AiChoice = AiChoices.FirstOrDefault(c => c.Value == view.Ai) ?? AiChoices[0];
            CopiesChoice = CopiesChoices.FirstOrDefault(c => c.Value == view.Copies) ?? CopiesChoices[0];
            SystemChoice = Pick(SystemChoices, AllSystems, view.System);
            TypeChoice = Pick(TypeChoices, AllTypes, view.Type);
            OwnChoice = Pick(OwnChoices, AnyOwn, view.Own);
            LevelChoice = LevelChoices.FirstOrDefault(c => c.Value == view.Level) ?? AnyLevel;
            IncludeUnknownLevels = view.IncludeUnknownLevels;
            Layout = view.Layout;
            Tab = view.Tab;
            _folderOnLoad = view.Folder;
        }
        finally
        {
            _holdRefresh = false;
        }
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>A counted menu's choice for a saved value, added to the menu until the counts arrive.</summary>
    static Choice<string?> Pick(ObservableCollection<Choice<string?>> menu, Choice<string?> all, string? value)
    {
        if (value is null) return all;
        if (menu.FirstOrDefault(c => c.Value == value) is { } found) return found;
        var choice = new Choice<string?>(value, value);
        menu.Add(choice);
        return choice;
    }

    LibraryScope? ResolveScope(string? key)
    {
        if (key is null) return null;
        if (key == LibraryScope.Favorites.Key) return LibraryScope.Favorites;
        if (Collections.Directory.All.FirstOrDefault(c => ScopeKeys.Collection(c.Id) == key) is { } collection) return Collections.Directory.ScopeFor(collection);
        if (Sessions.Directory.All.FirstOrDefault(p => ScopeKeys.Session(p.Id) == key) is { } pack) return SessionDirectory.ScopeFor(pack);
        return null;
    }

    /// <summary>Save view: names the search, filters and scope in use as a Smart View, listed in the sidebar.</summary>
    [RelayCommand]
    void SaveView() => AskName("Save as a Smart View", "Save", "", async name =>
    {
        var current = CurrentView();
        var created = await RunView(() => Views.CreateAsync(name, current));
        if (created is null) return;
        ShowView(created, current);
        SayView($"Saved {created.Name}. It's in the sidebar under Smart Views.", null);
    });

    /// <summary>Update view: the view now keeps the search and filters as they are.</summary>
    [RelayCommand]
    async Task UpdateView()
    {
        if (ActiveView is not { } view) return;
        var current = CurrentView();
        if (!await RunView(() => Views.UpdateAsync(view.Id, current))) return;
        var previous = _viewSaved;
        ShowView(Views.Find(view.Id) ?? view, current);
        SayView($"Updated {view.Name}.", previous is null ? null : async () =>
        {
            await Views.UpdateAsync(view.Id, previous);
            if (ActiveView?.Id == view.Id) ShowView(Views.Find(view.Id) ?? view, previous);
        });
    }

    /// <summary>Save as new: a second view from the changed search and filters, leaving the first as it was.</summary>
    [RelayCommand]
    void SaveViewAsNew() => AskName("Save as a new Smart View", "Save", ActiveView is { } view ? $"{view.Name} (2)" : "", async name =>
    {
        var current = CurrentView();
        var created = await RunView(() => Views.CreateAsync(name, current));
        if (created is null) return;
        ShowView(created, current);
        SayView($"Saved {created.Name}. It's in the sidebar under Smart Views.", null);
    });

    [RelayCommand]
    void RenameView()
    {
        if (ActiveView is not { } view) return;
        AskName("Rename Smart View", "Save", view.Name, async name =>
        {
            if (await RunView(() => Views.RenameAsync(view.Id, name)) && Views.Find(view.Id) is { } renamed) ShowView(renamed, _viewSaved);
        });
    }

    /// <summary>Deletes the view, never a book. The page keeps showing the same books, and Undo brings the view back.</summary>
    [RelayCommand]
    async Task DeleteView()
    {
        if (ActiveView is not { } view) return;
        var deleted = await RunView(() => Views.DeleteAsync(view.Id));
        if (deleted is null) return;
        var saved = _viewSaved;
        ShowView(null, null);
        SayView($"Deleted the {deleted.Name} Smart View. Your books are untouched.", async () =>
        {
            var back = await Views.RestoreAsync(deleted);
            ShowView(back, saved ?? SmartViewDefinition.Parse(back.Definition));
        });
    }

    [RelayCommand(CanExecute = nameof(HasViewUndo))]
    async Task UndoView()
    {
        if (ViewUndo is not { } undo) return;
        ViewUndo = null;
        ViewMessage = "Undoing…";
        try
        {
            await undo();
            ViewMessage = null;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Undoing a Smart View change failed");
            ViewMessage = "That couldn't be undone.";
        }
    }

    void SayView(string message, Func<Task>? undo)
    {
        ViewMessage = message;
        ViewUndo = undo;
    }

    void AskName(string heading, string action, string name, Func<string, Task> then)
    {
        var dialog = new SmartViewDialogViewModel(heading, action, name);
        dialog.Closed += async (_, chosen) =>
        {
            ViewDialog = null;
            if (chosen is not null) await then(chosen);
        };
        ViewDialog = dialog;
    }

    async Task<T?> RunView<T>(Func<Task<T>> change)
    {
        try
        {
            return await change();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Changing a Smart View failed");
            SayView("That didn't work. The log has the details.", null);
            return default;
        }
    }
}
