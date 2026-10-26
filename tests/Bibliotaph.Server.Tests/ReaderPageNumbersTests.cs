using Bibliotaph.Api.Contracts;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Server.Tests;

/// <summary>The web reader's page box reads page numbers as the Windows reader's does.</summary>
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
}
