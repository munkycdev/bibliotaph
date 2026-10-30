using System.Globalization;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;

namespace Bibliotaph.Core;

/// <summary>The page a Summary figure opens.</summary>
public enum SummaryGoal
{
    Library,
    Collections,
    Binders,
}

/// <summary>
/// The Library filters a Summary figure opens the Library with, in terms both clients have: <see cref="Field"/> and
/// <see cref="Value"/> for a vocabulary value (system, type, setting, own or store), <see cref="Kind"/> as the Library's
/// kind filter names it ("Packs", "Elsewhere"), and <see cref="AdventureCards"/> for the adventures inside files shown
/// as cards of their own, as levels and adventures count them.
/// </summary>
public sealed record SummaryFilter(
    string? Field = null, string? Value = null, long? Folder = null, int? Level = null, string? Kind = null, bool Copies = false,
    bool ReadByAi = false, bool AdventureCards = false);

/// <summary>Where a figure goes: the Library with <see cref="Query"/> in the search box and <see cref="Filter"/>, or another page.</summary>
public sealed record SummaryLink(string Query = "", SummaryFilter? Filter = null, SummaryGoal Goal = SummaryGoal.Library);

/// <summary>A count in a tile: "312 Adventures".</summary>
public sealed record SummaryTile(string Count, string Label, string? Detail, SummaryLink? Link);

/// <summary>
/// One row of a bar list. <see cref="Confirmed"/> and <see cref="Total"/> are the bar's lengths as fractions of the
/// longest row's; the part of the bar between them is suggested and not yet confirmed.
/// </summary>
public sealed record SummaryBar(string Label, string Count, double Confirmed, double Total, string ToolTip, SummaryLink? Link, bool Offline = false);

/// <summary>A list of bars under a heading, with a note such as "14 in all" when it holds more than it shows.</summary>
public sealed record SummaryList(string Heading, string? Note, IReadOnlyList<SummaryBar> Bars);

/// <summary>One column of a strip, a level or a decade: <see cref="Share"/> is its height as a fraction of the tallest's.</summary>
public sealed record SummaryColumn(string Tick, double Share, string ToolTip, SummaryLink? Link);

/// <summary>
/// The Summary page as both clients draw it: every line, tile, bar and column, with where each goes. A client only
/// lays it out and turns <see cref="SummaryLink"/> into its own way of opening the Library.
/// </summary>
public sealed record SummaryPage
{
    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>The type vocabulary's key for adventures.</summary>
    const string AdventureType = "adventure";

    /// <summary>The whole Library, as the hero line's count of books opens it.</summary>
    public static SummaryLink Everything { get; } = new();

    /// <summary>The books not opened yet.</summary>
    public static SummaryLink NeverOpened { get; } = new($"{SearchFields.Opened.Name}:{SearchQuery.No}");

    public bool HasBooks { get; init; }
    public string BooksText { get; init; } = "";
    public string PagesText { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string? ReadingTime { get; init; }
    public IReadOnlyList<SummaryTile> Hoard { get; init; } = [];

    public SummaryList? Folders { get; init; }
    public string? OfflineText { get; init; }
    public SummaryList? Sources { get; init; }
    public string? StoresText { get; init; }

    public SummaryList? Systems { get; init; }
    public SummaryList? Types { get; init; }
    public SummaryList? Settings { get; init; }
    public IReadOnlyList<SummaryColumn> Levels { get; init; } = [];
    public string? LevelsText { get; init; }
    public string? LevelsUnknownText { get; init; }
    public SummaryLink LevelsUnknownLink { get; } = new($"{SearchFields.Type.Name}:{AdventureType} {SearchFields.Level.Name}:{SearchQuery.Unknown}");
    public IReadOnlyList<SummaryColumn> Decades { get; init; } = [];
    public string? YearsText { get; init; }
    public string? YearUnknownText { get; init; }

    public string OpenedText { get; init; } = "";
    public string WaitingText { get; init; } = "";
    /// <summary>How much of the library has been opened, from 0 to 1.</summary>
    public double OpenedShare { get; init; }
    public IReadOnlyList<SummaryTile> Work { get; init; } = [];
    public string ReadingText { get; init; } = "";

    /// <summary>The books at the end of each month, as fractions of the last month's.</summary>
    public IReadOnlyList<double> Growth { get; init; } = [];
    /// <summary>"October 2026: 1,284 books" for each month, as pointing at the chart shows it.</summary>
    public IReadOnlyList<string> GrowthLabels { get; init; } = [];
    public string GrowthStart { get; init; } = "";
    public string GrowthMiddle { get; init; } = "";
    public string GrowthEnd { get; init; } = "";
    public string? BiggestDayText { get; init; }
    public bool HasGrowth => Growth.Count > 1;

    /// <summary>
    /// The page for <paramref name="summary"/>. <paramref name="folder"/> names a library folder the client's way, and
    /// says where it is for its bar's tooltip.
    /// </summary>
    public static SummaryPage Of(LibrarySummary summary, Func<SummaryFolder, (string Name, string Where)> folder)
    {
        var (books, pages, size) = SummaryText.Hero(summary);
        var (opened, waiting) = SummaryText.Backlog(summary);
        var growth = summary.Growth;
        var last = growth.Count == 0 ? 1 : Math.Max(1, growth[^1].Total);
        return new SummaryPage
        {
            HasBooks = summary.Books > 0,
            BooksText = books,
            PagesText = pages,
            SizeText = size,
            ReadingTime = SummaryText.ReadingTime(summary.Pages),
            Hoard = HoardTiles(summary),
            Folders = FolderList(summary.Folders, folder),
            OfflineText = SummaryText.Offline(summary.Folders),
            Sources = List("Online libraries and stores", summary.Sources),
            StoresText = SummaryText.Stores(summary),
            Systems = List("Game systems", summary.Systems,
                summary.SystemUnknown > 0 ? new SummaryValue("", "", "Not cataloged yet", summary.SystemUnknown, 0) : null,
                new SummaryLink($"{SearchFields.System.Name}:{SearchQuery.Unknown}")),
            Types = List("Types", summary.Types),
            Settings = List("Settings", summary.Settings),
            Levels = LevelColumns(summary.Levels),
            LevelsText = SummaryText.Levels(summary),
            LevelsUnknownText = summary.LevelsUnknown > 0 ? $"Levels unknown: {SummaryText.Count(summary.LevelsUnknown)}" : null,
            Decades = DecadeColumns(summary.Decades),
            YearsText = SummaryText.Years(summary),
            YearUnknownText = summary.YearUnknown > 0 && summary.Decades.Count > 0 ? $"Year unknown: {SummaryText.Count(summary.YearUnknown)}" : null,
            OpenedText = opened,
            WaitingText = waiting,
            OpenedShare = summary.Books == 0 ? 0 : (double)summary.Opened / summary.Books,
            Work = WorkTiles(summary),
            ReadingText = SummaryText.Reading(summary),
            Growth = [.. growth.Select(m => (double)m.Total / last)],
            GrowthLabels = [.. growth.Select(m => $"{Month(m.Month, "MMMM yyyy")}: {SummaryText.Plural(m.Total, "book", "books")}")],
            GrowthStart = growth.Count > 0 ? Month(growth[0].Month, "MMM yyyy") : "",
            GrowthMiddle = growth.Count > 2 ? Month(growth[growth.Count / 2].Month, "MMM") : "",
            GrowthEnd = growth.Count > 1 ? Month(growth[^1].Month, "MMM yyyy") : "",
            BiggestDayText = SummaryText.BiggestDay(summary.BiggestDay),
        };
    }

    static string Month(DateOnly month, string format) => month.ToString(format, Culture);

    static List<SummaryTile> HoardTiles(LibrarySummary summary)
    {
        var tiles = new List<SummaryTile>();
        var adventures = summary.Types.FirstOrDefault(t => t.Value == AdventureType)?.Count ?? 0;
        if (adventures > 0 || summary.Adventures > 0)
            tiles.Add(new(SummaryText.Count(adventures), "Adventures",
                summary.Adventures > 0 ? $"and {SummaryText.Count(summary.Adventures)} inside other books" : null,
                new SummaryLink(Filter: new SummaryFilter(MetadataFields.Types.Key, AdventureType, AdventureCards: true))));
        if (summary.Ebooks > 0)
            tiles.Add(new(SummaryText.Count(summary.Ebooks), "E-books", null, new SummaryLink($"{SearchFields.Format.Name}:ebook")));
        if (summary.Packs > 0)
            tiles.Add(new(SummaryText.Count(summary.Packs), "Packs", "folders and ZIPs kept as one", new SummaryLink(Filter: new SummaryFilter(Kind: "Packs"))));
        if (summary.Duplicates > 0)
            tiles.Add(new(SummaryText.Count(summary.Duplicates), "Duplicates found", "the same file again", new SummaryLink(Filter: new SummaryFilter(Copies: true))));
        if (summary.Elsewhere > 0)
            tiles.Add(new(SummaryText.Count(summary.Elsewhere), "Owned with no file", "in print, online or on a VTT",
                new SummaryLink(Filter: new SummaryFilter(Kind: "Elsewhere"))));
        return tiles;
    }

    static IReadOnlyList<SummaryTile> WorkTiles(LibrarySummary summary) =>
    [
        new(SummaryText.Count(summary.Favorites), summary.Favorites == 1 ? "Favorite" : "Favorites", null,
            new SummaryLink($"{SearchFields.Favorite.Name}:{SearchQuery.Yes}")),
        new(SummaryText.Count(summary.Notes), summary.Notes == 1 ? "Note" : "Notes",
            summary.NotedBooks > 0 ? $"on {SummaryText.Plural(summary.NotedBooks, "book", "books")}" : null, null),
        new(SummaryText.Count(summary.Collections), summary.Collections == 1 ? "Collection" : "Collections", null, new SummaryLink(Goal: SummaryGoal.Collections)),
        new(SummaryText.Count(summary.Binders), summary.Binders == 1 ? "Binder" : "Binders",
            summary.BinderItems > 0 ? $"{SummaryText.Plural(summary.BinderItems, "item", "items")} ready" : null, new SummaryLink(Goal: SummaryGoal.Binders)),
        new(SummaryText.Count(summary.SavedSearches), summary.SavedSearches == 1 ? "Saved search" : "Saved searches", null, null),
        new(SummaryText.Count(summary.ReadByAi), "Read by AI", null, new SummaryLink(Filter: new SummaryFilter(ReadByAi: true))),
    ];

    static SummaryList? FolderList(IReadOnlyList<SummaryFolder> list, Func<SummaryFolder, (string Name, string Where)> folder)
    {
        if (list.Count == 0) return null;
        var longest = Math.Max(1, list.Max(f => f.Books));
        return new SummaryList("Folders and drives", null,
        [
            .. list.Select(f =>
            {
                var (name, where) = folder(f);
                var share = (double)f.Books / longest;
                return new SummaryBar(f.Offline ? $"{name} (offline)" : name, $"{SummaryText.Count(f.Books)} · {SummaryText.Size(f.Bytes)}", share, share,
                    $"{where}: {SummaryText.Plural(f.Books, "book", "books")}, {SummaryText.Size(f.Bytes)}",
                    new SummaryLink(Filter: new SummaryFilter(Folder: f.Id)), f.Offline);
            }),
        ]);
    }

    /// <summary>
    /// The most common values, each opening the Library filtered to it, then the rest as one line; <paramref name="extra"/>
    /// is a row after them all, such as the books with no system yet.
    /// </summary>
    static SummaryList? List(string heading, IReadOnlyList<SummaryValue> values, SummaryValue? extra = null, SummaryLink? extraLink = null)
    {
        if (values.Count == 0 && extra is null) return null;
        var shown = values.Count > SummaryText.ListLength ? values.Take(SummaryText.ListLength - 1).ToList() : [.. values];
        var rest = values.Skip(shown.Count).ToList();
        var rows = shown.Select(v => (Value: v, Link: (SummaryLink?)new SummaryLink(Filter: new SummaryFilter(v.Field, v.Value)))).ToList();
        if (rest.Count > 0)
            rows.Add((new SummaryValue("", "", $"{SummaryText.Count(rest.Count)} others", rest.Sum(v => v.Count), rest.Sum(v => v.Confirmed)), null));
        if (extra is not null) rows.Add((extra, extraLink));
        var longest = Math.Max(1, rows.Max(r => r.Value.Count));
        return new SummaryList(heading, rest.Count > 0 ? $"{SummaryText.Count(values.Count)} in all" : null,
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
                return new SummaryColumn(level == 1 || level % 5 == 0 ? level.ToString(Culture) : "", (double)count / highest,
                    $"Level {level}: {SummaryText.Plural(count, "book or adventure", "books and adventures")}",
                    new SummaryLink(Filter: new SummaryFilter(Level: level, AdventureCards: true)));
            }),
        ];
    }

    static IReadOnlyList<SummaryColumn> DecadeColumns(IReadOnlyList<SummaryDecade> decades)
    {
        if (decades.Count == 0) return [];
        var highest = Math.Max(1, decades.Max(d => d.Count));
        return [.. decades.Select(d => new SummaryColumn($"{d.Decade}s", (double)d.Count / highest, $"{d.Decade}s: {SummaryText.Plural(d.Count, "book", "books")}", null))];
    }
}
