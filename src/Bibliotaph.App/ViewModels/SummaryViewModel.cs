using System.Globalization;
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

/// <summary>
/// Where a figure on the Summary page goes: the Library with a search and filters, so it shows the books counted, or
/// another page.
/// </summary>
public sealed record SummaryLink(string Query = "", SmartViewDefinition? Filters = null, Route? Page = null);

/// <summary>A count in a tile: "312 Adventures".</summary>
public sealed record SummaryTile(string Count, string Label, string? Detail, SummaryLink? Link);

/// <summary>
/// One row of a bar list. <see cref="Confirmed"/> and <see cref="Total"/> are the bar's lengths as fractions of the
/// longest row's; the part of the bar between them is suggested and not yet confirmed.
/// </summary>
public sealed record SummaryBar(string Label, string Count, double Confirmed, double Total, string ToolTip, SummaryLink? Link, bool Offline = false);

/// <summary>A list of bars under a heading, with a note such as "14 in all" when it holds more than it shows.</summary>
public sealed record SummaryList(string Heading, string? Note, IReadOnlyList<SummaryBar> Bars);

/// <summary>One column of a strip: a level or a decade. <see cref="Height"/> is in pixels.</summary>
public sealed record SummaryColumn(string Tick, double Height, string ToolTip, SummaryLink? Link);

/// <summary>
/// Summary: the library by the numbers. How much there is, where it came from, what it is and what has been done with
/// it, each figure opening the Library at the books it counts.
/// </summary>
public sealed partial class SummaryViewModel(LibrarySummaryService summaries, SourceRootStore roots, LibraryActivity activity, LibraryPages pages,
    INavigationService navigation, LibraryFolders folders, SettingsLinks settings, WebShelfCache shelf, ILogger<SummaryViewModel> log)
    : LibraryAwarePageViewModel(roots, activity)
{
    /// <summary>The height of the tallest column in a strip.</summary>
    public const double StripHeight = 72;

    static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    int _version;

    public override Route Route => Route.Summary;
    public override string Title => "Summary";

    /// <summary>The whole Library, as the hero line's count of books opens it.</summary>
    public SummaryLink Everything { get; } = new();

    /// <summary>The books not opened yet.</summary>
    public SummaryLink NeverOpened { get; } = new($"{SearchFields.Opened.Name}:{SearchQuery.No}");

    /// <summary>The figures have been read once, so the page shows them or says the library is empty.</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool HasBooks { get; private set; }

    public bool IsEmpty => IsLoaded && !HasBooks;

    partial void OnIsLoadedChanged(bool value) => OnPropertyChanged(nameof(IsEmpty));

    [ObservableProperty]
    public partial string BooksText { get; private set; } = "";

    [ObservableProperty]
    public partial string PagesText { get; private set; } = "";

    [ObservableProperty]
    public partial string SizeText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ReadingTime { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SummaryTile> Hoard { get; private set; } = [];

    [ObservableProperty]
    public partial SummaryList? Folders { get; private set; }

    [ObservableProperty]
    public partial string? OfflineText { get; private set; }

    [ObservableProperty]
    public partial SummaryList? Sources { get; private set; }

    [ObservableProperty]
    public partial string? StoresText { get; private set; }

    [ObservableProperty]
    public partial SummaryList? Systems { get; private set; }

    [ObservableProperty]
    public partial SummaryList? Types { get; private set; }

    [ObservableProperty]
    public partial SummaryList? Settings { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SummaryColumn> Levels { get; private set; } = [];

    [ObservableProperty]
    public partial string? LevelsText { get; private set; }

    [ObservableProperty]
    public partial string? LevelsUnknownText { get; private set; }

    [ObservableProperty]
    public partial SummaryLink? LevelsUnknownLink { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SummaryColumn> Decades { get; private set; } = [];

    [ObservableProperty]
    public partial string? YearsText { get; private set; }

    [ObservableProperty]
    public partial string? YearUnknownText { get; private set; }

    [ObservableProperty]
    public partial string OpenedText { get; private set; } = "";

    [ObservableProperty]
    public partial string WaitingText { get; private set; } = "";

    /// <summary>How much of the library has been opened, from 0 to 1.</summary>
    [ObservableProperty]
    public partial double OpenedShare { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<SummaryTile> Work { get; private set; } = [];

    [ObservableProperty]
    public partial string ReadingText { get; private set; } = "";

    /// <summary>The growth chart's books at the end of each month, as fractions of the last month's.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<double> Growth { get; private set; } = [];

    /// <summary>"October 2026: 1,284 books" for each month, as the chart's pointer shows it.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> GrowthLabels { get; private set; } = [];

    [ObservableProperty]
    public partial string GrowthStart { get; private set; } = "";

    [ObservableProperty]
    public partial string GrowthMiddle { get; private set; } = "";

    [ObservableProperty]
    public partial string GrowthEnd { get; private set; } = "";

    [ObservableProperty]
    public partial string? BiggestDayText { get; private set; }

    public bool HasGrowth => Growth.Count > 1;

    partial void OnGrowthChanged(IReadOnlyList<double> value) => OnPropertyChanged(nameof(HasGrowth));

    public override async Task LoadAsync()
    {
        await base.LoadAsync();
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

    /// <summary>A figure: the Library at the books it counts, or the page it names.</summary>
    [RelayCommand]
    void Open(SummaryLink? link)
    {
        if (link is null) return;
        if (link.Page is { } page) navigation.NavigateTo(page);
        else pages.OpenFiltered(link.Query, link.Filters);
    }

    [RelayCommand]
    void ManageFolders() => folders.Manage();

    [RelayCommand]
    void ImportMore() => settings.Open(SettingsSection.OnlineLibraries);

    public async Task RefreshAsync()
    {
        var version = ++_version;
        try
        {
            var summary = await Task.Run(() => summaries.GetAsync());
            if (version != _version) return;
            Show(summary);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the library summary failed");
        }
    }

    void Show(LibrarySummary summary)
    {
        HasBooks = summary.Books > 0;
        IsLoaded = true;
        (BooksText, PagesText, SizeText) = SummaryText.Hero(summary);
        ReadingTime = SummaryText.ReadingTime(summary.Pages);
        Hoard = HoardTiles(summary);

        Folders = FolderList(summary.Folders);
        OfflineText = SummaryText.Offline(summary.Folders);
        Sources = summary.Sources.Count == 0 ? null : List("Online libraries and stores", summary.Sources, Link);
        StoresText = SummaryText.Stores(summary);

        Systems = List("Game systems", summary.Systems, Link,
            summary.SystemUnknown > 0 ? new SummaryValue("", "", "Not cataloged yet", summary.SystemUnknown, 0) : null,
            new SummaryLink($"{SearchFields.System.Name}:{SearchQuery.Unknown}"));
        Types = List("Types", summary.Types, Link);
        Settings = List("Settings", summary.Settings, Link);

        Levels = LevelColumns(summary.Levels);
        LevelsText = SummaryText.Levels(summary);
        LevelsUnknownText = summary.LevelsUnknown > 0 ? $"Levels unknown: {SummaryText.Count(summary.LevelsUnknown)}" : null;
        LevelsUnknownLink = new SummaryLink($"{SearchFields.Type.Name}:{AdventureType} {SearchFields.Level.Name}:{SearchQuery.Unknown}");
        Decades = DecadeColumns(summary.Decades);
        YearsText = SummaryText.Years(summary);
        YearUnknownText = summary.YearUnknown > 0 && summary.Decades.Count > 0 ? $"Year unknown: {SummaryText.Count(summary.YearUnknown)}" : null;

        (OpenedText, WaitingText) = SummaryText.Backlog(summary);
        OpenedShare = summary.Books == 0 ? 0 : (double)summary.Opened / summary.Books;
        Work = WorkTiles(summary);
        ReadingText = SummaryText.Reading(summary);

        var last = summary.Growth.Count == 0 ? 0 : Math.Max(1, summary.Growth[^1].Total);
        Growth = [.. summary.Growth.Select(m => (double)m.Total / last)];
        GrowthLabels = [.. summary.Growth.Select(m => $"{Month(m.Month, "MMMM yyyy")}: {SummaryText.Plural(m.Total, "book", "books")}")];
        GrowthStart = summary.Growth.Count > 0 ? Month(summary.Growth[0].Month, "MMM yyyy") : "";
        GrowthMiddle = summary.Growth.Count > 2 ? Month(summary.Growth[summary.Growth.Count / 2].Month, "MMM") : "";
        GrowthEnd = summary.Growth.Count > 1 ? Month(summary.Growth[^1].Month, "MMM yyyy") : "";
        BiggestDayText = SummaryText.BiggestDay(summary.BiggestDay);
    }

    /// <summary>The type vocabulary's key for adventures.</summary>
    const string AdventureType = "adventure";

    static string Month(DateOnly month, string format) => month.ToString(format, Culture);

    static List<SummaryTile> HoardTiles(LibrarySummary summary)
    {
        var tiles = new List<SummaryTile>();
        var adventures = summary.Types.FirstOrDefault(t => t.Value == AdventureType)?.Count ?? 0;
        if (adventures > 0 || summary.Adventures > 0)
            tiles.Add(new(SummaryText.Count(adventures), "Adventures",
                summary.Adventures > 0 ? $"and {SummaryText.Count(summary.Adventures)} inside other books" : null,
                new SummaryLink(Filters: new SmartViewDefinition(Type: AdventureType, Adventures: AdventureCards.Cards))));
        if (summary.Ebooks > 0)
            tiles.Add(new(SummaryText.Count(summary.Ebooks), "E-books", null, new SummaryLink($"{SearchFields.Format.Name}:ebook")));
        if (summary.Packs > 0)
            tiles.Add(new(SummaryText.Count(summary.Packs), "Packs", "folders and ZIPs kept as one", new SummaryLink(Filters: new SmartViewDefinition(Kind: KindFilter.Packs))));
        if (summary.Duplicates > 0)
            tiles.Add(new(SummaryText.Count(summary.Duplicates), "Duplicates found", "the same file again", new SummaryLink(Filters: new SmartViewDefinition(Copies: true))));
        if (summary.Elsewhere > 0)
            tiles.Add(new(SummaryText.Count(summary.Elsewhere), "Owned with no file", "in print, online or on a VTT",
                new SummaryLink(Filters: new SmartViewDefinition(Kind: KindFilter.Elsewhere))));
        return tiles;
    }

    static IReadOnlyList<SummaryTile> WorkTiles(LibrarySummary summary) =>
    [
        new(SummaryText.Count(summary.Favorites), "Favorites", null, new SummaryLink($"{SearchFields.Favorite.Name}:{SearchQuery.Yes}")),
        new(SummaryText.Count(summary.Notes), summary.Notes == 1 ? "Note" : "Notes",
            summary.NotedBooks > 0 ? $"on {SummaryText.Plural(summary.NotedBooks, "book", "books")}" : null, null),
        new(SummaryText.Count(summary.Collections), summary.Collections == 1 ? "Collection" : "Collections", null, new SummaryLink(Page: Route.Collections)),
        new(SummaryText.Count(summary.Binders), summary.Binders == 1 ? "Binder" : "Binders",
            summary.BinderItems > 0 ? $"{SummaryText.Plural(summary.BinderItems, "item", "items")} ready" : null, new SummaryLink(Page: Route.Sessions)),
        new(SummaryText.Count(summary.SavedSearches), summary.SavedSearches == 1 ? "Saved search" : "Saved searches", null, null),
        new(SummaryText.Count(summary.ReadByAi), "Read by AI", null, new SummaryLink(Filters: new SmartViewDefinition(Ai: AiFilter.Read))),
    ];

    SummaryList? FolderList(IReadOnlyList<SummaryFolder> list)
    {
        if (list.Count == 0) return null;
        var longest = Math.Max(1, list.Max(f => f.Books));
        return new SummaryList("Folders and drives", null,
        [
            .. list.Select(f =>
            {
                var name = shelf.NameOf(f.Path);
                var count = $"{SummaryText.Count(f.Books)} · {SummaryText.Size(f.Bytes)}";
                var share = (double)f.Books / longest;
                return new SummaryBar(f.Offline ? $"{name} (offline)" : name, count, share, share,
                    $"{shelf.Show(f.Path)}: {SummaryText.Plural(f.Books, "book", "books")}, {SummaryText.Size(f.Bytes)}",
                    new SummaryLink(Filters: new SmartViewDefinition(Folder: f.Id)), f.Offline);
            }),
        ]);
    }

    /// <summary>The Library filtered to one value, with the filter the Library has for its field.</summary>
    static SummaryLink? Link(SummaryValue value) => value.Field switch
    {
        "system" => new SummaryLink(Filters: new SmartViewDefinition(System: value.Value)),
        "type" => new SummaryLink(Filters: new SmartViewDefinition(Type: value.Value)),
        "own" => new SummaryLink(Filters: new SmartViewDefinition(Own: value.Value)),
        "store" => new SummaryLink(Filters: new SmartViewDefinition(Store: value.Value)),
        "setting" => new SummaryLink(Filters: new SmartViewDefinition(Picked: PickedValues.Of([KeyValuePair.Create(MetadataFields.Settings.Key, (IReadOnlyList<string>)[value.Value])]))),
        _ => null,
    };

    /// <summary>
    /// The most common values, then the rest as Other; <paramref name="extra"/> is a row after them all, such as the
    /// books with no system yet.
    /// </summary>
    static SummaryList? List(string heading, IReadOnlyList<SummaryValue> values, Func<SummaryValue, SummaryLink?> link,
        SummaryValue? extra = null, SummaryLink? extraLink = null)
    {
        if (values.Count == 0 && extra is null) return null;
        var shown = values.Count > SummaryText.ListLength ? values.Take(SummaryText.ListLength - 1).ToList() : [.. values];
        var rest = values.Skip(shown.Count).ToList();
        var rows = shown.Select(v => (Value: v, Link: link(v))).ToList();
        if (rest.Count > 0)
            rows.Add((new SummaryValue("", "", $"{rest.Count.ToString("N0", Culture)} others", rest.Sum(v => v.Count), rest.Sum(v => v.Confirmed)), null));
        if (extra is not null) rows.Add((extra, extraLink));
        var longest = Math.Max(1, rows.Max(r => r.Value.Count));
        var note = rest.Count > 0 ? $"{values.Count.ToString("N0", Culture)} in all" : null;
        return new SummaryList(heading, note,
        [
            .. rows.Select(r => new SummaryBar(r.Value.Label, SummaryText.Count(r.Value.Count), (double)r.Value.Confirmed / longest,
                (double)r.Value.Count / longest, BarTip(r.Value), r.Link)),
        ]);
    }

    static string BarTip(SummaryValue value)
    {
        var books = SummaryText.Plural(value.Count, "book", "books");
        if (value.Field.Length == 0) return $"{value.Label}: {books}";
        return value.Confirmed >= value.Count
            ? $"{value.Label}: {books}, all confirmed"
            : $"{value.Label}: {books}, {SummaryText.Count(value.Confirmed)} confirmed and {SummaryText.Count(value.Count - value.Confirmed)} suggested";
    }

    static IReadOnlyList<SummaryColumn> LevelColumns(IReadOnlyList<long> levels)
    {
        if (levels.All(l => l == 0)) return [];
        var highest = Math.Max(1, levels.Max());
        return
        [
            .. levels.Select((count, i) =>
            {
                var level = i + 1;
                return new SummaryColumn(level == 1 || level % 5 == 0 ? level.ToString(Culture) : "", StripHeight * count / highest,
                    $"Level {level}: {SummaryText.Plural(count, "book or adventure", "books and adventures")}",
                    new SummaryLink(Filters: new SmartViewDefinition(Level: level, Adventures: AdventureCards.Cards)));
            }),
        ];
    }

    static IReadOnlyList<SummaryColumn> DecadeColumns(IReadOnlyList<SummaryDecade> decades)
    {
        if (decades.Count == 0) return [];
        var highest = Math.Max(1, decades.Max(d => d.Count));
        return [.. decades.Select(d => new SummaryColumn($"{d.Decade}s", StripHeight * d.Count / highest, $"{d.Decade}s: {SummaryText.Plural(d.Count, "book", "books")}", null))];
    }
}
