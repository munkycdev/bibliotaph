using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Pdf.Host.Tests;

/// <summary>The synthetic PDFs through the real worker process.</summary>
public sealed class PdfWorkerTests(SyntheticPdfs pdfs) : IAsyncLifetime
{
    readonly WorkerClient _worker = new();
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _worker.DisposeAsync();

    async Task<DocInfo> OpenAsync(string path, string? password = null)
    {
        var response = await _worker.SendAsync(new Request { Op = Op.Open, Path = path, Password = password }, Timeout);
        Assert.True(response.Ok, $"{response.Error}: {response.Message}");
        return response.Doc!;
    }

    [Fact]
    public async Task Opens_a_text_pdf_and_reports_its_pages()
    {
        var doc = await OpenAsync(pdfs.KnownText);

        Assert.Equal(SyntheticPdfs.PageCount, doc.PageCount);
        Assert.False(doc.IsEncrypted);
        Assert.All(doc.PageSizes, size => Assert.True(size.Width > 0 && size.Height > 0));
    }

    [Fact]
    public async Task Extracts_the_text_of_a_page_and_none_from_an_image_only_page()
    {
        var doc = await OpenAsync(pdfs.KnownText);

        var text = await _worker.SendAsync(new Request { Op = Op.ExtractText, DocId = doc.DocId, PageIndex = SyntheticPdfs.KnownPhrasePage }, Timeout);
        var image = await _worker.SendAsync(new Request { Op = Op.ExtractText, DocId = doc.DocId, PageIndex = SyntheticPdfs.ImageOnlyPage }, Timeout);

        Assert.Contains(SyntheticPdfs.KnownPhrase, text.Text!.Text, StringComparison.Ordinal);
        Assert.Equal(0, image.Text!.CharCount);
    }

    [Fact]
    public async Task Reports_document_info_and_the_outline()
    {
        var doc = await OpenAsync(pdfs.KnownText);

        Assert.Equal(SyntheticPdfs.Title, doc.Metadata.Title);
        Assert.Equal(SyntheticPdfs.Author, doc.Metadata.Author);
        Assert.Null(doc.Metadata.Keywords);
        Assert.Equal(
            [new OutlineItem("Chapter one", SyntheticPdfs.KnownPhrasePage, 0), new OutlineItem("A picture", SyntheticPdfs.ImageOnlyPage, 1)],
            doc.Outline);
    }

    [Fact]
    public async Task Opens_a_file_whose_name_is_outside_the_ansi_code_page()
    {
        var doc = await OpenAsync(pdfs.NonAsciiName);

        Assert.Equal(SyntheticPdfs.PageCount, doc.PageCount);
    }

    [Fact]
    public async Task Extracts_a_run_of_pages_in_one_request()
    {
        var doc = await OpenAsync(pdfs.KnownText);

        var response = await _worker.SendAsync(new Request { Op = Op.ExtractPages, DocId = doc.DocId, PageIndex = 1, PageCount = 10 }, Timeout);

        Assert.True(response.Ok, response.Message);
        var pages = response.Pages!;
        Assert.Equal([1, 2], pages.Select(p => p.PageIndex));
        Assert.Contains(SyntheticPdfs.KnownPhrase, pages[0].Text, StringComparison.Ordinal);
        Assert.Equal(0, pages[1].CharCount);
        Assert.All(pages, p => Assert.Null(p.Error));
    }

    [Fact]
    public async Task Reads_a_page_with_no_text_layer_by_ocr()
    {
        var doc = await OpenAsync(pdfs.Scanned);
        var layer = await _worker.SendAsync(new Request { Op = Op.ExtractText, DocId = doc.DocId, PageIndex = 0 }, Timeout);
        Assert.Equal(0, layer.Text!.CharCount);

        var response = await _worker.SendAsync(new Request { Op = Op.Ocr, DocId = doc.DocId, PageIndex = 0, Scale = 300 / 72.0 }, Timeout);

        // Linux has no Windows OCR, and a Windows image may have no OCR language installed.
        Assert.SkipWhen(response.Error == ErrorKind.OcrUnavailable, $"OCR unavailable: {response.Message}");
        Assert.True(response.Ok, response.Message);
        var ocr = response.Ocr!;
        Assert.Contains("goblin", ocr.Text, StringComparison.OrdinalIgnoreCase);
        // Boxes are in PDF points, inside the page, around the phrase drawn at y = 160 from the top.
        var size = doc.PageSizes[0];
        Assert.All(ocr.Words, w => Assert.InRange(w.Box.Left, 0, size.Width));
        Assert.All(ocr.Words, w => Assert.InRange(size.Height - w.Box.Top, 100, 170));
    }

    [Fact]
    public async Task Whole_book_find_hits_the_right_page_with_highlight_boxes()
    {
        var doc = await OpenAsync(pdfs.KnownText);

        var response = await _worker.SendAsync(new Request { Op = Op.Find, DocId = doc.DocId, PageIndex = -1, Query = "old mill" }, Timeout);

        var hit = Assert.Single(response.Hits!);
        Assert.Equal(SyntheticPdfs.KnownPhrasePage, hit.PageIndex);
        Assert.NotEmpty(hit.Rects);
    }

    [Fact]
    public async Task Renders_a_page_into_shared_memory()
    {
        var doc = await OpenAsync(pdfs.KnownText);

        var (response, pixels) = await _worker.RenderAsync(new Request { DocId = doc.DocId, PageIndex = SyntheticPdfs.ImageOnlyPage, Scale = 1.0 }, Timeout);

        Assert.True(response.Ok, response.Message);
        var render = response.Render!;
        Assert.Equal((int)Math.Round(doc.PageSizes[SyntheticPdfs.ImageOnlyPage].Width), render.Width);
        Assert.Equal(render.Stride * render.Height, pixels!.Length);
        Assert.Contains(pixels, b => b != 0xFF);   // not a blank white page
    }

    [Fact]
    public async Task A_password_file_needs_its_password_and_then_opens()
    {
        var withoutPassword = await _worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.PasswordProtected }, Timeout);
        Assert.False(withoutPassword.Ok);
        Assert.Equal(ErrorKind.Password, withoutPassword.Error);

        var wrongPassword = await _worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.PasswordProtected, Password = "wrong" }, Timeout);
        Assert.Equal(ErrorKind.Password, wrongPassword.Error);

        var doc = await OpenAsync(pdfs.PasswordProtected, SyntheticPdfs.Password);
        Assert.True(doc.IsEncrypted);
        Assert.True(doc.SecurityRevision >= 6, $"Expected AES-256 (revision 6), got revision {doc.SecurityRevision}.");
        Assert.Equal(SyntheticPdfs.PageCount, doc.PageCount);
    }

    [Fact]
    public async Task A_locked_file_that_forbids_copying_opens_with_its_password_and_still_gives_its_text()
    {
        var withoutPassword = await _worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.LockedNoCopying }, Timeout);
        Assert.Equal(ErrorKind.Password, withoutPassword.Error);

        var doc = await OpenAsync(pdfs.LockedNoCopying, Bibliotaph.App.Services.SmokePdfs.Password);
        Assert.True(doc.IsEncrypted);
        Assert.False(doc.CanCopy);
        var text = await _worker.SendAsync(new Request { Op = Op.ExtractText, DocId = doc.DocId, PageIndex = 0 }, Timeout);
        Assert.Contains(Bibliotaph.App.Services.SmokePdfs.LockedPhrase, text.Text!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_under_a_protection_scheme_pdfium_lacks_is_a_security_error_password_or_not()
    {
        var response = await _worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.Protected }, Timeout);
        var withPassword = await _worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.Protected, Password = "anything" }, Timeout);

        Assert.Equal(ErrorKind.Security, response.Error);
        Assert.Equal(ErrorKind.Security, withPassword.Error);
        Assert.True((await _worker.SendAsync(new Request { Op = Op.Ping }, Timeout)).Ok);
    }

    [Fact]
    public async Task A_truncated_file_is_reported_as_damaged_without_crashing_the_worker()
    {
        var response = await _worker.SendAsync(new Request { Op = Op.Open, Path = pdfs.Truncated }, Timeout);

        Assert.False(response.Ok);
        Assert.Equal(ErrorKind.Format, response.Error);
        Assert.True((await _worker.SendAsync(new Request { Op = Op.Ping }, Timeout)).Ok);
        Assert.Equal(0, _worker.Restarts);
    }

    [Fact]
    public async Task A_broken_cross_reference_table_is_repaired()
    {
        var doc = await OpenAsync(pdfs.BrokenXref);

        Assert.Equal(SyntheticPdfs.PageCount, doc.PageCount);
    }

    [Fact]
    public async Task Pdf_contracts_reference_only_the_base_class_library()
    {
        await Task.CompletedTask;
        var references = typeof(Request).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal),
            $"Bibliotaph.Pdf.Contracts must reference nothing outside the BCL, but references {name}."));
    }
}
