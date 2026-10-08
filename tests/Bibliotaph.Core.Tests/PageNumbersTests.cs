using Bibliotaph.Core.Reading;

namespace Bibliotaph.Core.Tests;

public class PageNumbersTests
{
    static readonly string?[] Labels = ["Cover", "i", "ii", "1", "2", "3", null];

    [Theory]
    [InlineData("ii", 2)]
    [InlineData("II", 2)]
    [InlineData(" cover ", 0)]
    [InlineData("2", 4)]   // the printed page 2, not PDF page 2
    [InlineData("7", 6)]   // no page is labelled 7, so PDF page 7
    [InlineData("8", null)]
    [InlineData("0", null)]
    [InlineData("xiv", null)]
    [InlineData("", null)]
    public void Finds_printed_labels_before_pdf_page_numbers(string entry, int? expected) =>
        Assert.Equal(expected, PageNumbers.Find(entry, Labels, Labels.Length));

    [Fact]
    public void Shows_the_label_or_the_pdf_page_number()
    {
        Assert.Equal("ii", PageNumbers.Display(2, Labels));
        Assert.Equal("7", PageNumbers.Display(6, Labels));
        Assert.Equal("9", PageNumbers.Display(8, Labels));
    }
}
