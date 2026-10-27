using Bibliotaph.Pdf.Contracts;

namespace Bibliotaph.Pdf.Host.Tests;

/// <summary>
/// A confined worker, as the web server runs it: it reads the books in the folders it was given, and nothing else of
/// the machine's, and never sees the host's secrets. Linux only; skipped on a kernel without Landlock.
/// </summary>
public sealed class ConfinementTests(SyntheticPdfs pdfs) : IAsyncLifetime
{
    const string Secret = "BIBLIOTAPH_TEST_SECRET";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    readonly string _books = Directory.CreateTempSubdirectory("bibliotaph-confined-books-").FullName;
    readonly string _elsewhere = Directory.CreateTempSubdirectory("bibliotaph-confined-elsewhere-").FullName;
    WorkerClient? _worker;

    public async ValueTask InitializeAsync()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Confinement is Landlock's, on Linux.");
        Environment.SetEnvironmentVariable(Secret, "not for the worker");
        _worker = new WorkerClient(new WorkerOptions { ConfinedTo = [_books] });
        var ping = await _worker.SendAsync(new Request { Op = Op.Ping }, Timeout);
        Assert.True(ping.Ok, ping.Message);
        Assert.SkipWhen(ping.Message?.Contains("no Landlock", StringComparison.Ordinal) == true, ping.Message!);
        Assert.StartsWith("Confined by Landlock", ping.Message, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Secret, null);
        if (_worker is not null) await _worker.DisposeAsync();
        foreach (var folder in new[] { _books, _elsewhere }) Directory.Delete(folder, recursive: true);
    }

    static string Copy(string source, string folder)
    {
        var path = Path.Combine(folder, Path.GetFileName(source));
        File.Copy(source, path);
        return path;
    }

    async Task<Response> OpenAsync(string path) => await _worker!.SendAsync(new Request { Op = Op.Open, Path = path }, Timeout);

    [Fact]
    public async Task Reads_a_book_in_a_folder_it_was_given()
    {
        var open = await OpenAsync(Copy(pdfs.KnownText, _books));
        Assert.True(open.Ok, open.Message);

        var text = await _worker!.SendAsync(new Request { Op = Op.ExtractText, DocId = open.Doc!.DocId, PageIndex = SyntheticPdfs.KnownPhrasePage }, Timeout);

        Assert.True(text.Ok, text.Message);
        Assert.Contains(SyntheticPdfs.KnownPhrase, text.Text!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_a_scanned_page_by_ocr()
    {
        var open = await OpenAsync(Copy(pdfs.Scanned, _books));
        Assert.True(open.Ok, open.Message);

        var ocr = await _worker!.SendAsync(new Request { Op = Op.Ocr, DocId = open.Doc!.DocId, PageIndex = 0, Scale = 300 / 72.0 }, Timeout);

        Assert.SkipWhen(ocr.Error == ErrorKind.OcrUnavailable, $"OCR unavailable: {ocr.Message}");
        Assert.True(ocr.Ok, ocr.Message);
        Assert.Contains("goblin", ocr.Ocr!.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cannot_open_a_book_anywhere_else()
    {
        var open = await OpenAsync(Copy(pdfs.KnownText, _elsewhere));

        Assert.False(open.Ok, "The confined worker opened a file outside the folders it was given.");
    }

    [Fact]
    public void Starts_without_the_hosts_environment()
    {
        var environment = File.ReadAllText($"/proc/{_worker!.WorkerProcessId}/environ");

        Assert.DoesNotContain(Secret, environment, StringComparison.Ordinal);
        Assert.Contains("DOTNET_EnableDiagnostics=0", environment, StringComparison.Ordinal);
    }
}
