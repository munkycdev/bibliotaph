using Bibliotaph.Api.Contracts;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Server.Tests;

/// <summary>The web reader's page box reads page numbers.</summary>
public sealed class ReaderPageNumbersTests
{
    static readonly IReadOnlyList<string?> Labels = ["Cover", "i", "ii", "1", "2", null, "4", " xii "];

    [Theory]
    [InlineData("ii")]
    [InlineData("II")]
    [InlineData("2")]
    [InlineData("4")]
    [InlineData("xii")]
    [InlineData("6")]
    [InlineData("8")]
    [InlineData("9")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("cover")]
    [InlineData("nowhere")]
    public void A_typed_page_goes_where_the_windows_reader_goes(string typed) =>
        Assert.Equal(PageNumbers.Find(typed, Labels, Labels.Count), ReaderPageNumbers.Find(typed, Labels, Labels.Count));

    [Fact]
    public void A_page_shows_as_the_windows_reader_shows_it()
    {
        for (var page = 0; page < Labels.Count + 2; page++)
            Assert.Equal(PageNumbers.Display(page, Labels), ReaderPageNumbers.Display(page, Labels));
    }

    [Theory]
    [InlineData("ii", "", 2, 2)]
    [InlineData("1", "4", 3, 6)]
    [InlineData("4", "1", 3, 6)]
    [InlineData("2", " ", 4, 4)]
    public void From_and_to_name_their_pages_in_order(string from, string to, int first, int last) =>
        Assert.Equal((first, last), ReaderPageNumbers.Range(from, to, Labels, Labels.Count));

    [Theory]
    [InlineData("nowhere", "")]
    [InlineData("1", "nowhere")]
    [InlineData("", "4")]
    public void A_range_with_a_page_the_book_lacks_is_none(string from, string to) =>
        Assert.Null(ReaderPageNumbers.Range(from, to, Labels, Labels.Count));
}
