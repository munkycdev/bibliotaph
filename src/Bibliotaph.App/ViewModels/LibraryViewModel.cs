using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
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

/// <summary>Where the Library shows the adventures marked inside files.</summary>
public enum AdventureCards
{
    /// <summary>
    /// Inside their books: their files' cards and details list them, and a search shows those it finds on their file's
    /// cover, in place of the file.
    /// </summary>
    InBooks,

    /// <summary>Cards of their own beside their files, whether browsing or searching.</summary>
    Cards,

    /// <summary>Not shown at all: only their files show, and their pages are found under the files.</summary>
    Hidden,
}

/// <summary>
/// The Library: every book in a cover grid or detail list, with format and folder filters and a sort; and, while
/// the top bar has a search, its results in two tabs (Documents: titles and file details; Inside documents: page
/// text). Results update as indexing finds more, without losing the scroll position.
/// </summary>
public sealed partial class LibraryViewModel : LibraryAwarePageViewModel, ICollectionChoices, ISessionChoices, IPinState, ILibrarySelectionPage,
    ISavedSearchPage
{
    /// <summary>More changes than this between refreshes replace the list instead of editing it in place.</summary>
    const int InPlaceLimit = 50;

    static readonly Choice<LibrarySort> BestMatch = new(LibrarySort.Relevance, "Best match");
    static readonly Choice<LibrarySort> RecentlyAdded = new(LibrarySort.RecentlyAdded, "Recently added");
    static readonly Choice<LibrarySort> TitleOrder = new(LibrarySort.Title, "Title A–Z");
    static readonly Choice<LibrarySort> PublisherOrder = new(LibrarySort.Publisher, "Publisher A–Z");
    static readonly Choice<LibrarySort> RecentlyOpened = new(LibrarySort.RecentlyOpened, "Recently opened");
    static readonly IReadOnlyList<Choice<LibrarySort>> SearchSorts = [BestMatch, RecentlyAdded, RecentlyOpened, TitleOrder, PublisherOrder];
    static readonly IReadOnlyList<Choice<LibrarySort>> BrowseSorts = [RecentlyAdded, RecentlyOpened, TitleOrder, PublisherOrder];

    readonly SourceRootStore _roots;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly LibraryFolders _folders;
    readonly MetadataService _metadata;
    readonly INavigationService _navigation;
    readonly SettingsLinks _settings;
    readonly ReaderWindows _readers;
    readonly PackService _packs;
    readonly ElsewhereService _elsewhere;
    readonly ChosenCovers _chosenCovers;
    readonly FavoritesService _favorites;
    readonly LibraryPages _pages;
    readonly ILogger<LibraryViewModel> _log;
    readonly BookTextActions _text;
    readonly Dictionary<EntryId, LibraryItemViewModel> _known = [];
    /// <summary>The books whose files are offline or missing as of the last refresh, so their covers are marked.</summary>
    IReadOnlyDictionary<EntryId, EntryAvailability> _away = new Dictionary<EntryId, EntryAvailability>();
    readonly UiTimer _staleTimer;
    readonly AskService _ask;
    readonly LibraryAskScope _askScope;
    readonly CorrectionService _corrections;
    readonly IFilePicker _files;
    readonly InspectorFactory _inspectors;
    int _version;
    bool _loaded;
    bool _stale;
    bool _holdRefresh;
    readonly LibrarySorts _sorts;
    // Whether the order shown was chosen for a search, so the order changes only when searching starts or stops.
    bool _sortSearching;

    public LibraryViewModel(SourceRootStore roots, LibraryStore library, LibraryQueries queries, LibraryActivity activity, SearchState search,
        CoverImages covers, LibraryFolders folders, MetadataService metadata, INavigationService navigation, SettingsLinks settings, ReaderWindows readers,
        PackService packs, ElsewhereService elsewhere, FavoritesService favorites, CollectionActions collections, SessionActions sessions,
        SmartViewDirectory views, LibraryPages pages, BookTextActions text, PinDirectory pins, CorrectionService corrections, AskService ask,
        ChosenCovers chosenCovers, LibraryAskScope askScope, IFilePicker files, InspectorFactory inspectors, LibrarySelectionFactory selection,
        LibrarySorts sorts, TimeProvider time, ILogger<LibraryViewModel> log)
        : base(roots, activity)
    {
        _files = files;
        _inspectors = inspectors;
        _chosenCovers = chosenCovers;
        _askScope = askScope;
        _ask = ask;
        _corrections = corrections;
        Pins = pins;
        Collections = collections;
        Sessions = sessions;
        _pages = pages;
        _favorites = favorites;
        _elsewhere = elsewhere;
        _packs = packs;
        _roots = roots;
        _library = library;
        _queries = queries;
        Search = search;
        _covers = covers;
        _folders = folders;
        _metadata = metadata;
        _navigation = navigation;
        _settings = settings;
        _readers = readers;
        _log = log;
        _text = text;
        _sorts = sorts;
        Selection = selection.Create(this);
        SmartViews = new SmartViewsViewModel(views, this, log);
        _sortSearching = IsSearching;
        SortChoice = RememberedSort();
        Filters.Changed += (_, _) => Refresh();
        // While indexing runs, a search is re-run at most this often rather than on every progress tick.
        _staleTimer = new UiTimer(time, TimeSpan.FromSeconds(3), repeat: true, RefreshIfStaleAsync);
        Selection.Books.Changed += (_, _) => RemoveSelectedFromCollectionCommand.NotifyCanExecuteChanged();
    }

    public override Route Route => Route.Library;
    public override string Title => SmartViews.ActiveView?.Name ?? Scope?.Name ?? "Library";
    public override bool ScrollsItself => true;

    /// <summary>A collection's page sits under Collections, in the breadcrumb and the sidebar.</summary>
    public override string Section => IsCollection ? "Collections" : base.Section;

    public override Route? SectionRoute => IsCollection ? Route.Collections : null;

    public override Route NavRoute => IsCollection && SmartViews.ActiveView is null ? Route.Collections : Route;

    /// <summary>The part of the library shown, such as Favorites, or null for all of it.</summary>
    public LibraryScope? Scope { get; private set; }

    public bool HasScope => Scope is not null;

    /// <summary>The saved search, collection or Favorites shown, which its pin in the sidebar marks.</summary>
    public override string? PinKey => SmartViews.ActiveView is { } view ? SmartViewKey(view.Id) : Scope?.Key is ScopeKeys.Favorites || IsCollection ? Scope?.Key : null;

    /// <summary>Shows only a part of the library, or all of it. Set before the page is shown, or by the scope chip's ×.</summary>
    public void ShowScope(LibraryScope? scope)
    {
        if (Scope == scope) return;
        Scope = scope;
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(HasScope));
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
        NotifyPin();
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

    /// <summary>The details of <see cref="DetailsOnLoad"/> open for Cataloged wrong…, as from a Needs review card.</summary>
    public bool CorrectOnLoad { get; set; }

    public SearchState Search { get; }

    public bool IsSearching => Search.IsSearching;

    public string Heading => SmartViews.ActiveView?.Name ?? (IsSearching ? "Search results" : Scope?.Name ?? "Your library");

    public string Subtitle => SmartViews.ActiveView is not null ? "A saved search: its search, filters and order, run again each time." : IsSearching
        ? Scope is { } scope ? $"In {scope.Name}, across titles, file details and the pages within." : "Across titles, file details and the pages within."
        : Scope?.Subtitle ?? "Good stories begin with something you already own.";

    [ObservableProperty]
    public partial ObservableCollection<LibraryItemViewModel> Items { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DocumentHitsViewModel> Hits { get; set; } = [];

    /// <summary>The books' filters, their menus and the chips that show which are on.</summary>
    public LibraryFilterState Filters { get; } = new();

    /// <summary>Select mode: the books ticked, the bulk editor and the Select bar's actions, with their Undo.</summary>
    public LibrarySelectionViewModel Selection { get; }

    /// <summary>The saved search shown, Save search, and the list of every saved search.</summary>
    public SmartViewsViewModel SmartViews { get; }

    public IReadOnlyList<Choice<LibrarySort>> SortChoices => IsSearching ? SearchSorts : BrowseSorts;

    [ObservableProperty]
    public partial Choice<LibrarySort> SortChoice { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocumentsTab), nameof(IsPagesTab), nameof(ShowGrid), nameof(ShowList), nameof(ShowHits), nameof(ShowLayoutChoice))]
    public partial ResultsTab Tab { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridLayout), nameof(IsListLayout), nameof(ShowGrid), nameof(ShowList))]
    public partial LibraryLayout Layout { get; set; }

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

    /// <summary>"Searching text in 70 of 84 documents", while some are still being read.</summary>
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
        Pins.Changed -= OnPinsChanged;
        Pins.Changed += OnPinsChanged;
        SmartViews.Load();
        await Sessions.Directory.LoadAsync();
        if (SmartViews.TakeViewOnLoad() is { } view) ApplyView(view);
        _staleTimer.Start();
        if (IsCollection) await ShowCollectionAsync();
        // A saved search's folder is chosen here, once the folders are known.
        await LoadFolderChoicesAsync();
        await RefreshAsync();
        if (SavedScrollOffset is not null) RestoreScroll?.Invoke(this, EventArgs.Empty);
        if (DetailsOnLoad is { } details)
        {
            DetailsOnLoad = null;
            var correct = CorrectOnLoad;
            CorrectOnLoad = false;
            var item = _known.GetValueOrDefault(details) ?? (await Task.Run(() => _queries.GetEntriesAsync([details]))).Select(Item).FirstOrDefault();
            if (item is not null && correct) await CatalogedWrong(item);
            else if (item is not null) await OpenDetails(item);
        }
    }

    public override void Unload()
    {
        Activity.Refreshed -= OnActivityRefreshed;
        Collections.Directory.Changed -= OnCollectionsChanged;
        Pins.Changed -= OnPinsChanged;
        Collections.Close();
        Sessions.Close();
        Inspector?.Notes?.Flush();
        if (Inspector is { IsCorrecting: true } correcting) EndCorrecting(correcting);
        SmartViews.Unload();
        Search.PropertyChanged -= OnSearchChanged;
        // A view's search is the view's: it goes when the view is left, and comes back with the page on Back.
        if (SmartViews.IsView && Search.Text.Length > 0) Search.Search("");
        _staleTimer.Stop();
        Selection.Close();
        AddElsewhereDialog = null;
    }

    async void OnSearchChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchState.Text)) return;
        Inspector = null;
        // Browsing and searching each keep their own order, since Best match means nothing without a search. The sort
        // is chosen before the menu changes, so the menu never sees its selection disappear.
        if (_sortSearching != IsSearching)
        {
            _sortSearching = IsSearching;
            _holdRefresh = true;
            SortChoice = RememberedSort();
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

    // A menu whose items are replaced can briefly select nothing; the sort never holds null.
    partial void OnSortChoiceChanged(Choice<LibrarySort> value)
    {
        if (value is null) SortChoice = RememberedSort();
        else
        {
            // Only the user's own pick is kept: not a saved search's order, nor the switch as a search starts or stops.
            if (_loaded && !_holdRefresh) _ = _sorts.RememberAsync(IsSearching, value.Value);
            Refresh();
        }
    }

    Choice<LibrarySort> RememberedSort() =>
        SortChoices.FirstOrDefault(c => c.Value == _sorts.For(IsSearching)) ?? (IsSearching ? BestMatch : RecentlyAdded);

    partial void OnTabChanged(ResultsTab value) => Refresh();

    async Task LoadFolderChoicesAsync()
    {
        var roots = await _roots.ListAsync();
        FolderCount = roots.Count;
        Filters.ShowFolders([.. roots.Select(r => new Choice<long?>(r.Id, System.IO.Path.GetFileName(r.Path) is { Length: > 0 } name ? name : r.Path))]);
    }

    /// <summary>Runs the current view's query. A newer refresh supersedes an older one still running.</summary>
    public async Task RefreshAsync()
    {
        // The constructor sets the order, which would otherwise query before the page is shown.
        if (!_loaded) return;
        Filters.ShowChips();
        var version = ++_version;
        try
        {
            var scope = await _library.GetVisibleEntryIdsAsync(Filters.FolderChoice.Value);
            var away = await _library.GetUnavailableAsync();
            var filter = Filters.ToFilter(scope, SortChoice.Value, Group);
            // A question to the library is about the books shown here.
            _askScope.Set(filter, IsSearching ? Search.Query : SearchQuery.Parse(""), Filters.ActiveFilters.Select(c => c.Label));
            var aiRead = await Task.Run(() => _queries.CountAiReadAsync());
            var withCopies = await Task.Run(() => _queries.CountWithCopiesAsync());
            var parts = await Task.Run(() => _queries.CountPartsAsync());
            if (version == _version) Filters.ShowCounts(aiRead, withCopies, parts, Collections.Directory.ChildrenOf(null).Count > 0);
            if (!IsSearching)
            {
                var entries = await Task.Run(() => _queries.ListAsync(filter));
                var browseFacets = await CountFacetsAsync(filter, null);
                if (version != _version) return;
                _away = away;
                Filters.ShowFacets(browseFacets);
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
            Filters.ShowFacets(facets);
            ShowResults(found, pageResults, plan, query);
            NotifyViewState();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Refreshing the library failed");
        }
    }

    /// <summary>
    /// How many books each system, type and the rest would show. Each menu is counted without its own choice, so
    /// picking a system still shows how many books the other systems have.
    /// </summary>
    Task<FacetMenus> CountFacetsAsync(LibraryFilter filter, SearchPlan? plan)
    {
        var chips = Filters.ChipFilters.Select(b => b.Field.Key).ToList();
        return Task.Run(async () => new FacetMenus(
            await _queries.GetFacetCountsAsync("system", filter with { Systems = null }, plan),
            await _queries.GetFacetCountsAsync("type", filter with { Types = null }, plan),
            await _queries.GetFacetCountsAsync("own", filter with { Owns = null }, plan),
            await _queries.GetFacetCountsAsync("style", filter with { Styles = null }, plan),
            await _queries.GetFacetCountsAsync("duration", filter with { Durations = null }, plan),
            await _queries.GetFacetCountsAsync("store", filter with { Stores = null }, plan),
            await Task.WhenAll(chips.Select(key => _queries.GetValueCountsAsync(key, filter with { Picked = filter.Picked?.Without(key) }, plan)))));
    }

    void ShowResults(IReadOnlyList<LibraryEntry> documents, PageResults? pages, SearchPlan? plan, SearchQuery query)
    {
        var filtered = Filters.IsFiltering;
        IssueText = query.Issues.Count == 0 ? null : Describe(query, query.Issues[0]);

        if (pages is null || plan is null)
        {
            ShowItems(documents);
            Selection.Books.SetShown(Items);
            Hits = [];
            CountText = documents.Count.ToString("N0", CultureInfo.CurrentCulture);
            CountLabel = documents.Count == 1 ? "document" : "documents";
            Coverage = null;
            if (documents.Count > 0) SetEmpty(null);
            else if (!HasFolders) SetEmpty(("No folders yet.", "Add a folder of RPG books, maps or handouts to start your library. Bibliotaph reads them where they are.",
                "Add a folder", AddFolderCommand));
            else if (filtered) SetEmpty(("Nothing here. Yet.", "No books match these filters.", "Clear filters", Filters.ClearFiltersCommand));
            else if (Scope is { } scope) SetEmpty((scope.EmptyTitle, scope.EmptyMessage, "Show your library", ClearScopeCommand));
            else SetEmpty(("Indexing your library.", $"{Activity.Summary}. Books appear here as they are read.", "Manage folders", ManageFoldersCommand));
            return;
        }

        DocumentsTabLabel = $"Documents · {documents.Count.ToString("N0", CultureInfo.CurrentCulture)}";
        PagesTabLabel = plan.TextMatch is null ? "Inside documents" : $"Inside documents · {pages.MatchingPages.ToString("N0", CultureInfo.CurrentCulture)}";
        var progress = Activity.Progress;
        Coverage = Tab == ResultsTab.Pages && progress.Searchable < progress.Documents ? CoverageText(progress) : null;
        var forQuery = $"for “{Search.Text}”";

        if (Tab == ResultsTab.Documents)
        {
            ShowItems(documents);
            Selection.Books.SetShown(Items);
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
    void ClearAll()
    {
        Filters.ClearFiltersCommand.Execute(null);
        Search.Search("");
    }

    [RelayCommand]
    void ShowDocuments() => Tab = ResultsTab.Documents;

    [RelayCommand]
    async Task OpenDetails(LibraryItemViewModel item)
    {
        try
        {
            var inspector = await _inspectors.LoadAsync(item);
            inspector.Removed += async (_, removed) => await ShowRemovedAsync(item, removed);
            inspector.MetadataChanged += async (_, _) => await RefreshAsync();
            inspector.CopiesChanged += async (_, entryId) => await ShowCopiesChangedAsync(entryId);
            inspector.PackSplit += async (_, images) => await ShowPackSplitAsync(item, images);
            inspector.ShowEntryRequested += async (_, entryId) => await ShowDetailsAsync(entryId);
            inspector.PartRemoved += async (_, removed) => await ShowPartRemovedAsync(removed.Removed, removed.Title);
            inspector.LookAlikesAccepted += async (_, offer) => await ApplyLookAlikesAsync(offer);
            inspector.CorrectionEnded += (_, _) => Activity.Invalidate();
            inspector.IsPinned = Pins.IsPinned(PinTarget.Entry(item.EntryId));
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

    /// <summary>Another card's details, as an adventure's file from its details: found in the list, or read from the index.</summary>
    async Task ShowDetailsAsync(EntryId entryId)
    {
        var item = _known.GetValueOrDefault(entryId) ?? (await Task.Run(() => _queries.GetEntriesAsync([entryId]))).Select(Item).FirstOrDefault();
        if (item is not null) await OpenDetails(item);
    }

    /// <summary>After an adventure's own Remove adventure: the library without its card, and its file's details with Undo.</summary>
    async Task ShowPartRemovedAsync(RemovedPart removed, string title)
    {
        Inspector = null;
        await RefreshAsync();
        await ShowDetailsAsync(removed.ParentEntryId);
        Inspector?.ShowPartRemoved(removed, title);
    }

    /// <summary>
    /// What was typed in the details' Notes tab is saved when they close, whatever closed them, and Cataloged wrong…
    /// ends there too.
    /// </summary>
    partial void OnInspectorChanged(InspectorViewModel? oldValue, InspectorViewModel? newValue)
    {
        oldValue?.Notes?.Flush();
        if (oldValue is { IsCorrecting: true }) EndCorrecting(oldValue);
    }

    /// <summary>
    /// Cataloged wrong…, from a card's menu: the book's details, open to correct it, with the values Bibliotaph
    /// guessed marked.
    /// </summary>
    [RelayCommand]
    async Task CatalogedWrong(LibraryItemViewModel item)
    {
        if (Inspector?.Item.EntryId != item.EntryId) await OpenDetails(item);
        if (Inspector is { } inspector && inspector.Item.EntryId == item.EntryId) inspector.StartCorrecting();
    }

    async void EndCorrecting(InspectorViewModel inspector)
    {
        try
        {
            await inspector.EndCorrectingAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Ending the correction of {EntryId} failed", inspector.Item.EntryId);
        }
    }

    /// <summary>
    /// Change them, on the details' offer: the correction repeated on the look-alikes as one bulk edit, with the
    /// bulk edit's Undo, which takes it all back.
    /// </summary>
    async Task ApplyLookAlikesAsync(LookAlikeOffer offer)
    {
        Selection.Undo.Say("Changing…");
        try
        {
            var (problem, undo) = await Task.Run(() => _corrections.ApplyAsync(offer));
            if (problem is not null) Selection.Undo.Say($"The other books couldn't be changed: {problem.Message}");
            else
            {
                var books = undo?.Select(s => s.EntryId).Distinct().Count() ?? 0;
                Selection.SayDone(books == 0 ? "Those books already had these values, so nothing changed."
                        : $"Changed {BulkEditViewModel.Books(books)} from “{offer.FolderName}”.",
                    books > 0 && undo is not null && ReferenceEquals(_navigation.Current, this) ? () => _metadata.UndoManyAsync(undo) : null,
                    "The edit couldn't be undone.");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Correcting {Count} look-alikes of {EntryId} failed", offer.Books.Count, offer.EntryId);
            Selection.Undo.Say("The other books couldn't be changed.");
        }
        // Three books given the same value can propose a folder rule, a card in Needs review.
        Activity.Invalidate();
        await RefreshAsync();
    }

    /// <summary>
    /// A page note in the details' Notes tab: the book opens at its pages, in the copy it opens now where they were
    /// found there, else in the copy the note was made in.
    /// </summary>
    [RelayCommand]
    void OpenPageNote(PageNoteRow note)
    {
        var title = _known.TryGetValue(note.Note.EntryId, out var item) ? item.Title : Inspector?.Item.Title ?? "";
        if (note.Place is { CanOpen: true, DocumentId: { } documentId } place)
        {
            _readers.OpenInMainWindow(new ViewerRequest(documentId, title, place.FirstPage)
            {
                PageNote = new PageNoteOpen(note.Id, place.State, place.Reason, place.FirstPage, place.LastPage),
            });
            return;
        }
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
    async Task OpenPage(PageHitViewModel page) => _readers.OpenInMainWindow(await PageRequestAsync(page));

    /// <summary>Opens a page that matched in a window of its own (its button, Shift+Enter or middle-click).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task OpenPageInNewWindow(PageHitViewModel page) => await _readers.OpenInNewWindowAsync(await PageRequestAsync(page));

    async Task<ViewerRequest> PageRequestAsync(PageHitViewModel page)
    {
        var title = _known.TryGetValue(page.Hit.EntryId, out var item) ? item.Title : "";
        // A page in an adventure inside a file opens as the adventure, its pages shaded.
        if (item?.Entry is { IsPart: true, Pages: { } pages } part && part.DocumentId == page.Hit.DocumentId)
            return PartOpen.Open(page.Hit.DocumentId, title, part.EntryId, part.ParentTitle, pages) with { PageIndex = page.Hit.PdfPage, Query = Search.Query };
        // A page of a PDF in a pack opens in the pack, so Previous and Next step through its other files.
        if (item is { IsPack: true }
            && ViewerRequest.ForPack(item.EntryId, await Task.Run(() => _queries.GetPackImagesAsync(item.EntryId)), title, page.Hit.DocumentId) is { } inPack
            && inPack.DocumentId == page.Hit.DocumentId)
            return inPack with { PageIndex = page.Hit.PdfPage, Query = Search.Query };
        return new ViewerRequest(page.Hit.DocumentId, title, page.Hit.PdfPage, Search.Query);
    }

    /// <summary>
    /// Opens a book at its first page, or a pack at its first image, from the inspector or a card's menu; a book owned
    /// only online opens its page in the browser.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenCard))]
    async Task OpenBook(LibraryItemViewModel item)
    {
        if (item.OpensOnline) await _text.ReadOnlineAsync(item);
        else if (await RequestAsync(item) is { } request) _readers.OpenInMainWindow(request);
    }

    /// <summary>
    /// Open on…, from a card's menu or the details: the book's page where the user owns it online, in the online reader
    /// unless Settings sends it to the everyday browser.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenOnline))]
    Task OpenOnline(LibraryItemViewModel item) => _text.ReadOnlineAsync(item);

    static bool CanOpenOnline(LibraryItemViewModel? item) => item is { HasOnlineLink: true };

    /// <summary>A card opens when it has a file, or a page online.</summary>
    static bool CanOpenCard(LibraryItemViewModel? item) => item is { CanOpen: true } or { OpensOnline: true };

    /// <summary>
    /// The heart on a cover, in a card's menu or in the details: marks the book as a favorite, or unmarks it.
    /// The heart changes at once; in Favorites an unmarked book leaves with the next refresh.
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

    /// <summary>
    /// How much of the library a text search covers. Locked, protected and forgotten files aren't read by themselves,
    /// so they are counted apart from those still being read.
    /// </summary>
    static string CoverageText(IndexProgress progress)
    {
        var culture = CultureInfo.CurrentCulture;
        var text = $"Searching text in {progress.Searchable.ToString("N0", culture)} of {progress.Documents.ToString("N0", culture)} documents.";
        var reading = progress.Documents - progress.Searchable - progress.Withheld;
        if (reading > 0) text += progress.Withheld == 0 ? " The rest are still being read." : $" {reading.ToString("N0", culture)} still being read.";
        if (progress.Withheld > 0)
            text += $" {progress.Withheld.ToString("N0", culture)} {(progress.Withheld == 1 ? "isn't" : "aren't")} read: locked, protected, or with their text forgotten.";
        return text;
    }

    /// <summary>A book owned elsewhere has no file to open.</summary>
    static bool CanOpenBook(LibraryItemViewModel? item) => item is { CanOpen: true };

    /// <summary>
    /// Open in another app, from a card's menu or the details of a PDF whose protection Bibliotaph can't open: the
    /// user's default PDF app.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanOpenElsewhere))]
    Task OpenElsewhere(LibraryItemViewModel item) => _text.OpenElsewhereAsync(item.DocumentId);

    static bool CanOpenElsewhere(LibraryItemViewModel? item) => BookTextActions.HasFile(item);

    /// <summary>Forget its text, from a card's menu: its pages, cover and AI results go.</summary>
    [RelayCommand(CanExecute = nameof(CanForgetText))]
    async Task ForgetText(LibraryItemViewModel item)
    {
        await _text.ForgetAsync(item.EntryId);
        await RefreshAsync();
        if (Inspector is { } inspector && inspector.Item.EntryId == item.EntryId) await inspector.RefreshAsync();
    }

    static bool CanForgetText(LibraryItemViewModel? item) => item is { CanForgetText: true };

    /// <summary>Read it again: undoes Forget its text.</summary>
    [RelayCommand(CanExecute = nameof(CanReadTextAgain))]
    async Task ReadTextAgain(LibraryItemViewModel item)
    {
        await _text.ReadAgainAsync(item.EntryId);
        await RefreshAsync();
        if (Inspector is { } inspector && inspector.Item.EntryId == item.EntryId) await inspector.RefreshAsync();
    }

    static bool CanReadTextAgain(LibraryItemViewModel? item) => item is { CanReadAgain: true };

    /// <summary>"Ask this book", from a card's menu or the details: opens the PDF with its Ask panel showing.</summary>
    [RelayCommand(CanExecute = nameof(CanAskBook))]
    void AskBook(LibraryItemViewModel item) => _readers.OpenInMainWindow(new ViewerRequest(item.DocumentId, item.Title) { Resume = true, Ask = true });

    bool CanAskBook(LibraryItemViewModel? item) => item is { CanAsk: true } && _ask.IsOn;

    /// <summary>Opens a book in a window of its own, from a card's menu, the inspector, Shift+Enter or middle-click.</summary>
    [RelayCommand(AllowConcurrentExecutions = true, CanExecute = nameof(CanOpenBook))]
    async Task OpenBookInNewWindow(LibraryItemViewModel item)
    {
        if (await RequestAsync(item) is { } request) await _readers.OpenInNewWindowAsync(request);
    }

    /// <summary>Opens one of a pack's files, from its inspector's grid; the viewer steps through the rest.</summary>
    [RelayCommand]
    async Task OpenPackImage(PackImageViewModel image)
    {
        if (Inspector is { } inspector && await RequestAsync(inspector.Item, image.Image.DocumentId) is { } request) _readers.OpenInMainWindow(request);
    }

    /// <summary>
    /// What opens a card: its document, or for a pack one of its files with all of them to step through. The file is
    /// <paramref name="documentId"/>, else the one a search found the pack by, else the first.
    /// </summary>
    async Task<ViewerRequest?> RequestAsync(LibraryItemViewModel item, long? documentId = null)
    {
        if (!item.Entry.IsPack) return item.BookRequest();
        return ViewerRequest.ForPack(item.EntryId, await Task.Run(() => _queries.GetPackImagesAsync(item.EntryId)), item.Title, documentId ?? item.Entry.MatchedDocumentId);
    }

    /// <summary>
    /// Check a download: which files in a folder or ZIP the library already has, on a page of its own.
    /// </summary>
    [RelayCommand]
    void CheckDownload() => _navigation.NavigateTo(Route.DownloadCheck);

    /// <summary>
    /// Online libraries: books owned on online services, imported from their library pages. A section of Settings,
    /// with how books owned online show and open, rather than a page of its own.
    /// </summary>
    [RelayCommand]
    void OpenOnlineLibraries() => _settings.Open(SettingsSection.OnlineLibraries);

    // Books owned elsewhere.

    /// <summary>The "Add a book I own elsewhere" dialog, while it is open over the Library.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddElsewhereCommand))]
    public partial AddElsewhereViewModel? AddElsewhereDialog { get; private set; }

    bool CanAddElsewhere => AddElsewhereDialog is null;

    /// <summary>"Add a book I own elsewhere": a dialog for its title and the few fields most books need.</summary>
    [RelayCommand(CanExecute = nameof(CanAddElsewhere))]
    async Task AddElsewhere()
    {
        try
        {
            var dialog = await AddElsewhereViewModel.LoadAsync(_elsewhere, _files, _chosenCovers);
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

    /// <summary>Remove from library, from a card's menu: as from its details, for a book owned elsewhere only.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveFromLibrary))]
    async Task RemoveFromLibrary(LibraryItemViewModel item)
    {
        if (await Task.Run(() => _elsewhere.RemoveAsync(item.EntryId)) is { } removed) await ShowRemovedAsync(item, removed);
    }

    static bool CanRemoveFromLibrary(LibraryItemViewModel? item) => item is { IsElsewhere: true };

    /// <summary>After "Remove from library": the library without it, and Undo beside a note saying so.</summary>
    async Task ShowRemovedAsync(LibraryItemViewModel item, RemovedEntry removed)
    {
        Inspector = null;
        // Undo brings the book back with everything that was set on it.
        Selection.SayDone($"Removed {item.Title} from your library.", () => _elsewhere.UndoRemoveAsync(removed), "The book couldn't be brought back.");
        await RefreshAsync();
    }

    /// <summary>After "Split into separate images" (or files): the library with them back, and Undo beside a note saying so.</summary>
    async Task ShowPackSplitAsync(LibraryItemViewModel pack, IReadOnlyList<EntryId> files)
    {
        Inspector = null;
        // Undo makes the split pack one card again.
        Selection.SayDone($"Split {pack.Title} into {(pack.HasPdfMembers ? LibraryItemViewModel.Files(files.Count) : LibraryItemViewModel.Images(files.Count))}.",
            () => _packs.RepackAsync(pack.EntryId, files), "The split couldn't be undone.");
        await RefreshAsync();
    }

    /// <summary>
    /// How far the visible list was scrolled when the reader left the Library, so Back returns to the same place.
    /// The view saves it when it unloads and restores it once the list has refreshed.
    /// </summary>
    public double? SavedScrollOffset { get; set; }

    /// <summary>Raised after a refresh that followed coming Back, when the view can restore its scroll position.</summary>
    public event EventHandler? RestoreScroll;

    // Select mode: a click ticks a book rather than opening it.

    /// <summary>A click or Enter on a book: in Select mode it ticks the book, otherwise it opens its details.</summary>
    [RelayCommand]
    async Task Activate(LibraryItemViewModel item)
    {
        if (Selection.Books.IsActive) Selection.Books.Toggle(item);
        else await OpenDetails(item);
    }

    /// <summary>A double-click on a book opens it, as Open does, with its details closed behind it. Select mode leaves it to tick.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenFromCard))]
    async Task OpenFromCard(LibraryItemViewModel item)
    {
        Inspector = null;
        await OpenBook(item);
    }

    bool CanOpenFromCard(LibraryItemViewModel? item) => !Selection.Books.IsActive && CanOpenCard(item);

    IReadOnlyList<LibraryItemViewModel> ILibrarySelectionPage.Shown => Items;

    bool ILibrarySelectionPage.ShowsPages => ShowingHits;

    bool ILibrarySelectionPage.IsCurrent => ReferenceEquals(_navigation.Current, this);

    string? ILibrarySelectionPage.TitleOf(EntryId entryId) => _known.TryGetValue(entryId, out var card) ? card.Title : null;

    void ILibrarySelectionPage.CloseDetails() => Inspector = null;

    /// <summary>Esc closes the topmost thing: a dialog, the bulk editor (or its summary), the details, then Select mode.</summary>
    [RelayCommand]
    void Escape()
    {
        if (SmartViews.ViewDialog is { } naming) naming.CancelCommand.Execute(null);
        else if (Sessions.Dialog is { } session) session.CancelCommand.Execute(null);
        else if (Collections.Dialog is { } collection) collection.CancelCommand.Execute(null);
        else if (AddElsewhereDialog is { } dialog) dialog.CancelCommand.Execute(null);
        else if (Selection.CloseDialog()) return;
        else if (Inspector is not null) Inspector = null;
        else Selection.Books.Stop();
    }

    // Collections.

    /// <summary>Adding books to collections and changing the collection shown, with the dialog and the note they show.</summary>
    public CollectionActions Collections { get; }

    IReadOnlyList<CollectionMenuChoice> ICollectionChoices.MenuChoices => Collections.Directory.MenuChoices;

    /// <summary>The Library scoped to a collection: its sub-collections above the books, and its actions in the header.</summary>
    public bool IsCollection => Scope?.CollectionId is not null;

    /// <summary>The whole Library or Favorites: Add folder and the other library actions show in the header.</summary>
    public bool IsWholeLibrary => !IsCollection;

    /// <summary>Add folder and the other library actions, in the header unless a Smart View's own actions are there.</summary>
    public bool ShowsLibraryActions => IsWholeLibrary && !SmartViews.IsView;

    /// <summary>A collection's actions, in the header unless a Smart View scoped to it is shown.</summary>
    public bool ShowsCollectionActions => IsCollection && !SmartViews.IsView;

    CollectionInfo? Collection => Scope?.CollectionId is { } id ? Collections.Directory.Find(id) : null;

    /// <summary>The group the books shown are in: the scope's, or for "Only books added here" the collection's own.</summary>
    string? Group => Scope is { CollectionId: { } id } && OnlyAddedHere ? ScopeKeys.CollectionOwn(id) : Scope?.Key;

    /// <summary>"Only books added here": leaves out the books that are only in its sub-collections.</summary>
    [ObservableProperty]
    public partial bool OnlyAddedHere { get; set; }

    partial void OnOnlyAddedHereChanged(bool value) => Refresh();

    /// <summary>The collections inside the one shown, as cards above its books.</summary>
    public ObservableCollection<CollectionCardViewModel> SubCollections { get; } = [];

    [ObservableProperty]
    public partial bool HasSubCollections { get; private set; }

    // Pins: the page's Pin button pins the saved search, collection or Favorites shown.

    public PinDirectory Pins { get; }

    bool IPinState.IsPinned(object? context) => ((IPinState)Pins).IsPinned(context);

    PinTarget? PageTarget => SmartViews.ActiveView is { } view ? PinTarget.SavedSearch(view.Id)
        : Scope?.CollectionId is { } id ? PinTarget.Collection(id)
        : Scope?.Key == ScopeKeys.Favorites ? PinTarget.Favorites : null;

    public bool IsFavoritesPage => SmartViews.ActiveView is null && Scope?.Key == ScopeKeys.Favorites;

    public string PinLabel => PageTarget is { } target ? Pins.PinLabel(target) : "Pin";

    void NotifyPin()
    {
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(PinKey));
        OnPropertyChanged(nameof(IsFavoritesPage));
    }

    void OnPinsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(PinLabel));
        Inspector?.IsPinned = Pins.IsPinned(PinTarget.Entry(Inspector.Item.EntryId));
        SmartViews.ShowSavedSearchRows();
    }

    /// <summary>A book's Pin or Unpin, from its menu or its details.</summary>
    [RelayCommand]
    Task TogglePinBook(LibraryItemViewModel item) => Pins.ToggleAsync(PinTarget.Entry(item.EntryId));

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
        if (PageTarget is { } target) await Pins.ToggleAsync(target);
    }

    /// <summary>
    /// Deletes the collection shown, never its books, and goes to Collections, where a note says so with Undo.
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
        Collections.Add(Selection.Books.EntryIds, BulkEditViewModel.Books(Selection.Books.Count), request.Choice);

    bool CanRemoveSelected => IsCollection && Selection.Books.HasAny;

    /// <summary>Select mode's Remove from collection, while a collection is shown: the ticked books leave it.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveSelected))]
    async Task RemoveSelectedFromCollection()
    {
        if (Scope?.CollectionId is not { } id) return;
        await Collections.RemoveAsync(Selection.Books.EntryIds, BulkEditViewModel.Books(Selection.Books.Count), id);
        Selection.Books.Clear();
    }

    /// <summary>A collection chip's × in the details: the book leaves that collection.</summary>
    [RelayCommand]
    async Task RemoveFromInspectorCollection(CollectionChip chip)
    {
        if (Inspector is { } inspector) await Collections.RemoveAsync([inspector.Item.EntryId], inspector.Item.Title, chip.Id);
    }

    // Session packs.

    /// <summary>Adding books and pages to session packs, with the dialog and the note they show.</summary>
    public SessionActions Sessions { get; }

    IReadOnlyList<SessionMenuChoice> ISessionChoices.MenuChoices => Sessions.Directory.MenuChoices;

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
        Sessions.Add(Selection.Books.EntryIds, BulkEditViewModel.Books(Selection.Books.Count), request.Choice);

    /// <summary>A matching page's Add: that page to the current session pack.</summary>
    [RelayCommand]
    void AddPageToSession(PageHitViewModel page) =>
        Sessions.AddPage(page.Hit.DocumentId, page.Hit.PdfPage,
            $"page {(string.IsNullOrWhiteSpace(page.Hit.Label) ? (page.Hit.PdfPage + 1).ToString(CultureInfo.CurrentCulture) : page.Hit.Label)}");

    // Smart Views, called saved searches in the app.

    /// <summary>The sidebar key of a Smart View's item, which the Library marks while it shows that view.</summary>
    public static string SmartViewKey(long viewId) => $"view:{viewId.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>What the Library shows now, as a Smart View would keep it.</summary>
    public SmartViewDefinition CurrentView() => Filters.ToView() with
    {
        Query = Search.Text,
        Scope = Scope?.Key,
        OnlyAddedHere = Scope?.CollectionId is not null && OnlyAddedHere,
        Sort = SortChoice.Value,
        Layout = Layout,
        Tab = Tab,
    };

    void ISavedSearchPage.ViewShown()
    {
        OnPropertyChanged(nameof(ShowsLibraryActions));
        OnPropertyChanged(nameof(ShowsCollectionActions));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(NavRoute));
        NotifyPin();
    }

    bool ISavedSearchPage.IsPinned(SmartViewInfo view) => Pins.IsPinned(PinTarget.SavedSearch(view.Id));

    Task ISavedSearchPage.TogglePinAsync(SmartViewInfo view) => Pins.ToggleAsync(PinTarget.SavedSearch(view.Id));

    void ISavedSearchPage.Open(SmartViewInfo view) => _pages.OpenView(view);

    void NotifyViewState() => SmartViews.NotifyCurrentChanged();

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
            SmartViews.ViewNote = view.Scope is not null && scope is null
                ? "The collection or binder this search was saved in is gone, so it looks through your whole library." : null;
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
            _sortSearching = IsSearching;
            SortChoice = SortChoices.FirstOrDefault(c => c.Value == view.Sort) ?? RememberedSort();
            Filters.Apply(view);
            Layout = view.Layout;
            Tab = view.Tab;
        }
        finally
        {
            _holdRefresh = false;
        }
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(Subtitle));
    }

    LibraryScope? ResolveScope(string? key)
    {
        if (key is null) return null;
        if (key == LibraryScope.Favorites.Key) return LibraryScope.Favorites;
        if (Collections.Directory.All.FirstOrDefault(c => ScopeKeys.Collection(c.Id) == key) is { } collection) return Collections.Directory.ScopeFor(collection);
        if (Sessions.Directory.All.FirstOrDefault(p => ScopeKeys.Session(p.Id) == key) is { } pack) return SessionDirectory.ScopeFor(pack);
        return null;
    }
}
