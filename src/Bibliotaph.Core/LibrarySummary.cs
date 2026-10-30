using System.Globalization;

namespace Bibliotaph.Core;

/// <summary>
/// The Summary page's figures: how much the library holds, where it came from, what it is and what has been done with
/// it. Counts are of the cards the Library shows (files, packs and books owned elsewhere), not of the adventures marked
/// inside files, unless a figure says otherwise. Computed once and shown by both clients, so the PC and the web never
/// disagree.
/// </summary>
public sealed record LibrarySummary
{
    /// <summary>Every card the Library shows while browsing: files, packs and books owned elsewhere.</summary>
    public long Books { get; init; }

    /// <summary>The cards of single files: PDFs, e-books and pictures.</summary>
    public long Files { get; init; }

    public long Packs { get; init; }

    /// <summary>Adventures marked inside files, each a card of its own when searched for.</summary>
    public long Adventures { get; init; }

    /// <summary>Books owned in print, on a virtual tabletop or online, with no file.</summary>
    public long Elsewhere { get; init; }

    public long Ebooks { get; init; }

    /// <summary>Files found again under another name or in another folder: the copies beyond each book's first.</summary>
    public long Duplicates { get; init; }

    /// <summary>The pages of every book with a file, each book counted once whatever its copies.</summary>
    public long Pages { get; init; }

    /// <summary>The size of every file in the library's folders that is still there; a ZIP counts once, not its members.</summary>
    public long Bytes { get; init; }

    public IReadOnlyList<SummaryFolder> Folders { get; init; } = [];

    /// <summary>Where else books are owned and where they came from (the Also own and Get it from fields), most first.</summary>
    public IReadOnlyList<SummaryValue> Sources { get; init; } = [];

    /// <summary>Books from a store in Get it from whose file is in the library.</summary>
    public long StoreDownloaded { get; init; }

    /// <summary>Books from a store in Get it from that are still only on the store.</summary>
    public long StoreWaiting { get; init; }

    public IReadOnlyList<SummaryValue> Systems { get; init; } = [];

    /// <summary>Books with no game system yet.</summary>
    public long SystemUnknown { get; init; }

    public IReadOnlyList<SummaryValue> Types { get; init; } = [];

    public IReadOnlyList<SummaryValue> Settings { get; init; } = [];

    /// <summary>How many books and adventures inside files cover each character level, from 1 at index 0 to <see cref="SummaryText.TopLevel"/>.</summary>
    public IReadOnlyList<long> Levels { get; init; } = [];

    /// <summary>Adventures whose levels nobody knows yet.</summary>
    public long LevelsUnknown { get; init; }

    /// <summary>Books by the decade they were published in, oldest first, with every decade between the first and last.</summary>
    public IReadOnlyList<SummaryDecade> Decades { get; init; } = [];

    public int? OldestYear { get; init; }

    public int? NewestYear { get; init; }

    public long YearUnknown { get; init; }

    /// <summary>Books opened at least once.</summary>
    public long Opened { get; init; }

    public long Favorites { get; init; }

    public long Collections { get; init; }

    public long Binders { get; init; }

    /// <summary>The books and page ranges in all binders.</summary>
    public long BinderItems { get; init; }

    /// <summary>Notes on books and their pages.</summary>
    public long Notes { get; init; }

    /// <summary>The books with at least one note.</summary>
    public long NotedBooks { get; init; }

    public long SavedSearches { get; init; }

    /// <summary>Books an AI has read.</summary>
    public long ReadByAi { get; init; }

    /// <summary>Books with a file whose text has been read, so they are searchable.</summary>
    public long TextRead { get; init; }

    /// <summary>Pages whose text came from recognising a scan.</summary>
    public long OcrPages { get; init; }

    /// <summary>Books in the library at the end of each month, from the month the first one came to this month.</summary>
    public IReadOnlyList<SummaryMonth> Growth { get; init; } = [];

    /// <summary>The day the most books came at once, when more than one did.</summary>
    public SummaryDay? BiggestDay { get; init; }
}

/// <summary>
/// One value of a vocabulary field with how many books have it, and how many of those the user has confirmed; the rest
/// are suggestions. <see cref="Field"/> is the field's key (system, type, setting, own, store), which a client turns
/// into the Library's filter for it.
/// </summary>
public sealed record SummaryValue(string Field, string Value, string Label, long Count, long Confirmed);

/// <summary>A library folder: <see cref="Path"/> as it is kept, which each client names its own way.</summary>
public sealed record SummaryFolder(long Id, string Path, long Books, long Bytes, bool Offline);

/// <summary>Books published in the ten years from <see cref="Decade"/>, such as 1980.</summary>
public sealed record SummaryDecade(int Decade, long Count);

/// <summary>How many books the library held by the end of <see cref="Month"/> (its first day).</summary>
public sealed record SummaryMonth(DateOnly Month, long Total);

public sealed record SummaryDay(DateOnly Day, long Added);

/// <summary>The Summary page's words, worded once for the Windows app and the web.</summary>
public static class SummaryText
{
    /// <summary>The highest character level the level strip shows, as the Library's level filter goes no higher.</summary>
    public const int TopLevel = 20;

    /// <summary>How many values each list shows before the rest are folded into Other.</summary>
    public const int ListLength = 7;

    public const string Title = "Your library, by the numbers.";

    public const string Subtitle = "Every figure here opens the Library at that part of it.";

    /// <summary>Home's line that leads to the Summary page: "1,284 books, 96 GB."</summary>
    public static string Totals(long books, long bytes) => $"{Plural(books, "book", "books")}, {Size(bytes)}.";

    public const string SeeIt = "See your library in numbers";

    public const string Empty = "Add a folder, or a book you own elsewhere, and this page fills in as Bibliotaph reads them.";

    static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static string Count(long count) => count.ToString("N0", Culture);

    public static string Plural(long count, string one, string many) => $"{Count(count)} {(count == 1 ? one : many)}";

    /// <summary>"96 GB", "740 MB": a size in whole units, decimal as Windows Explorer shows file sizes in its status bar.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => string.Create(Culture, $"{bytes / 1e12:0.#} TB"),
        >= 10_000_000_000 => string.Create(Culture, $"{bytes / 1e9:0} GB"),
        >= 1_000_000_000 => string.Create(Culture, $"{bytes / 1e9:0.#} GB"),
        >= 1_000_000 => string.Create(Culture, $"{bytes / 1e6:0} MB"),
        >= 1_000 => string.Create(Culture, $"{bytes / 1e3:0} KB"),
        _ => string.Create(Culture, $"{bytes} bytes"),
    };

    /// <summary>The figures the hero line is made of, so each client can make each one a link.</summary>
    public static (string Books, string Pages, string Size) Hero(LibrarySummary summary) =>
        (Plural(summary.Books, "book", "books"), Plural(summary.Pages, "page", "pages"), Size(summary.Bytes));

    /// <summary>
    /// How long reading every page would take at a page a minute, round the clock: "about five months". Null for a
    /// library too small for it to be worth saying.
    /// </summary>
    public static string? ReadingTime(long pages)
    {
        var days = pages / (60.0 * 24);
        if (days < 1) return null;
        var span = days switch
        {
            < 14 => Words(days, "day", "days"),
            < 60 => Words(days / 7, "week", "weeks"),
            < 730 => Words(days / 30.4, "month", "months"),
            _ => Words(days / 365.25, "year", "years"),
        };
        return $"At a page a minute, that's about {span} of reading, without stopping to sleep.";
    }

    static string Words(double amount, string one, string many)
    {
        var whole = Math.Max(1, (int)Math.Round(amount));
        string[] names = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];
        return $"{(whole < names.Length ? names[whole] : whole.ToString(Culture))} {(whole == 1 ? one : many)}";
    }

    /// <summary>The line under the folders, when one can't be reached.</summary>
    public static string? Offline(IReadOnlyList<SummaryFolder> folders)
    {
        var away = folders.Where(f => f.Offline).ToList();
        if (away.Count == 0) return null;
        var books = away.Sum(f => f.Books);
        return away.Count == 1
            ? $"One folder can't be reached right now: its {Plural(books, "book stays", "books stay")} searchable and open again when it's back."
            : $"{Count(away.Count)} folders can't be reached right now: their {Plural(books, "book stays", "books stay")} searchable and open again when they're back.";
    }

    /// <summary>The line under where books came from, about the books bought from stores.</summary>
    public static string? Stores(LibrarySummary summary) => (summary.StoreDownloaded, summary.StoreWaiting) switch
    {
        (0, 0) => null,
        (var here, 0) => $"All {Plural(here, "book", "books")} you bought from stores {(here == 1 ? "is" : "are")} on your drive.",
        (0, var waiting) => $"{Plural(waiting, "book", "books")} bought from stores {(waiting == 1 ? "is" : "are")} still waiting to be downloaded.",
        var (here, waiting) => $"{Plural(here, "book", "books")} bought from stores {(here == 1 ? "is" : "are")} on your drive; {Count(waiting)} {(waiting == 1 ? "is" : "are")} still waiting to be downloaded.",
    };

    /// <summary>The backlog, said plainly: "You've opened 412 of 1,284." and what is left.</summary>
    public static (string Opened, string Waiting) Backlog(LibrarySummary summary)
    {
        var waiting = Math.Max(0, summary.Books - summary.Opened);
        var opened = summary.Opened == 0 ? "You haven't opened a book here yet." : $"You've opened {Count(summary.Opened)} of {Count(summary.Books)}.";
        var rest = waiting switch
        {
            0 => "Every one of them. Impressive.",
            1 => "One is still waiting its turn.",
            _ => $"{Count(waiting)} are still waiting their turn.",
        };
        return (opened, rest);
    }

    /// <summary>The line under the level strip: the strongest level, and how thin the top end is.</summary>
    public static string? Levels(LibrarySummary summary)
    {
        if (summary.Levels.Count == 0 || summary.Levels.All(l => l == 0)) return null;
        var best = summary.Levels.Max();
        var level = summary.Levels.ToList().IndexOf(best) + 1;
        var high = summary.Levels.Count > 15 ? summary.Levels.Skip(15).Max() : 0;
        var top = $"Level {level} is your strongest shelf: {Plural(best, "book", "books")}.";
        return high < best / 4 ? $"{top} From level 16 up you have {Count(high)}." : top;
    }

    /// <summary>The line under the decades: the oldest and newest years.</summary>
    public static string? Years(LibrarySummary summary) => (summary.OldestYear, summary.NewestYear) switch
    {
        ({ } oldest, { } newest) when oldest != newest =>
            $"The oldest was published in {oldest}, the newest in {newest}: {newest - oldest} years of other people's homework.",
        ({ } year, _) => $"Everything with a year was published in {year}.",
        _ => null,
    };

    /// <summary>The line under the growth chart: the biggest day.</summary>
    public static string? BiggestDay(SummaryDay? day) => day is null
        ? null
        : $"Biggest day: {day.Day.ToString("d MMMM yyyy", Culture)}, when {Count(day.Added)} books arrived at once. A bundle, presumably.";

    /// <summary>"Text read for 1,190 of 1,226 files so far." with the scanned pages, or that everything is read.</summary>
    public static string Reading(LibrarySummary summary)
    {
        var scanned = summary.OcrPages > 0 ? $" {Plural(summary.OcrPages, "scanned page was", "scanned pages were")} recognised." : "";
        return summary.TextRead >= summary.Files
            ? $"Every file's text has been read.{scanned}"
            : $"Text read for {Count(summary.TextRead)} of {Plural(summary.Files, "file", "files")} so far.{scanned} The rest is still being read.";
    }
}
