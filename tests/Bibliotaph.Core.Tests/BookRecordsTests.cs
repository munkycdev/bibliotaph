using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Core.Tests;

public class BookRecordsTests
{
    static BookRecord Record(string title, string? published = "March 2019") =>
        new("Open Library", "OL1M", "9781234567897", title, ["A. Example", "B. Example"], ["Example Press", "Sample Games"], published);

    [Theory]
    [InlineData("The Owlbear Mill", true)]
    [InlineData("The Owlbear Mill: A Tale of Eggs", true)] // the subtitle needn't be printed
    [InlineData("THE OWLBEAR MILL", true)]
    [InlineData("The Owlbear's Mill", false)]
    [InlineData("The Goblin Warrens", false)]
    public void A_record_is_used_only_when_its_title_is_printed_in_the_book(string title, bool used)
    {
        string?[] places = [null, "", "Writing: A. Example\nTHE   OWLBEAR — MILL\nAn adventure for four players", "owlbear_mill_v2.pdf"];

        Assert.Equal(used, BookRecords.IsNamedIn(Record(title), places));
    }

    [Fact]
    public void Accents_and_punctuation_dont_stop_a_title_matching() =>
        Assert.True(BookRecords.IsNamedIn(Record("Café Tales, Volume 2"), ["CAFE TALES VOLUME 2"]));

    [Fact]
    public void Broken_characters_in_a_books_text_are_passed_over() =>
        Assert.True(BookRecords.IsNamedIn(Record("The Owlbear Mill"), ["\uD83D The Owlbear Mill \uDC00\uFFFE"]));

    [Fact]
    public void A_record_suggests_its_title_authors_first_publisher_and_year()
    {
        var proposals = BookRecords.Proposals(Record("The Owlbear Mill"));

        Assert.Equal(
            [("title", "The Owlbear Mill"), ("authors", "A. Example"), ("authors", "B. Example"), ("publisher", "Example Press"), ("year", "2019")],
            proposals.Select(p => (p.Field.Key, p.Value)));
        Assert.All(proposals, p => Assert.Equal((AssertionOrigin.Lookup, "Open Library's record for ISBN 9781234567897"), (p.Origin, p.Quote)));
    }

    [Theory]
    [InlineData("1985", "1985")]
    [InlineData("c1985", "1985")]
    [InlineData("March 3, 2019", "2019")]
    [InlineData("n.d.", null)]
    [InlineData(null, null)]
    public void The_year_is_read_from_the_catalogues_date(string? published, string? year) =>
        Assert.Equal(year, BookRecords.Year(published));
}
