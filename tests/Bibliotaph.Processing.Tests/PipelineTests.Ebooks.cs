using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Core.Search;
using Bibliotaph.Ebooks.Tests;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;
using Dapper;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    void Write(string relative, byte[] bytes)
    {
        var target = Path.Combine(_library, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
    }

    static SyntheticEpub LanternEpub() => new SyntheticEpub
    {
        Title = "Tides of the Lantern Coast",
        ExtraMetadata = """
            <dc:creator>Ada Quill</dc:creator>
            <dc:publisher>Gull Rock Games</dc:publisher>
            <dc:description>An adventure for four characters of 3rd level.</dc:description>
            """,
    }
        .File("img/cover.png", FakeCodec.Png(600, 900), "image/png", "cover-image")
        .Chapter("one.xhtml", "<h1>The Harbour</h1>" + string.Concat(Enumerable.Range(0, 8).Select(i => $"<p>{SyntheticEpub.Words(700, $"gull{i}")}</p>")))
        .Chapter("two.xhtml", "<h1>The Reef</h1><p>A kraken sleeps beneath the drowned lighthouse.</p>");

    /// <summary>E-books: read in place like PDFs, cut into pages of their own, searchable and catalogued.</summary>
    public sealed class EbooksTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task An_EPUB_is_read_in_place_into_pages_that_search_finds()
        {
            var epub = LanternEpub().Build();
            Write("Novels/lantern-coast.epub", epub);
            Zip("Bundles/Sea Bundle.zip", Saved, ("Sea Bundle/lantern-coast-copy.epub", epub));
            var before = Directory.GetFiles(_library, "*", SearchOption.AllDirectories).Order().ToArray();
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            var progress = await SettleAsync();

            // The loose file and its copy in the ZIP are one document; the EPUB isn't listed as a ZIP of files.
            Assert.Equal(1, progress.Documents);
            await using var c = _index.OpenRead();
            var (documentId, format, pages) = c.QuerySingle<(long, string, long)>("SELECT document_id, format, page_count FROM doc");
            Assert.Equal(SourceFormats.Epub, format);
            Assert.Equal(4, pages); // three pages of the first chapter, one of the second
            // By its id: the document is titled by whichever of its two files was found first.
            Assert.Equal("Complete", StatusOf(c, documentId, Stage.Text));
            Assert.Equal("Complete", StatusOf(c, documentId, Stage.Covers));
            Assert.Null(StatusOf(c, documentId, Stage.Ocr));
            Assert.Equal(["c1", "c2"], c.Query<string>("SELECT title FROM outline WHERE document_id = @documentId ORDER BY ord", new { documentId }));

            // Search finds the phrase on the page it was cut into, and format:epub and the Books filter take the card.
            var search = new LibraryQueries(_index);
            var filter = new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
            var hit = Assert.Single(Assert.Single((await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("kraken")), filter, ct: Ct)).Entries).Pages);
            Assert.Equal(3, hit.PdfPage);
            var card = Assert.Single(await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("format:epub")), filter, ct: Ct));
            Assert.Empty(await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("format:pdf")), filter, ct: Ct));
            Assert.Single(await search.ListAsync(filter with { Kind = KindFilter.Books }, ct: Ct));

            // The package's title beats the file name; its author and the levels in its description are suggested.
            Assert.Equal("Tides of the Lantern Coast", card.Title);
            var fields = (await _metadataStore.GetAsync(card.EntryId, Ct)).Compute();
            var author = Assert.Single(fields[MetadataFields.Authors].Values);
            Assert.Equal(("Ada Quill", AssertionOrigin.Embedded), (author.Value, author.Origin));
            Assert.Equal("3", Assert.Single(fields[MetadataFields.Levels].Values).Value);

            Assert.Equal(before, Directory.GetFiles(_library, "*", SearchOption.AllDirectories).Order());
        }

        [Fact]
        public async Task An_EPUB_protected_by_DRM_is_listed_by_its_package_but_its_text_is_not_read()
        {
            Write("Store/locked.epub", LanternEpub().Encrypt("OEBPS/one.xhtml").Encrypt("OEBPS/two.xhtml").Build());
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            await using var c = _index.OpenRead();
            Assert.Equal("Complete", StatusOf(c, "Locked", Stage.Probe));
            Assert.Equal("Failed", StatusOf(c, "Locked", Stage.Text));
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT count(*) FROM page"));
            var search = new LibraryQueries(_index);
            var card = Assert.Single(await search.ListAsync(new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct)), ct: Ct));
            Assert.Equal("Tides of the Lantern Coast", card.Title);
            Assert.Equal(TextAccess.Protected, card.TextAccess);
            Assert.Contains(await _queries.GetAttentionAsync(ct: Ct), a => a.Reason == TextAccessReasons.ProtectedEbook);
        }

        [Fact]
        public async Task A_damaged_EPUB_keeps_a_card_by_its_file_name_and_says_why()
        {
            Write("broken.epub", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            await using var c = _index.OpenRead();
            Assert.Equal("Failed", StatusOf(c, "Broken", Stage.Probe));
            Assert.Contains(await _queries.GetAttentionAsync(ct: Ct), a => a.Reason!.Contains("EPUB", StringComparison.Ordinal));
        }

        static byte[] Kindle(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ebooks", name));

        [Fact]
        public async Task Kindle_books_are_read_in_place_into_pages_that_search_finds()
        {
            Write("Kindle/sample.azw3", Kindle("sample.azw3"));
            Write("Kindle/old/sample.mobi", Kindle("sample-old.mobi"));
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            // Two files with different bytes: two documents, each with its own pages, contents and cover.
            await using var c = _index.OpenRead();
            Assert.Equal([SourceFormats.Azw3, SourceFormats.Mobi], c.Query<string>("SELECT format FROM doc ORDER BY format"));
            Assert.All(c.Query<long>("SELECT page_count FROM doc"), pages => Assert.InRange(pages, 4, 6));
            Assert.Equal(0, c.ExecuteScalar<long>("SELECT count(*) FROM stage_status WHERE status = 'Failed'"));
            Assert.Equal(2, c.ExecuteScalar<long>("SELECT count(DISTINCT document_id) FROM outline WHERE title = 'The Crossing'"));

            var search = new LibraryQueries(_index);
            var filter = new LibraryFilter(await _libraryStore.GetVisibleEntryIdsAsync(ct: Ct));
            var hits = (await search.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("lantern keeper")), filter, ct: Ct)).Entries;
            Assert.Equal(2, hits.Sum(e => e.Pages.Count));
            var cards = await search.SearchDocumentsAsync(SearchPlan.From(SearchQuery.Parse("format:kindle")), filter, ct: Ct);
            Assert.Equal(2, cards.Count);
            Assert.All(cards, card => Assert.Equal("The Sample Road", card.Title));
            Assert.Equal(2, (await search.ListAsync(filter with { Kind = KindFilter.Books }, ct: Ct)).Count);
            var fields = (await _metadataStore.GetAsync(cards[0].EntryId, Ct)).Compute();
            Assert.Contains(fields[MetadataFields.Authors].Values, a => (a.Value, a.Origin) == ("Ann Writer", AssertionOrigin.Embedded));
        }

        [Fact]
        public async Task A_KFX_book_keeps_a_card_and_says_it_cant_be_read()
        {
            Write("Kindle/locked.kfx", [0xEA, (byte)'D', (byte)'R', (byte)'M', (byte)'I', (byte)'O', (byte)'N', 0xEE, 1, 2, 3, 4]);
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();

            await using var c = _index.OpenRead();
            Assert.Equal("Failed", StatusOf(c, "Locked", Stage.Probe));
            Assert.Contains(await _queries.GetAttentionAsync(ct: Ct), a => a.Reason!.Contains("KFX", StringComparison.Ordinal));
        }
    }
}
