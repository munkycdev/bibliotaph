using System.Buffers.Binary;
using System.Text;

namespace Bibliotaph.Ebooks.Tests;

public sealed class MobiBookTests
{
    public static TheoryData<string> Books => ["sample-old.mobi", "sample-both.mobi", "sample.azw3"];

    static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ebooks", name));

    static IEbook Open(string name) => Ebook.Open(new MemoryStream(Bytes(name)));

    [Theory]
    [InlineData("sample-old.mobi", false)]
    [InlineData("sample-both.mobi", true)]
    [InlineData("sample.azw3", true)]
    public void A_book_with_a_KF8_version_is_read_from_it(string name, bool kf8)
    {
        using var book = Open(name);

        Assert.Equal(kf8, Assert.IsType<MobiBook>(book).IsKf8);
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void The_header_gives_the_title_authors_publisher_date_ISBN_subjects_and_description(string name)
    {
        using var book = Open(name);

        var m = book.Metadata;
        Assert.Equal("The Sample Road", m.Title);
        Assert.Equal(["Ann Writer", "Bo Second"], m.Authors);
        Assert.Equal("Sample Press", m.Publisher);
        Assert.Equal("2019-05-14", m.Date);
        Assert.Equal("en", m.Language);
        Assert.Equal("9780306406157", m.Isbn);
        Assert.Equal(["Fantasy"], m.Subjects);
        Assert.Equal("A short walk for testing, levels 1-3.", m.Description);
        Assert.False(book.IsProtected);
        Assert.False(book.FixedLayout);
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void The_text_is_rebuilt_into_parts_and_cut_into_pages(string name)
    {
        using var book = Open(name);

        var layout = EbookPaging.Cut(book);
        var text = string.Join(" ", layout.Pages.Select(p => p.Text));
        Assert.Contains("The travellers reached the village at dusk", text, StringComparison.Ordinal);
        Assert.Contains("Mile 24 of the road", text, StringComparison.Ordinal);
        Assert.Contains("Caf\u00e9 \u2014 na\u00efve \u201cquotes\u201d.", text, StringComparison.Ordinal);
        // A part for each chapter and Calibre's contents page at the end; the long chapter takes two pages.
        Assert.Equal(4, book.Spine.Count);
        Assert.Equal(5, layout.Pages.Count);
    }

    [Theory]
    [InlineData("sample-old.mobi", false)]
    [InlineData("sample-both.mobi", true)]
    [InlineData("sample.azw3", true)]
    public void The_contents_come_from_the_index_in_order_and_land_on_their_pages(string name, bool kf8)
    {
        using var book = Open(name);
        var layout = EbookPaging.Cut(book);

        Assert.Equal(["Arrival", "The Long Road", "The Crossing", "The Lighthouse"], book.Contents.Select(c => c.Title));
        // The older format's index, as Calibre writes it, is flat.
        Assert.Equal(kf8 ? [0, 0, 1, 0] : [0, 0, 0, 0], book.Contents.Select(c => c.Depth));
        Assert.All(book.Contents, c => Assert.True(book.Contains(c.Path), $"{c.Title} points at {c.Path}, which isn't in the book."));
        var pages = layout.Outline.ToDictionary(o => o.Title, o => o.PageIndex);
        Assert.Equal(0, pages["Arrival"]);
        Assert.Contains("The Crossing", layout.Pages[pages["The Crossing"]].Text, StringComparison.Ordinal);
        Assert.Contains("lantern", layout.Pages[pages["The Lighthouse"]].Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void Links_and_pictures_become_paths_in_the_book(string name)
    {
        using var book = Open(name);

        var first = book.ReadText(book.Spine[0].Path);
        Assert.DoesNotContain("filepos", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("recindex", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kindle:", first, StringComparison.Ordinal);
        var image = HtmlText.FirstImage(first);
        Assert.NotNull(image);
        Assert.StartsWith("image/", book.MediaTypeOf(image!));
        Assert.NotEmpty(book.ReadBytes(image!));

        // The link to the lantern goes to its paragraph in the last chapter.
        var link = System.Text.RegularExpressions.Regex.Match(first, "href=\"([^\"]+)\"[^>]*>the lantern").Groups[1].Value;
        var (path, fragment) = EpubBook.Resolve(book.Spine[0].Path, link);
        var lighthouse = book.Spine.Single(s => book.ReadText(s.Path).Contains("At the end of the road", StringComparison.Ordinal)).Path;
        Assert.Equal(lighthouse, path);
        var last = book.ReadText(path);
        var anchor = last.IndexOf($"id=\"{fragment}\"", StringComparison.Ordinal);
        Assert.True(anchor >= 0, $"No anchor {fragment} in {path}.");
        Assert.InRange(last.IndexOf("At the end of the road", StringComparison.Ordinal) - anchor, 0, 80);
        Assert.Contains("https://example.com/", last, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void The_cover_is_the_image_the_header_names(string name)
    {
        using var book = Open(name);

        Assert.NotNull(book.CoverPath);
        var cover = book.ReadCover()!;
        // The sample's cover is a red picture, larger than its map; Calibre may keep it as a PNG or make it a JPEG.
        Assert.True(cover.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P']) || cover.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]));
        Assert.NotEqual(HtmlText.FirstImage(book.ReadText(book.Spine[0].Path)), book.CoverPath);
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void The_reader_page_shows_its_pictures_and_follows_its_links(string name)
    {
        using var book = Open(name);
        var layout = EbookPaging.Cut(book);

        var page = EbookReaderDocument.Build(book, layout, "", "");

        Assert.Equal(layout.Pages.Count, System.Text.RegularExpressions.Regex.Count(page, "<bt-page "));
        Assert.Matches("src=\"/res/image0000\\d\\.(jpg|png)\"", page);
        // The lantern link goes to the last chapter's section (the third), to the id its paragraph got.
        Assert.Matches("<a href=\"#bt-c2-[^\"]+\"[^>]*>the lantern</a>", page);
        Assert.Contains("href=\"https://example.com/\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_KF8_book_serves_its_stylesheet_and_links_its_chapters_to_it()
    {
        using var book = Open("sample.azw3");

        var head = book.ReadText(book.Spine[1].Path);
        var css = System.Text.RegularExpressions.Regex.Match(head, "href=\"(flow\\d+\\.css)\"").Groups[1].Value;
        Assert.NotEmpty(css);
        Assert.Equal("text/css", book.MediaTypeOf(css));
        Assert.Contains("color", Encoding.UTF8.GetString(book.ReadBytes(css)), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Books))]
    public void A_book_with_DRM_is_protected_but_keeps_its_metadata(string name)
    {
        var bytes = Bytes(name);
        // The encryption field of each header record: 2 is Mobipocket's DRM.
        foreach (var record in HeaderRecords(bytes)) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(record + 12), 2);

        using var book = Ebook.Open(new MemoryStream(bytes));

        Assert.True(book.IsProtected);
        Assert.Equal("The Sample Road", book.Metadata.Title);
        Assert.Empty(book.Spine);
    }

    static IEnumerable<int> HeaderRecords(byte[] bytes)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(76));
        for (var i = 0; i < count; i++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(78 + i * 8));
            if (offset + 20 <= bytes.Length && bytes.AsSpan(offset + 16, 4).SequenceEqual("MOBI"u8)) yield return offset;
        }
    }

    [Theory]
    [InlineData(new byte[] { 0xEA, (byte)'D', (byte)'R', (byte)'M', (byte)'I', (byte)'O', (byte)'N', 0xEE, 0, 0 }, "KFX")]
    [InlineData(new byte[] { (byte)'C', (byte)'O', (byte)'N', (byte)'T', 2, 0, 0, 0 }, "KFX")]
    [InlineData(new byte[] { (byte)'T', (byte)'P', (byte)'Z', (byte)'0', 0 }, "Topaz")]
    [InlineData(new byte[] { 1, 2, 3 }, "isn't an e-book")]
    public void Kindle_formats_it_cant_read_say_so(byte[] bytes, string says)
    {
        var ex = Assert.Throws<EbookException>(() => Ebook.Open(new MemoryStream(bytes)));
        Assert.Contains(says, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_damaged_book_fails_with_a_reason()
    {
        var bytes = Bytes("sample.azw3")[..2000];

        var ex = Assert.Throws<EbookException>(() => Ebook.Open(new MemoryStream(bytes)));
        Assert.Contains("Kindle book", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PalmDOC_reads_literals_raw_runs_spaces_and_back_references()
    {
        // "ab", a raw run of two bytes, " c" in one byte, then three bytes from six back.
        byte[] packed = [(byte)'a', (byte)'b', 2, 1, 2, 0xE3, 0x80, 6 << 3];

        var text = MobiCompression.PalmDoc(packed, 100);

        Assert.Equal([(byte)'a', (byte)'b', 1, 2, (byte)' ', 0x63, (byte)'a', (byte)'b', 1], text);
    }

    [Fact]
    public void PalmDOC_stops_at_the_limit()
    {
        // One literal, then a back reference repeating it, over and over.
        var packed = new List<byte> { (byte)'a' };
        for (var i = 0; i < 1000; i++) packed.AddRange([0x80, (1 << 3) | 7]);

        Assert.Throws<EbookException>(() => MobiCompression.PalmDoc([.. packed], 500));
    }

    [Fact]
    public void Huffman_codes_name_dictionary_phrases_including_ones_that_are_themselves_compressed()
    {
        // Every code is two bits: 11 names phrase 0, 10 phrase 1, 01 phrase 2 and 00 phrase 3. Phrase 2 is itself codes.
        var (huff, cdic) = SyntheticHuffman(["the ", "road ", "", "."], packedPhrase: 2, packedCodes: 0b1110_1100);
        var huffman = new MobiCompression.Huffman(huff, [cdic], 1000);

        var text = Encoding.Latin1.GetString(huffman.Unpack([0b1110_0100]));

        Assert.Equal("the road the road the ..", text);
    }

    /// <summary>
    /// A Huffman table where every code is two bits long and complete, so code c names phrase 3 - c, and one dictionary
    /// of the phrases, one of which is itself one byte of codes.
    /// </summary>
    static (byte[] Huff, byte[] Cdic) SyntheticHuffman(string[] phrases, int packedPhrase, byte packedCodes)
    {
        var huff = new byte[24 + 1024 + 512];
        "HUFF\0\0\0\u0018"u8.CopyTo(huff);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(12), 24 + 1024);
        for (var b = 0; b < 256; b++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(huff.AsSpan(24 + b * 4), (3u << 8) | 0x80 | 2);
        }
        var cdic = new List<byte>();
        cdic.AddRange("CDIC\0\0\0\u0010"u8.ToArray());
        cdic.AddRange(BigEndian((uint)phrases.Length));
        cdic.AddRange(BigEndian(2));
        var offsets = new List<int>();
        var bodies = new List<byte>();
        for (var i = 0; i < phrases.Length; i++)
        {
            offsets.Add(phrases.Length * 2 + bodies.Count);
            var packed = i == packedPhrase;
            var bytes = packed ? [packedCodes] : Encoding.Latin1.GetBytes(phrases[i]);
            var header = (ushort)(bytes.Length | (packed ? 0 : 0x8000));
            bodies.Add((byte)(header >> 8));
            bodies.Add((byte)header);
            bodies.AddRange(bytes);
        }
        foreach (var offset in offsets)
        {
            cdic.Add((byte)(offset >> 8));
            cdic.Add((byte)offset);
        }
        cdic.AddRange(bodies);
        return (huff, [.. cdic]);
    }

    static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(3, 5)]
    public void Trailing_entries_are_measured_from_the_end(int flags, int expected)
    {
        // Text, then a multibyte overlap of 2 bytes (count 1 in the low bits), then a 3-byte entry whose size ends it.
        byte[] record = flags switch
        {
            0 => [(byte)'a', (byte)'b'],
            1 => [(byte)'a', (byte)'b', 0xAA, 0x01],
            2 => [(byte)'a', (byte)'b', 9, 9, 0x83],
            _ => [(byte)'a', (byte)'b', 0xAA, 0x01, 9, 9, 0x83],
        };

        Assert.Equal(expected, MobiBook.TrailingSize(record, flags));
    }
}
