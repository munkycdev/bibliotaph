using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Bibliotaph.App.Services;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>The counted choices of the filter panel's menus, and of its type-ahead boxes in their order.</summary>
public sealed record FacetMenus(
    IReadOnlyList<FacetCount> Systems, IReadOnlyList<FacetCount> Types, IReadOnlyList<FacetCount> Owns, IReadOnlyList<FacetCount> Styles,
    IReadOnlyList<FacetCount> Durations, IReadOnlyList<FacetCount> Stores, IReadOnlyList<IReadOnlyList<ValueCount>> Chips);

/// <summary>
/// The Library's filters: the four common menus (game system, type, level, players), those folded under More filters,
/// the type-ahead boxes, and the chips that show which are on. A filter the user changes raises <see cref="Changed"/>
/// once, and the Library runs its query again; putting a saved search back, clearing them all at once or showing new
/// counts doesn't raise it for each.
/// </summary>
public sealed partial class LibraryFilterState : ObservableObject
{
    static readonly Choice<KindFilter> AllKinds = new(KindFilter.All, "All kinds");
    static readonly Choice<AiFilter> AnyAi = new(AiFilter.All, "All books");
    static readonly Choice<bool> AnyCopies = new(false, "All books");
    static readonly Choice<AdventureCards> InBooks = new(AdventureCards.InBooks, "Inside their book");
    static readonly Choice<CollectionFilter> AnyCollection = new(CollectionFilter.All, "All books");
    static readonly Choice<long?> AllFolders = new(null, "All folders");
    static readonly Choice<string?> AllSystems = new(null, "All systems");
    static readonly Choice<string?> AllTypes = new(null, "All types");
    static readonly Choice<int?> AnyLevel = new(null, "Any level");
    static readonly Choice<string?> AnyOwn = new(null, "Also own: any");
    static readonly Choice<string?> AnyStyle = new(null, "Any style");
    static readonly Choice<string?> AnyDuration = new(null, "Any length");
    static readonly Choice<int?> AnyPlayers = new(null, "Any number");
    static readonly Choice<string?> AnyStore = new(null, "Any store");

    int _quiet;
    int _moreFiltersOn;
    long? _folderOnLoad;

    public LibraryFilterState()
    {
        foreach (var box in ChipFilters) box.Changed += (_, _) => OnChanged();
    }

    /// <summary>Raised when the user changes a filter, so the books shown follow.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Books, single images, packs, everything with a file on this PC or books owned elsewhere.
    /// </summary>
    public IReadOnlyList<Choice<KindFilter>> KindChoices { get; } =
        [AllKinds, new(KindFilter.Books, "Books"), new(KindFilter.Images, "Images"), new(KindFilter.Packs, "Packs"),
            new(KindFilter.Here, "On this PC"), new(KindFilter.Elsewhere, "Owned elsewhere")];

    public Choice<KindFilter> KindChoice { get; set => Choose(ref field, value, AllKinds); } = AllKinds;

    public ObservableCollection<Choice<long?>> FolderChoices { get; } = [AllFolders];

    public Choice<long?> FolderChoice { get; set => Choose(ref field, value, AllFolders); } = AllFolders;

    /// <summary>"All systems", then each game system with how many books have it, then Unknown.</summary>
    public ObservableCollection<Choice<string?>> SystemChoices { get; } = [AllSystems];

    public Choice<string?> SystemChoice { get; set => Choose(ref field, value, AllSystems); } = AllSystems;

    public ObservableCollection<Choice<string?>> TypeChoices { get; } = [AllTypes];

    public Choice<string?> TypeChoice { get; set => Choose(ref field, value, AllTypes); } = AllTypes;

    public IReadOnlyList<Choice<int?>> LevelChoices { get; } =
        [AnyLevel, .. Enumerable.Range(1, 20).Select(l => new Choice<int?>(l, $"Level {l.ToString(CultureInfo.CurrentCulture)}"))];

    public Choice<int?> LevelChoice { get; set => Choose(ref field, value, AnyLevel, nameof(HasLevelChoice)); } = AnyLevel;

    /// <summary>Whether a level filter also keeps books whose levels nobody knows yet.</summary>
    [ObservableProperty]
    public partial bool IncludeUnknownLevels { get; set; }

    public bool HasLevelChoice => LevelChoice.Value is not null;

    /// <summary>How many players: books whose stated party size includes the number chosen.</summary>
    public IReadOnlyList<Choice<int?>> PlayersChoices { get; } =
        [AnyPlayers, new Choice<int?>(1, "1 player"), .. Enumerable.Range(2, 9).Select(n => new Choice<int?>(n, $"{n.ToString(CultureInfo.CurrentCulture)} players"))];

    public Choice<int?> PlayersChoice { get; set => Choose(ref field, value, AnyPlayers); } = AnyPlayers;

    /// <summary>
    /// Where else books are owned: "Also own: any", then Print, Foundry VTT and so on with how many books each has.
    /// Shown once some book has a value, or while one is chosen.
    /// </summary>
    public ObservableCollection<Choice<string?>> OwnChoices { get; } = [AnyOwn];

    public Choice<string?> OwnChoice { get; set => Choose(ref field, value, AnyOwn); } = AnyOwn;

    [ObservableProperty]
    public partial bool ShowOwnChoice { get; private set; }

    /// <summary>
    /// The kind of adventure (heist, dungeon crawl) with how many books each has, and how long they take to play.
    /// Each is shown once some book has a value, or while one is chosen.
    /// </summary>
    public ObservableCollection<Choice<string?>> StyleChoices { get; } = [AnyStyle];

    public Choice<string?> StyleChoice { get; set => Choose(ref field, value, AnyStyle); } = AnyStyle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdventureFilters))]
    public partial bool ShowStyleChoice { get; private set; }

    public ObservableCollection<Choice<string?>> DurationChoices { get; } = [AnyDuration];

    public Choice<string?> DurationChoice { get; set => Choose(ref field, value, AnyDuration); } = AnyDuration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAdventureFilters))]
    public partial bool ShowDurationChoice { get; private set; }

    /// <summary>The adventure's group shows once Style or Duration does.</summary>
    public bool ShowAdventureFilters => ShowStyleChoice || ShowDurationChoice;

    /// <summary>Where to get it, with how many books each store has; shown once some book has one.</summary>
    public ObservableCollection<Choice<string?>> StoreChoices { get; } = [AnyStore];

    public Choice<string?> StoreChoice { get; set => Choose(ref field, value, AnyStore); } = AnyStore;

    [ObservableProperty]
    public partial bool ShowStoreChoice { get; private set; }

    /// <summary>
    /// The type-ahead filters for fields with too many values for a menu: setting, themes, environments, publisher and
    /// edition. Each box's chips widen it; the boxes narrow together, and with every other filter.
    /// </summary>
    public IReadOnlyList<ChipFilterViewModel> ChipFilters { get; } = [.. TypeAheadFilters.All.Select(f => new ChipFilterViewModel(f.Field, f.Hint))];

    /// <summary>Their group shows once one of them has something to offer.</summary>
    public bool ShowAboutFilters => ChipFilters.Any(b => b.IsOffered);

    /// <summary>Books a model has read: shown once there are some, or while a choice is made.</summary>
    public IReadOnlyList<Choice<AiFilter>> AiChoices { get; } = [AnyAi, new(AiFilter.Read, "Read by AI"), new(AiFilter.NotRead, "Not read by AI")];

    public Choice<AiFilter> AiChoice { get; set => Choose(ref field, value, AnyAi); } = AnyAi;

    [ObservableProperty]
    public partial bool ShowAiChoice { get; private set; }

    /// <summary>
    /// Books in more than one file, so the copies Bibliotaph joined can be checked: shown once there are some, or
    /// while it is chosen.
    /// </summary>
    public IReadOnlyList<Choice<bool>> CopiesChoices { get; } = [AnyCopies, new(true, "Books with copies")];

    public Choice<bool> CopiesChoice { get; set => Choose(ref field, value, AnyCopies); } = AnyCopies;

    [ObservableProperty]
    public partial bool ShowCopiesChoice { get; private set; }

    /// <summary>Books in some collection, or in none yet: shown once there are collections, or while it is chosen.</summary>
    public IReadOnlyList<Choice<CollectionFilter>> CollectionChoices { get; } =
        [AnyCollection, new(CollectionFilter.InOne, "In a collection"), new(CollectionFilter.NotInOne, "Not in a collection")];

    public Choice<CollectionFilter> CollectionChoice { get; set => Choose(ref field, value, AnyCollection); } = AnyCollection;

    [ObservableProperty]
    public partial bool ShowCollectionChoice { get; private set; }

    /// <summary>
    /// Adventures inside files: inside their books (only a search shows them as cards), shown as cards of their own, or
    /// hidden so only their files show. Offered once there are some, or while another than the first is chosen.
    /// </summary>
    public IReadOnlyList<Choice<AdventureCards>> PartsChoices { get; } =
        [InBooks, new(AdventureCards.Cards, "Show as cards"), new(AdventureCards.Hidden, "Hide")];

    public Choice<AdventureCards> PartsChoice { get; set => Choose(ref field, value, InBooks); } = InBooks;

    [ObservableProperty]
    public partial bool ShowPartsChoice { get; private set; }

    /// <summary>The filter panel, opened from the toolbar's Filters.</summary>
    [ObservableProperty]
    public partial bool ShowFilters { get; set; }

    /// <summary>
    /// The filters past the common four (game system, type, level, players), in groups: the adventure, where the book
    /// lives, and housekeeping. Folded away until asked for; the chips show any of them that are on.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreFiltersLabel))]
    public partial bool ShowMoreFilters { get; set; }

    /// <summary>The filters that are on, as chips under the toolbar, so they show with the panel closed; a chip's × turns one off.</summary>
    public ObservableCollection<FilterChip> ActiveFilters { get; } = [];

    public bool HasActiveFilters => ActiveFilters.Count > 0;

    /// <summary>Clear filters beside the chips, once there is more than one to clear.</summary>
    public bool HasSeveralFilters => ActiveFilters.Count > 1;

    /// <summary>"Filters", or "Filters · 2" with two on.</summary>
    public string FiltersLabel => ActiveFilters.Count > 0 ? $"Filters · {ActiveFilters.Count.ToString(CultureInfo.CurrentCulture)}" : "Filters";

    /// <summary>"More filters", with how many of them are on while they are folded away.</summary>
    public string MoreFiltersLabel => ShowMoreFilters ? "Fewer filters"
        : _moreFiltersOn > 0 ? $"More filters · {_moreFiltersOn.ToString(CultureInfo.CurrentCulture)}" : "More filters";

    /// <summary>Some filter leaves books out, so an empty page offers to clear them.</summary>
    public bool IsFiltering => KindChoice.Value != KindFilter.All || FolderChoice.Value is not null || SystemChoice.Value is not null
        || TypeChoice.Value is not null || LevelChoice.Value is not null || AiChoice.Value != AiFilter.All || CopiesChoice.Value || OwnChoice.Value is not null
        || StyleChoice.Value is not null || DurationChoice.Value is not null || PlayersChoice.Value is not null || StoreChoice.Value is not null
        || PartsChoice.Value != AdventureCards.InBooks || CollectionChoice.Value != CollectionFilter.All || ChipFilters.Any(b => b.Picks.Count > 0);

    partial void OnIncludeUnknownLevelsChanged(bool value)
    {
        if (LevelChoice.Value is not null) OnChanged();
    }

    /// <summary>
    /// Sets a menu's choice and says the filters changed. A menu whose items are replaced can briefly select nothing;
    /// the filters never hold null, so nothing selected is the menu's first choice, and the menu is told so.
    /// </summary>
    void Choose<T>(ref Choice<T> slot, Choice<T>? value, Choice<T> first, string? alsoChanged = null, [CallerMemberName] string? name = null)
    {
        if (value is null)
        {
            OnPropertyChanging(name);
            slot = first;
            OnPropertyChanged(name);
        }
        else if (!SetProperty(ref slot, value, name)) return;
        if (alsoChanged is not null) OnPropertyChanged(alsoChanged);
        OnChanged();
    }

    void OnChanged()
    {
        if (_quiet == 0) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes several changes without raising <see cref="Changed"/> for each.</summary>
    void Quietly(Action change)
    {
        _quiet++;
        try
        {
            change();
        }
        finally
        {
            _quiet--;
        }
    }

    [RelayCommand]
    void ToggleFilters() => ShowFilters = !ShowFilters;

    [RelayCommand]
    void ToggleMoreFilters() => ShowMoreFilters = !ShowMoreFilters;

    /// <summary>Clear filters: every filter off, then the books shown follow once.</summary>
    [RelayCommand]
    void ClearFilters()
    {
        Quietly(() =>
        {
            KindChoice = KindChoices[0];
            AiChoice = AiChoices[0];
            CopiesChoice = CopiesChoices[0];
            PartsChoice = PartsChoices[0];
            CollectionChoice = CollectionChoices[0];
            FolderChoice = AllFolders;
            SystemChoice = AllSystems;
            TypeChoice = AllTypes;
            LevelChoice = AnyLevel;
            OwnChoice = AnyOwn;
            StyleChoice = AnyStyle;
            DurationChoice = AnyDuration;
            PlayersChoice = AnyPlayers;
            StoreChoice = AnyStore;
            foreach (var box in ChipFilters) box.Clear();
            IncludeUnknownLevels = false;
        });
        OnChanged();
    }

    /// <summary>
    /// What the query keeps: the books in <paramref name="scope"/> (those in the chosen folder, or every one), in
    /// <paramref name="group"/> if the page shows only a part of the library, in the order of <paramref name="sort"/>.
    /// </summary>
    public LibraryFilter ToFilter(IReadOnlyCollection<EntryId> scope, LibrarySort sort, string? group) =>
        new(scope, KindChoice.Value, sort, Selected(SystemChoice), Selected(TypeChoice), LevelChoice.Value,
            IncludeUnknownLevels, AiChoice.Value, CopiesChoice.Value, Selected(OwnChoice), group, Styles: Selected(StyleChoice),
            Durations: Selected(DurationChoice), Players: PlayersChoice.Value, Stores: Selected(StoreChoice),
            HideParts: PartsChoice.Value == AdventureCards.Hidden, Collections: CollectionChoice.Value, Picked: ChipPicks(),
            PartsInBooks: PartsChoice.Value == AdventureCards.InBooks);

    /// <summary>The filters as a saved search keeps them. Its search, scope, order, layout and tab are the page's to add.</summary>
    public SmartViewDefinition ToView() => new(Kind: KindChoice.Value, Folder: FolderChoice.Value, System: SystemChoice.Value, Type: TypeChoice.Value,
        Level: LevelChoice.Value, IncludeUnknownLevels: LevelChoice.Value is not null && IncludeUnknownLevels, Ai: AiChoice.Value, Copies: CopiesChoice.Value,
        Own: OwnChoice.Value, Style: StyleChoice.Value, Duration: DurationChoice.Value, Players: PlayersChoice.Value, Store: StoreChoice.Value,
        Collections: CollectionChoice.Value, Picked: ChipPicks(), Adventures: PartsChoice.Value);

    /// <summary>
    /// Puts a saved search's filters back, without raising <see cref="Changed"/>: the page refreshes once it has put the
    /// rest back too. A counted menu's value shows by itself until the counts arrive; its folder is chosen once the
    /// folders are known (<see cref="ShowFolders"/>).
    /// </summary>
    public void Apply(SmartViewDefinition view) => Quietly(() =>
    {
        KindChoice = KindChoices.FirstOrDefault(c => c.Value == view.Kind) ?? KindChoices[0];
        AiChoice = AiChoices.FirstOrDefault(c => c.Value == view.Ai) ?? AiChoices[0];
        CopiesChoice = CopiesChoices.FirstOrDefault(c => c.Value == view.Copies) ?? CopiesChoices[0];
        PartsChoice = PartsChoices.FirstOrDefault(c => c.Value == view.Adventures) ?? PartsChoices[0];
        CollectionChoice = CollectionChoices.FirstOrDefault(c => c.Value == view.Collections) ?? CollectionChoices[0];
        SystemChoice = Pick(SystemChoices, AllSystems, view.System);
        TypeChoice = Pick(TypeChoices, AllTypes, view.Type);
        OwnChoice = Pick(OwnChoices, AnyOwn, view.Own);
        StyleChoice = Pick(StyleChoices, AnyStyle, view.Style);
        StoreChoice = Pick(StoreChoices, AnyStore, view.Store);
        DurationChoice = Pick(DurationChoices, AnyDuration, view.Duration);
        foreach (var box in ChipFilters) box.Restore(view.Picked?[box.Field.Key] ?? []);
        PlayersChoice = PlayersChoices.FirstOrDefault(c => c.Value == view.Players) ?? AnyPlayers;
        LevelChoice = LevelChoices.FirstOrDefault(c => c.Value == view.Level) ?? AnyLevel;
        IncludeUnknownLevels = view.IncludeUnknownLevels;
        _folderOnLoad = view.Folder;
    });

    /// <summary>A counted menu's choice for a saved value, added to the menu until the counts arrive.</summary>
    static Choice<string?> Pick(ObservableCollection<Choice<string?>> menu, Choice<string?> all, string? value)
    {
        if (value is null) return all;
        if (menu.FirstOrDefault(c => c.Value == value) is { } found) return found;
        var choice = new Choice<string?>(value, value);
        menu.Add(choice);
        return choice;
    }

    /// <summary>
    /// The source folders to choose from, keeping the one chosen while it is still there, and then the folder of a
    /// saved search just put back.
    /// </summary>
    public void ShowFolders(IReadOnlyList<Choice<long?>> folders) => Quietly(() =>
    {
        if (!FolderChoices.Skip(1).SequenceEqual(folders))
        {
            var selected = FolderChoice;
            while (FolderChoices.Count > 1) FolderChoices.RemoveAt(1);
            foreach (var choice in folders) FolderChoices.Add(choice);
            FolderChoice = FolderChoices.Contains(selected) ? selected : AllFolders;
        }
        if (_folderOnLoad is { } folder)
        {
            _folderOnLoad = null;
            FolderChoice = FolderChoices.FirstOrDefault(c => c.Value == folder) ?? AllFolders;
        }
    });

    /// <summary>
    /// Shows the AI, Copies, Adventures inside files and Collection filters once the library has books for them (or
    /// collections), or while one is chosen.
    /// </summary>
    public void ShowCounts(long aiRead, long withCopies, long parts, bool hasCollections)
    {
        ShowAiChoice = aiRead > 0 || AiChoice.Value != AiFilter.All;
        ShowCopiesChoice = withCopies > 0 || CopiesChoice.Value;
        ShowPartsChoice = parts > 0 || PartsChoice.Value != AdventureCards.InBooks;
        ShowCollectionChoice = hasCollections || CollectionChoice.Value != CollectionFilter.All;
    }

    /// <summary>The menus' and boxes' values with how many books each would show, and the groups that have something to offer.</summary>
    public void ShowFacets(FacetMenus facets)
    {
        Quietly(() =>
        {
            SystemChoice = ShowChoices(SystemChoices, AllSystems, facets.Systems, SystemChoice);
            TypeChoice = ShowChoices(TypeChoices, AllTypes, facets.Types, TypeChoice);
            OwnChoice = ShowChoices(OwnChoices, AnyOwn, facets.Owns, OwnChoice);
            ShowOwnChoice = facets.Owns.Any(c => c.Value != SearchQuery.Unknown) || OwnChoice.Value is not null;
            StyleChoice = ShowChoices(StyleChoices, AnyStyle, facets.Styles, StyleChoice);
            ShowStyleChoice = facets.Styles.Any(c => c.Value != SearchQuery.Unknown) || StyleChoice.Value is not null;
            DurationChoice = ShowChoices(DurationChoices, AnyDuration, facets.Durations, DurationChoice);
            ShowDurationChoice = facets.Durations.Any(c => c.Value != SearchQuery.Unknown) || DurationChoice.Value is not null;
            StoreChoice = ShowChoices(StoreChoices, AnyStore, facets.Stores, StoreChoice);
            ShowStoreChoice = facets.Stores.Any(c => c.Value != SearchQuery.Unknown) || StoreChoice.Value is not null;
            for (var i = 0; i < ChipFilters.Count; i++) ChipFilters[i].ShowCounts(facets.Chips[i]);
            OnPropertyChanged(nameof(ShowAboutFilters));
        });
        // A chip put back from a saved search shows its value's name once the counts have it.
        ShowChips();
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

    static string[]? Selected(Choice<string?> choice) => choice.Value is { } value ? [value] : null;

    /// <summary>The values picked in the type-ahead filters, or null with none.</summary>
    PickedValues? ChipPicks() => PickedValues.Of(ChipFilters.Select(b => KeyValuePair.Create(b.Field.Key, b.Values)));

    /// <summary>The chips for the filters that are on, common ones first, each turning its filter off.</summary>
    public IReadOnlyList<FilterChip> Chips() => Chips(out _);

    List<FilterChip> Chips(out int folded)
    {
        List<FilterChip> chips = [];
        var more = 0;
        void Add(bool on, string label, string filter, Action clear, bool isFolded = false)
        {
            if (!on) return;
            chips.Add(new FilterChip(label, $"Turn off the {filter} filter", new RelayCommand(clear)));
            if (isFolded) more++;
        }
        Add(SystemChoice.Value is not null, Uncounted(SystemChoice.Label), "game system", () => SystemChoice = AllSystems);
        Add(TypeChoice.Value is not null, Uncounted(TypeChoice.Label), "type", () => TypeChoice = AllTypes);
        Add(LevelChoice.Value is not null, IncludeUnknownLevels ? $"{LevelChoice.Label} or unknown" : LevelChoice.Label, "character level",
            () => LevelChoice = AnyLevel);
        Add(PlayersChoice.Value is not null, PlayersChoice.Label, "players", () => PlayersChoice = AnyPlayers);
        Add(StyleChoice.Value is not null, Uncounted(StyleChoice.Label), "style", () => StyleChoice = AnyStyle, isFolded: true);
        Add(DurationChoice.Value is not null, Uncounted(DurationChoice.Label), "duration", () => DurationChoice = AnyDuration, isFolded: true);
        foreach (var box in ChipFilters)
            foreach (var pick in box.Picks)
            {
                var label = $"{box.Label}: {pick.Label}";
                chips.Add(new FilterChip(label, $"Remove {label}", new RelayCommand(() => box.Remove(pick.Value))));
                more++;
            }
        Add(FolderChoice.Value is not null, $"Folder: {FolderChoice.Label}", "source folder", () => FolderChoice = AllFolders, isFolded: true);
        Add(OwnChoice.Value is not null, $"Also own: {Uncounted(OwnChoice.Label)}", "also own", () => OwnChoice = AnyOwn, isFolded: true);
        Add(StoreChoice.Value is not null, $"Get it from: {Uncounted(StoreChoice.Label)}", "get it from", () => StoreChoice = AnyStore, isFolded: true);
        Add(KindChoice.Value != KindFilter.All, KindChoice.Label, "kind", () => KindChoice = KindChoices[0], isFolded: true);
        Add(AiChoice.Value != AiFilter.All, AiChoice.Label, "AI", () => AiChoice = AiChoices[0], isFolded: true);
        Add(CopiesChoice.Value, CopiesChoice.Label, "copies", () => CopiesChoice = CopiesChoices[0], isFolded: true);
        Add(PartsChoice.Value != AdventureCards.InBooks, PartsChoice.Value == AdventureCards.Hidden ? "Adventures inside files hidden" : "Adventures as cards",
            "adventures inside files", () => PartsChoice = PartsChoices[0], isFolded: true);
        Add(CollectionChoice.Value != CollectionFilter.All, CollectionChoice.Label, "collection", () => CollectionChoice = CollectionChoices[0], isFolded: true);
        folded = more;
        return chips;
    }

    /// <summary>Shows the chips for the filters that are on, and how many of those are folded under More filters.</summary>
    public void ShowChips()
    {
        var chips = Chips(out _moreFiltersOn);
        OnPropertyChanged(nameof(MoreFiltersLabel));
        if (ActiveFilters.Select(c => c.Label).SequenceEqual(chips.Select(c => c.Label))) return;
        ActiveFilters.Clear();
        foreach (var chip in chips) ActiveFilters.Add(chip);
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(HasSeveralFilters));
        OnPropertyChanged(nameof(FiltersLabel));
    }
}
