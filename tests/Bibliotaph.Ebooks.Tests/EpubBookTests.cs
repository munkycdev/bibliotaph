namespace Bibliotaph.Ebooks.Tests;

public sealed class EpubBookTests
{
    [Fact]
    public void The_package_gives_the_title_authors_publisher_date_ISBN_subjects_and_description()
    {
        using var book = new SyntheticEpub
        {
            Title = "Lantern",
            ExtraMetadata = """
                <dc:title id="t2">Tides of the Lantern Coast</dc:title>
                <meta refines="#t2" property="title-type">main</meta>
                <dc:creator>Ada Quill</dc:creator>
                <dc:creator>Bo Marsh</dc:creator>
                <dc:publisher>Gull Rock Games</dc:publisher>
                <dc:date>2019-05-14</dc:date>
                <dc:identifier>urn:isbn:978-0-306-40615-7</dc:identifier>
                <dc:subject>Fantasy</dc:subject>
                <dc:subject>Adventure</dc:subject>
                <dc:description>&lt;p&gt;An adventure for &lt;b&gt;four&lt;/b&gt; characters of 3rd level.&lt;/p&gt;</dc:description>
                """,
        }.Chapter("one.xhtml", "<p>Hello.</p>").Open();

        var m = book.Metadata;
        Assert.Equal("Tides of the Lantern Coast", m.Title);
        Assert.Equal(["Ada Quill", "Bo Marsh"], m.Authors);
        Assert.Equal("Gull Rock Games", m.Publisher);
        Assert.Equal("2019-05-14", m.Date);
        Assert.Equal("en", m.Language);
        Assert.Equal("9780306406157", m.Isbn);
        Assert.Equal(["Fantasy", "Adventure"], m.Subjects);
        Assert.Equal("An adventure for four characters of 3rd level.", m.Description);
        Assert.False(book.IsProtected);
        Assert.False(book.FixedLayout);
    }

    [Theory]
    [InlineData("urn:isbn:9780306406157", "9780306406157")]
    [InlineData("ISBN 0-306-40615-2", "0306406152")]
    [InlineData("978-0-306-40615-8", null)] // wrong check digit
    [InlineData("urn:uuid:12345678-1234-1234-1234-123456789012", null)]
    public void An_ISBN_is_taken_only_when_its_check_digit_holds(string identifier, string? isbn) =>
        Assert.Equal(isbn, EpubBook.IsbnOf(identifier));

    [Fact]
    public void Chapters_are_read_in_spine_order_and_paths_resolve_against_the_package()
    {
        using var book = new SyntheticEpub()
            .Chapter("text/one.xhtml", "<p>One.</p>")
            .Chapter("text/two%20b.xhtml", "<p>Two.</p>")
            .Open();
        Assert.Equal(["OEBPS/text/one.xhtml", "OEBPS/text/two b.xhtml"], book.Spine.Select(s => s.Path));
        Assert.Equal(("OEBPS/images/map.png", "x"), EpubBook.Resolve("OEBPS/text/one.xhtml", "../images/map.png#x"));
        Assert.Equal(("OEBPS/text/one.xhtml", "top"), EpubBook.Resolve("OEBPS/text/one.xhtml", "#top"));
    }

    [Fact]
    public void Text_encrypted_by_DRM_marks_the_book_protected_but_obfuscated_fonts_do_not()
    {
        using (var drm = new SyntheticEpub().Chapter("one.xhtml", "<p>Secret.</p>").Encrypt("OEBPS/one.xhtml").Open())
            Assert.True(drm.IsProtected);

        using var fonts = new SyntheticEpub().Chapter("one.xhtml", "<p>Open.</p>")
            .File("font.otf", [1, 2, 3], "font/otf")
            .Encrypt("OEBPS/font.otf", "http://www.idpf.org/2008/embedding")
            .Open();
        Assert.False(fonts.IsProtected);
        Assert.False(fonts.IsEncrypted("OEBPS/font.otf"));
    }

    [Fact]
    public void The_cover_is_the_marked_image_else_the_EPUB_2_meta_else_the_first_chapters_first_image()
    {
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G'];
        using (var marked = new SyntheticEpub().Chapter("one.xhtml", "<p>x</p>").File("img/cover.png", png, "image/png", "cover-image").Open())
            Assert.Equal("OEBPS/img/cover.png", marked.CoverPath);

        using (var meta = new SyntheticEpub { ExtraMetadata = """<meta name="cover" content="f0"/>""" }
                   .Chapter("one.xhtml", "<p>x</p>").File("img/front.jpg", png, "image/jpeg").Open())
            Assert.Equal("OEBPS/img/front.jpg", meta.CoverPath);

        using var first = new SyntheticEpub().Chapter("cover.xhtml", """<div><img src="img/front.jpg" alt=""/></div>""")
            .File("img/front.jpg", png, "image/jpeg").Open();
        Assert.Equal("OEBPS/img/front.jpg", first.CoverPath);
        Assert.Equal(png, first.ReadCover());
    }

    [Fact]
    public void A_file_that_isnt_an_EPUB_or_is_too_large_is_refused_with_a_reason()
    {
        Assert.Throws<EbookException>(() => EpubBook.Open(new MemoryStream([1, 2, 3, 4])));
        Assert.Throws<EbookException>(() => new SyntheticEpub { WriteContainer = false }.Chapter("one.xhtml", "<p>x</p>").Open());

        var big = new SyntheticEpub().Chapter("one.xhtml", "<p>" + new string('a', 200_000) + "</p>");
        var error = Assert.Throws<EbookException>(() =>
        {
            using var book = big.Open(new EbookLimits { EntryBytes = 100_000 });
            EbookPaging.Cut(book);
        });
        Assert.Contains("larger than Bibliotaph reads", error.Message, StringComparison.Ordinal);
    }
}
