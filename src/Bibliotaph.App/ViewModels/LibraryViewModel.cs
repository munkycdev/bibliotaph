using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
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
    static readonly Choice<long?> AllFolders = new(null, "All folders");
    static readonly Choice<string?> AllSystems = new(null, "All systems");
    static readonly Choice<string?> AllTypes = new(null, "All types");
    static readonly Choice<int?> AnyLevel = new(null, "Any level");
    static readonly IReadOnlyList<Choice<LibrarySort>> SearchSorts = [BestMatch, RecentlyAdded, TitleOrder, PublisherOrder];
    static readonly IReadOnlyList<Choice<LibrarySort>> BrowseSorts = [RecentlyAdded, TitleOrder, PublisherOrder];

    readonly SourceRootStore _roots;
    readonly LibraryStore _library;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly LibraryFolders _folders;
    readonly MetadataService _metadata;
    readonly INavigationService _navigation;
    readonly ReaderWindows _readers;
    readonly IndexingService _indexing;
    readonly ILogger<LibraryViewModel> _log;
    readonly Dictionary<long, LibraryItemViewModel> _known = [];
    readonly DispatcherTimer _staleTimer;
    int _version;
    bool _loaded;
    bool _stale;
    bool _holdRefresh;

    public LibraryViewModel(SourceRootStore roots, LibraryStore library, LibraryQueries queries, LibraryActivity activity, SearchState search,
        CoverImages covers, LibraryFolders folders, MetadataService metadata, INavigationService navigation, ReaderWindows readers,
        IndexingService indexing, ILogger<LibraryViewModel> log)
        : base(roots, activity)
    {
        _indexing = indexing;
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
        FormatChoice = FormatChoices[0];
        AiChoice = AiChoices[0];
        FolderChoice = AllFolders;
        SystemChoice = AllSystems;
        TypeChoice = AllTypes;
        LevelChoice = AnyLevel;
        // While indexing runs, a search is re-run at most this often rather than on every progress tick.
        _staleTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) => await RefreshIfStaleAsync(),
            Dispatcher.CurrentDispatcher);
        _staleTimer.Stop();
        Selection.Changed += (_, _) => OnSelectionChanged();
    }

    public override Route Route => Route.Library;
    public override string Title => "Library";
    public override bool ScrollsItself => true;

    public SearchState Search { get; }

    public bool IsSearching => Search.IsSearching;

    public string Heading => IsSearching ? "Search results" : "Your library";

    public string Subtitle => IsSearching ? "Across titles, file details and the pages within." : "Good stories begin with something you already own.";

    [ObservableProperty]
    public partial ObservableCollection<LibraryItemViewModel> Items { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<DocumentHitsViewModel> Hits { get; set; } = [];

    public ObservableCollection<Choice<long?>> FolderChoices { get; } = [AllFolders];

    public IReadOnlyList<Choice<FormatFilter>> FormatChoices { get; } =
        [new(FormatFilter.All, "All formats"), new(FormatFilter.Pdf, "PDFs"), new(FormatFilter.Images, "Images")];

    /// <summary>Books a model has read (2c): shown once there are some, or while a choice is made.</summary>
    public IReadOnlyList<Choice<AiFilter>> AiChoices { get; } =
        [new(AiFilter.All, "All books"), new(AiFilter.Read, "Read by AI"), new(AiFilter.NotRead, "Not read by AI")];

    [ObservableProperty]
    public partial Choice<AiFilter> AiChoice { get; set; }

    [ObservableProperty]
    public partial bool ShowAiChoice { get; private set; }

    public IReadOnlyList<Choice<LibrarySort>> SortChoices => IsSearching ? SearchSorts : BrowseSorts;

    [ObservableProperty]
    public partial Choice<LibrarySort> SortChoice { get; set; }

    [ObservableProperty]
    public partial Choice<FormatFilter> FormatChoice { get; set; }

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
        _staleTimer.Start();
        await LoadFolderChoicesAsync();
        await RefreshAsync();
        if (SavedScrollOffset is not null) RestoreScroll?.Invoke(this, EventArgs.Empty);
    }

    public override void Unload()
    {
        Activity.Refreshed -= OnActivityRefreshed;
        Search.PropertyChanged -= OnSearchChanged;
        _staleTimer.Stop();
        // A bulk edit can be undone until the Library is left (plan choice 7).
        BulkUndo = null;
        BulkMessage = null;
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

    partial void OnFormatChoiceChanged(Choice<FormatFilter> value)
    {
        if (value is null) FormatChoice = FormatChoices[0];
        else Refresh();
    }

    partial void OnAiChoiceChanged(Choice<AiFilter> value)
    {
        if (value is null) AiChoice = AiChoices[0];
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
            var scope = await _library.GetVisibleDocumentIdsAsync(FolderChoice.Value);
            var filter = new LibraryFilter(scope, FormatChoice.Value, SortChoice.Value, Selected(SystemChoice), Selected(TypeChoice), LevelChoice.Value,
                IncludeUnknownLevels, AiChoice.Value);
            var aiRead = await Task.Run(() => _queries.CountAiReadAsync());
            if (version == _version) ShowAiChoice = aiRead > 0 || AiChoice.Value != AiFilter.All;
            if (!IsSearching)
            {
                var entries = await Task.Run(() => _queries.ListAsync(filter));
                var browseFacets = await CountFacetsAsync(filter, null);
                if (version != _version) return;
                ShowFacets(browseFacets);
                ShowResults(entries, null, null, SearchQuery.Parse(""));
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
            ShowFacets(facets);
            ShowResults(found, pageResults, plan, query);
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
    Task<(IReadOnlyList<FacetCount> Systems, IReadOnlyList<FacetCount> Types)> CountFacetsAsync(LibraryFilter filter, SearchPlan? plan) => Task.Run(async () =>
        (await _queries.GetFacetCountsAsync("system", filter with { Systems = null }, plan),
            await _queries.GetFacetCountsAsync("type", filter with { Types = null }, plan)));

    void ShowFacets((IReadOnlyList<FacetCount> Systems, IReadOnlyList<FacetCount> Types) facets)
    {
        _holdRefresh = true;
        SystemChoice = ShowChoices(SystemChoices, AllSystems, facets.Systems, SystemChoice);
        TypeChoice = ShowChoices(TypeChoices, AllTypes, facets.Types, TypeChoice);
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
        var filtered = FormatChoice.Value != FormatFilter.All || FolderChoice.Value is not null || SystemChoice.Value is not null
            || TypeChoice.Value is not null || LevelChoice.Value is not null || AiChoice.Value != AiFilter.All;
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

        Hits = [.. pages.Documents.Select(d => new DocumentHitsViewModel(Item(d.Document), [.. d.Pages.Select(p => new PageHitViewModel(p))], d.MatchingPages))];
        CountText = pages.MatchingPages.ToString("N0", CultureInfo.CurrentCulture);
        CountLabel = $"matching {(pages.MatchingPages == 1 ? "page" : "pages")} in {pages.MatchingDocuments.ToString("N0", CultureInfo.CurrentCulture)} "
            + $"{(pages.MatchingDocuments == 1 ? "document" : "documents")} {forQuery}";
        if (pages.Documents.Count < pages.MatchingDocuments)
            CountLabel += $", best {pages.Documents.Count.ToString("N0", CultureInfo.CurrentCulture)} shown";
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
        if (_known.TryGetValue(entry.DocumentId, out var item))
        {
            item.Update(entry);
            return item;
        }
        return _known[entry.DocumentId] = new LibraryItemViewModel(entry, _covers);
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
        FormatChoice = FormatChoices[0];
        AiChoice = AiChoices[0];
        FolderChoice = AllFolders;
        SystemChoice = AllSystems;
        TypeChoice = AllTypes;
        LevelChoice = AnyLevel;
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
            var inspector = await InspectorViewModel.LoadAsync(item, _queries, _library, _metadata, _indexing);
            inspector.MetadataChanged += async (_, _) => await RefreshAsync();
            Inspector = inspector;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading details for document {DocumentId} failed", item.DocumentId);
        }
    }

    [RelayCommand]
    void CloseDetails() => Inspector = null;

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
        var title = _known.TryGetValue(page.Hit.DocumentId, out var item) ? item.Title : "";
        return new ViewerRequest(page.Hit.DocumentId, title, page.Hit.PdfPage, Search.Query);
    }

    /// <summary>Opens a book at its first page, from the inspector or a card's menu.</summary>
    [RelayCommand]
    void OpenBook(LibraryItemViewModel item) => _readers.OpenInMainWindow(new ViewerRequest(item.DocumentId, item.Title));

    /// <summary>Opens a book in a window of its own, from a card's menu, the inspector, Shift+Enter or middle-click.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    Task OpenBookInNewWindow(LibraryItemViewModel item) => _readers.OpenInNewWindowAsync(new ViewerRequest(item.DocumentId, item.Title));

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
        EditSelectedCommand.NotifyCanExecuteChanged();
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
            var bulk = await BulkEditViewModel.LoadAsync(Selection.DocumentIds, _metadata);
            bulk.Closed += async (_, result) => await BulkEditClosedAsync(result);
            Inspector = null;
            BulkEdit = bulk;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading metadata for {Count} selected documents failed", Selection.Count);
        }
    }

    async Task BulkEditClosedAsync(BulkEditResult? result)
    {
        BulkEdit = null;
        if (result is null) return;
        // Leaving the Library while it applied ends the Undo, as leaving afterwards would.
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

    /// <summary>Esc closes the topmost thing: the bulk editor (or its summary), the details, then Select mode.</summary>
    [RelayCommand]
    void Escape()
    {
        if (BulkEdit is { } bulk) bulk.EscapeCommand.Execute(null);
        else if (Inspector is not null) Inspector = null;
        else Selection.Stop();
    }
}
