namespace Bibliotaph.Core.Tests;

public class BookIdentifiersTests
{
    const string Story = "The owlbear waits beneath the old mill, guarding a clutch of eggs.";

    /// <summary>A book of <paramref name="count"/> pages of story, with the pages given put in place.</summary>
    static string?[] Book(int count, params (int Page, string Text)[] pages)
    {
        var book = Enumerable.Repeat<string?>(Story, count).ToArray();
        foreach (var (page, text) in pages) book[page] = text;
        return book;
    }

    static IEnumerable<(IdentifierKind, string, int?)> Found(string?[] pages, string? package = null) =>
        BookIdentifiers.Find(pages, package).Select(i => (i.Kind, i.Value, i.PdfPage));

    [Theory]
    [InlineData("978-1-23-456789-7", "9781234567897")]
    [InlineData("978 1 23 456789 7", "9781234567897")]
    [InlineData("9781234567897", "9781234567897")]
    [InlineData("123456789X", "9781234567897")]
    [InlineData("0-555-55555-0", "9780555555552")]
    [InlineData("978-1-23-456789-8", null)]
    [InlineData("123456789", null)]
    [InlineData("9771234567898", null)]
    public void An_isbn_is_read_by_its_check_digit_and_kept_as_13_digits(string written, string? expected) =>
        Assert.Equal(expected, Isbn.Normalize(written));

    [Fact]
    public void A_labelled_isbn_on_the_credits_page_is_found_as_printed()
    {
        var found = Assert.Single(BookIdentifiers.Find(Book(40, (2, "Writing: A. Example\nISBN: 978-1-23-456789-7\nPrinted in 2026"))));

        Assert.Equal(new BookIdentifier(IdentifierKind.Isbn, "9781234567897", 2, "978-1-23-456789-7"), found);
    }

    [Fact]
    public void An_isbn_10_that_runs_into_a_year_is_still_found()
    {
        var found = Assert.Single(BookIdentifiers.Find(Book(20, (1, "ISBN 0-555-55555-0 2019 Example Press"))));

        Assert.Equal(("9780555555552", "0-555-55555-0"), (found.Value, found.Printed));
    }

    [Fact]
    public void A_bare_number_counts_only_on_a_copyright_page_or_the_back_cover()
    {
        Assert.Equal([(IdentifierKind.Isbn, "9781111111113", 3)], Found(Book(30, (3, "© 2026 Example Press. All rights reserved.\n9781111111113"))));
        Assert.Equal([(IdentifierKind.Isbn, "9782222222224", 29)], Found(Book(30, (29, "9 782222 222224\n$29.99"))));
        Assert.Empty(Found(Book(30, (4, "The vault code is 9783333333335, written on the wall."))));
    }

    [Fact]
    public void Only_the_front_and_back_pages_are_read()
    {
        Assert.Empty(Found(Book(40, (15, "ISBN 978-1-23-456789-7"))));
        // Blank pages before the title page don't use up the front pages.
        var blankFirst = Book(40, (9, "ISBN 978-1-23-456789-7"));
        for (var i = 0; i < 5; i++) blankFirst[i] = "";
        Assert.Single(Found(blankFirst));
    }

    [Fact]
    public void A_wrong_check_digit_is_not_an_isbn() =>
        Assert.Empty(Found(Book(10, (1, "ISBN 978-1-23-456789-8"))));

    [Fact]
    public void A_catalogue_of_other_books_keeps_only_the_isbns_labelled_on_the_copyright_page()
    {
        var catalogue = string.Join('\n', new[] { "9781111111113", "9782222222224", "9783333333335", "9784444444446", "9785555555557" }.Select(i => "ISBN " + i));
        var pages = Book(30, (2, "© 2026 Example Press\nISBN 978-9-99999-999-1 (print)"), (28, catalogue));

        Assert.Equal([(IdentifierKind.Isbn, "9789999999991", 2)], Found(pages));
    }

    [Fact]
    public void A_book_lists_each_identifier_once_where_it_first_appears()
    {
        var pages = Book(30, (2, "ISBN 978-1-23-456789-7"), (29, "ISBN 9781234567897"));

        Assert.Equal([(IdentifierKind.Isbn, "9781234567897", 2)], Found(pages));
    }

    [Fact]
    public void Product_codes_are_found_by_their_label_or_a_known_prefix()
    {
        var pages = Book(30, (1, "Product code: ABC-1234"), (2, "PZO9999 · Example Press"), (29, "TSR 9123"));

        Assert.Equal(
            [(IdentifierKind.ProductCode, "ABC1234", 1), (IdentifierKind.ProductCode, "PZO9999", 2), (IdentifierKind.ProductCode, "TSR9123", 29)],
            Found(pages));
    }

    [Fact]
    public void A_year_beside_a_publishers_name_is_not_a_product_code() =>
        Assert.Empty(Found(Book(10, (1, "Copyright TSR 1987, all rights reserved."))));

    [Fact]
    public void An_ebooks_package_isbn_comes_first_with_no_page()
    {
        var found = BookIdentifiers.Find(Book(10, (1, "ISBN 978-1-11-111111-3")), packageIsbn: "urn:isbn:978-0-555-55555-2");

        Assert.Equal([(IdentifierKind.Isbn, "9780555555552", (int?)null), (IdentifierKind.Isbn, "9781111111113", 1)], found.Select(i => (i.Kind, i.Value, i.PdfPage)));
    }
}
