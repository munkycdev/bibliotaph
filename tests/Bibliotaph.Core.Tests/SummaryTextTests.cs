namespace Bibliotaph.Core.Tests;

/// <summary>The Summary page's words, which both clients show as they are.</summary>
public sealed class SummaryTextTests
{
    [Theory]
    [InlineData(512, "512 bytes")]
    [InlineData(740_000_000, "740 MB")]
    [InlineData(4_250_000_000, "4.3 GB")]
    [InlineData(96_400_000_000, "96 GB")]
    [InlineData(1_520_000_000_000, "1.5 TB")]
    public void Sizes_are_in_whole_decimal_units(long bytes, string size) => Assert.Equal(size, SummaryText.Size(bytes));

    [Theory]
    [InlineData(600, null)]
    [InlineData(4_400, "about three days")]
    [InlineData(212_540, "about five months")]
    [InlineData(2_000_000, "about four years")]
    public void Reading_time_is_said_in_the_largest_unit_that_fits(long pages, string? span)
    {
        var line = SummaryText.ReadingTime(pages);
        if (span is null) Assert.Null(line);
        else Assert.Contains(span, line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_backlog_says_what_is_left()
    {
        Assert.Equal(("You've opened 412 of 1,284.", "872 are still waiting their turn."), SummaryText.Backlog(new LibrarySummary { Books = 1284, Opened = 412 }));
        Assert.Equal("Every one of them. Impressive.", SummaryText.Backlog(new LibrarySummary { Books = 3, Opened = 3 }).Waiting);
        Assert.Equal("You haven't opened a book here yet.", SummaryText.Backlog(new LibrarySummary { Books = 3 }).Opened);
    }

    [Fact]
    public void Store_books_say_how_many_are_still_to_download()
    {
        Assert.Null(SummaryText.Stores(new LibrarySummary()));
        Assert.Equal("1 book bought from stores is still waiting to be downloaded.", SummaryText.Stores(new LibrarySummary { StoreWaiting = 1 }));
        Assert.Equal("503 books bought from stores are on your drive; 18 are still waiting to be downloaded.",
            SummaryText.Stores(new LibrarySummary { StoreDownloaded = 503, StoreWaiting = 18 }));
    }

    [Fact]
    public void Offline_folders_keep_their_books_searchable()
    {
        Assert.Null(SummaryText.Offline([new SummaryFolder(1, "a", 10, 0, false)]));
        Assert.Equal("One folder can't be reached right now: its 74 books stay searchable and open again when it's back.",
            SummaryText.Offline([new SummaryFolder(1, "a", 10, 0, false), new SummaryFolder(2, "b", 74, 0, true)]));
    }
}
