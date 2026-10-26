using System.Globalization;
using Bibliotaph.Core.Reading;

namespace Bibliotaph.Core.Tests;

public class PrintedPagesTests
{
    [Fact]
    public void Labels_map_printed_numbers_to_their_pdf_pages()
    {
        string?[] labels = ["Cover", "i", "ii", "1", "2", "3", null, "5"];

        var printed = PrintedPages.FromLabels(labels, labels.Length);

        Assert.Equal(PrintedPagesSource.Labels, printed.Source);
        Assert.Equal(3, printed.IndexOf(1));
        Assert.Equal(5, printed.IndexOf(3));
        Assert.Equal(7, printed.IndexOf(5));
        Assert.Null(printed.IndexOf(4));   // the unlabelled page is not assumed to be page 4
        Assert.Null(printed.IndexOf(9));
    }

    [Fact]
    public void A_number_two_pages_share_is_ambiguous_and_finds_nothing()
    {
        string?[] labels = ["1", "2", "3", "1", "2", "4"];

        var printed = PrintedPages.FromLabels(labels, labels.Length);

        Assert.Null(printed.IndexOf(1));
        Assert.Null(printed.IndexOf(2));
        Assert.Equal(2, printed.IndexOf(3));
        Assert.Equal(5, printed.IndexOf(4));
    }

    [Fact]
    public void Labels_that_are_not_plain_numbers_say_nothing()
    {
        Assert.False(PrintedPages.FromLabels(["Cover", "i", "ii", "A-1", "01"], 5).IsKnown);
        Assert.False(PrintedPages.FromLabels([null, null], 2).IsKnown);
        Assert.False(PrintedPages.FromLabels([], 10).IsKnown);
        Assert.Null(PrintedPages.None.IndexOf(1));
        Assert.Equal(PrintedPagesSource.None, PrintedPages.None.Source);
    }

    [Fact]
    public void Labels_past_the_page_count_are_ignored()
    {
        var printed = PrintedPages.FromLabels(["1", "2", "3"], 2);

        Assert.Equal(1, printed.IndexOf(2));
        Assert.Null(printed.IndexOf(3));
    }

    [Fact]
    public void Folios_at_the_foot_of_the_pages_give_the_offset()
    {
        // Four unnumbered front pages, then printed page 1 on PDF page 4.
        var pages = Book(30, page => page < 4 ? null : (page - 3).ToString(CultureInfo.InvariantCulture), top: false);

        var printed = PrintedPages.FromFolios(pages, 30);

        Assert.Equal(PrintedPagesSource.Folios, printed.Source);
        Assert.Equal(4, printed.IndexOf(1));
        Assert.Equal(13, printed.IndexOf(10));
        Assert.Equal(29, printed.IndexOf(26));
        Assert.Null(printed.IndexOf(27));   // past the end of the PDF
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("Page {0}")]
    [InlineData("p. {0}")]
    [InlineData("{0} | The Sunken Valley")]
    [InlineData("The Sunken Valley | {0}")]
    [InlineData("The Sunken Valley • {0}")]
    public void Folios_are_read_in_the_usual_shapes_at_the_top_or_foot(string shape)
    {
        var pages = Book(20, page => page < 2 ? null : string.Format(CultureInfo.InvariantCulture, shape, page - 1), top: true);

        var printed = PrintedPages.FromFolios(pages, 20);

        Assert.Equal(7, printed.IndexOf(6));
    }

    [Fact]
    public void A_table_of_every_lookup_finds_the_same_pages_again()
    {
        var folios = PrintedPages.FromFolios(Book(30, page => page < 4 ? null : (page - 3).ToString(CultureInfo.InvariantCulture), top: false), 30);
        var labels = PrintedPages.FromLabels(["Cover", "i", "1", "2", "1", "4"], 6);

        foreach (var printed in new[] { folios, labels })
        {
            var table = printed.ToTable();
            var again = PrintedPages.FromTable(printed.Source, table);
            Assert.Equal(printed.Source, again.Source);
            for (var number = 1; number <= table.Count + 5; number++) Assert.Equal(printed.IndexOf(number), again.IndexOf(number));
        }
        Assert.Equal(26, folios.ToTable().Count);
        Assert.Equal([-1, 3, -1, 5], labels.ToTable());
        Assert.Empty(PrintedPages.None.ToTable());
        Assert.False(PrintedPages.FromTable(PrintedPagesSource.Labels, [-1, -1]).IsKnown);
    }

    [Fact]
    public void Too_few_pages_agreeing_says_nothing()
    {
        // Two numbered pages are not enough to trust.
        var two = Book(20, page => page is 5 or 6 ? (page - 2).ToString(CultureInfo.InvariantCulture) : null, top: false);
        Assert.False(PrintedPages.FromFolios(two, 20).IsKnown);

        // Numbers at the foot that don't agree with each other (a table's last row, say) are not folios.
        var scattered = Book(20, page => ((page * 7 % 13) + 1).ToString(CultureInfo.InvariantCulture), top: false);
        Assert.False(PrintedPages.FromFolios(scattered, 20).IsKnown);

        Assert.False(PrintedPages.FromFolios([], 20).IsKnown);
    }

    [Fact]
    public void A_page_showing_the_number_wins_over_the_usual_offset()
    {
        // Printed pages 1-16 on PDF pages 4-19, two unnumbered plates, then printed 17 on PDF page 22.
        var pages = Book(40, page => page switch
        {
            < 4 or 20 or 21 => null,
            < 20 => (page - 3).ToString(CultureInfo.InvariantCulture),
            _ => (page - 5).ToString(CultureInfo.InvariantCulture),
        }, top: false);

        var printed = PrintedPages.FromFolios(pages, 40);

        Assert.Equal(13, printed.IndexOf(10));   // before the plates, where page 10 is printed
        Assert.Equal(19, printed.IndexOf(16));
        Assert.Equal(22, printed.IndexOf(17));
        Assert.Equal(39, printed.IndexOf(34));
    }

    [Fact]
    public void Numbers_inside_the_page_are_not_folios()
    {
        // The middle lines end in numbers; only the first two and last two lines are looked at.
        var pages = Enumerable.Range(0, 12).Select(page =>
            (page, $"Running head\nThe tower has 7\nfloors and 12\ndoors.\nA quiet line\n{page + 10}")).ToList();

        var printed = PrintedPages.FromFolios(pages, 12);

        Assert.Equal(0, printed.IndexOf(10));
        Assert.Equal(11, printed.IndexOf(21));
    }

    /// <summary>Made-up pages of a few lines each, with a folio as the first or last line where one is given.</summary>
    static List<(int, string)> Book(int count, Func<int, string?> folio, bool top) =>
    [
        .. Enumerable.Range(0, count).Select(page =>
        {
            var body = $"A made-up page about the river crossing.\nMore words on page {page} of the invented book.";
            return (page, folio(page) is { } f ? (top ? $"{f}\n{body}" : $"{body}\n{f}") : body);
        }),
    ];
}
