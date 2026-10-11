using Dapper;

namespace Bibliotaph.Index.Tests;

/// <summary>What Ask this book reads from index.db and keeps there: pages by words, page vectors and coverage.</summary>
public sealed class AskQueriesTests : IndexFixture
{
    IndexStore _store = null!;
    AskQueries _ask = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _store = new IndexStore(Writer, Clock);
        _ask = new AskQueries(Database);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DocRow Doc(long id, int pages) => new()
    {
        DocumentId = id,
        ContentHash = $"hash{id}",
        Format = "pdf",
        DisplayTitle = $"Invented Book {id}",
        PageCount = pages,
    };

    async Task AddBookAsync(long id, params string[] texts)
    {
        await _store.UpsertDocumentAsync(Doc(id, texts.Length),
            [.. texts.Select((_, i) => new PageRow(i, i == 0 ? "i" : i.ToString(System.Globalization.CultureInfo.InvariantCulture), 612, 792))], [], Ct);
        await _store.SetPageTextAsync(id, [.. texts.Select((t, i) => new PageTextRow(i, t, "pdf", 1, t.Length == 0))], Ct);
    }

    long PageId(long documentId, int pdfPage) =>
        Connection.ExecuteScalar<long>("SELECT id FROM page WHERE document_id = @documentId AND pdf_page = @pdfPage", new { documentId, pdfPage });

    long Vectors() => Connection.ExecuteScalar<long>("SELECT count(*) FROM page_vec");

    [Fact]
    public async Task Words_find_only_the_asked_books_pages_best_first()
    {
        await AddBookAsync(1, "The mill wheel turns.", "The dragon sleeps beneath the mill; the dragon dreams.", "");
        await AddBookAsync(2, "Another dragon, in another book.");

        var hits = await _ask.FindWordsAsync(1, "\"dragon\"* OR \"mill\"*", 10, Ct);

        Assert.Equal([1, 0], hits.Select(h => h.PdfPage));
        Assert.Single(await _ask.FindWordsAsync(1, "\"dragon\"*", 1, Ct));
    }

    [Fact]
    public async Task Pages_come_with_their_labels_and_text_and_labels_line_up_with_pdf_pages()
    {
        await AddBookAsync(1, "Front.", "Chapter One.");

        var pages = await _ask.GetPagesAsync(1, [PageId(1, 1), PageId(1, 0)], Ct);

        Assert.Equal([(0, "i", "Front."), (1, "1", "Chapter One.")], pages.Select(p => (p.PdfPage, p.Label, p.Text)));
        Assert.Equal(["i", "1"], await _ask.GetLabelsAsync(1, Ct));
        Assert.Empty(await _ask.GetPagesAsync(1, [], Ct));
    }

    [Fact]
    public async Task A_vector_survives_the_round_trip_and_is_read_only_for_its_model_and_version()
    {
        await AddBookAsync(1, "The mill.", "The dragon.");
        await _store.SaveVectorsAsync("embed-a", 1, [(PageId(1, 0), [0.25f, -1.5f, 3f])], Ct);

        var vector = Assert.Single(await _ask.GetVectorsAsync(1, "embed-a", 1, Ct));
        Assert.Equal(0, vector.PdfPage);
        Assert.Equal([0.25f, -1.5f, 3f], vector.Vector);
        Assert.Empty(await _ask.GetVectorsAsync(1, "embed-b", 1, Ct));
        Assert.Empty(await _ask.GetVectorsAsync(1, "embed-a", 2, Ct));
        Assert.Equal([1], (await _ask.GetPagesWithoutVectorsAsync(1, "embed-a", 1, 10, Ct)).Select(p => p.PdfPage));
        Assert.Equal([0, 1], (await _ask.GetPagesWithoutVectorsAsync(1, "embed-b", 1, 10, Ct)).Select(p => p.PdfPage));
    }

    [Fact]
    public async Task Changing_the_embedding_model_drops_the_books_old_vectors_and_only_that_books()
    {
        await AddBookAsync(1, "The mill.");
        await AddBookAsync(2, "The dragon.");
        await _store.SaveVectorsAsync("embed-a", 1, [(PageId(1, 0), [1f]), (PageId(2, 0), [1f])], Ct);

        await _store.DeleteStaleVectorsAsync(1, "embed-b", 1, Ct);

        Assert.Empty(await _ask.GetVectorsAsync(1, "embed-a", 1, Ct));
        Assert.Single(await _ask.GetVectorsAsync(2, "embed-a", 1, Ct));
        await _store.DeleteStaleVectorsAsync(2, "embed-a", 1, Ct);
        Assert.Single(await _ask.GetVectorsAsync(2, "embed-a", 1, Ct));
    }

    [Fact]
    public async Task A_page_whose_text_changes_or_that_goes_loses_its_vector_and_one_probed_again_keeps_it()
    {
        await AddBookAsync(1, "The mill.", "The dragon.", "The inn.");
        await _store.SaveVectorsAsync("embed-a", 1, [(PageId(1, 0), [1f]), (PageId(1, 1), [1f]), (PageId(1, 2), [1f])], Ct);

        // Probing again keeps the page rows, and the same text keeps the vector.
        await AddBookAsync(1, "The mill.", "The dragon.", "The inn.");
        Assert.Equal(3, Vectors());

        await _store.SetPageTextAsync(1, [new PageTextRow(1, "The dragon wakes.", "ocr", 0.9, false)], Ct);
        Assert.Equal([0, 2], (await _ask.GetVectorsAsync(1, "embed-a", 1, Ct)).Select(v => v.PdfPage).Order());

        await _store.UpsertDocumentAsync(Doc(1, 1), [new PageRow(0, "i", 612, 792)], [], Ct);
        Assert.Equal(1, Vectors());
        // A vector for a page that went while it was being made isn't kept.
        await _store.SaveVectorsAsync("embed-a", 1, [(987_654, [1f])], Ct);
        Assert.Equal(1, Vectors());
    }

    [Fact]
    public async Task Coverage_counts_pages_with_text_those_awaiting_ocr_and_those_with_vectors()
    {
        await AddBookAsync(1, "The mill.", "", "The inn.");
        await _store.SaveVectorsAsync("embed-a", 1, [(PageId(1, 2), [1f])], Ct);

        Assert.Equal(new PageCoverage(3, 2, 1, 1), await _ask.GetCoverageAsync(1, "embed-a", 1, Ct));
        Assert.Equal(new PageCoverage(3, 2, 1, 0), await _ask.GetCoverageAsync(1, null, 1, Ct));
        Assert.Equal(new PageCoverage(0, 0, 0, 0), await _ask.GetCoverageAsync(99, "embed-a", 1, Ct));
    }

    [Fact]
    public void A_vector_is_stored_as_little_endian_floats() =>
        Assert.Equal([0, 0, 128, 63, 0, 0, 0, 192], VectorBlob.ToBytes([1f, -2f]));
}
