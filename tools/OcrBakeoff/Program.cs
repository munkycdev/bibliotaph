using System.Diagnostics;
using System.Globalization;
using Bibliotaph.Pdf.Host;
using OcrBakeoff;

// Renders sample pages through the real PDF worker and runs every OCR engine over the same pixels.
// See README.md for how to run it and how to read the report.

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(Options.Usage);
    return 2;
}

Directory.CreateDirectory(options.Output);
Console.WriteLine($"Writing results to {options.Output}");

var engines = new List<IOcrCandidate>();
await TryAddAsync(() => Task.FromResult<IOcrCandidate>(new WindowsOcr()));
await TryAddAsync(async () => await TesseractOcr.CreateAsync(options.TessData, "fast"));
await TryAddAsync(async () => await TesseractOcr.CreateAsync(options.TessData, "best"));
if (engines.Count == 0)
{
    Console.Error.WriteLine("No OCR engine could be started.");
    return 1;
}

await using var worker = new WorkerClient(new WorkerOptions { Name = "bake-off worker", DefaultTimeout = TimeSpan.FromMinutes(2) });
var report = new Report(options.Output, [.. engines.Select(e => e.Name)]);

// Vocabulary for the scanned books: every word in the digital books' text layers.
var vocabulary = new HashSet<string>(StringComparer.Ordinal);
foreach (var digital in options.Digital)
{
    var pages = await Book.AllTextAsync(worker, digital);
    foreach (var page in pages) vocabulary.UnionWith(Words.Of(page.Text));
}
if (vocabulary.Count > 0) Console.WriteLine($"Vocabulary: {vocabulary.Count:N0} words from {options.Digital.Count} digital book(s)");

var bookNumber = 0;
foreach (var (path, scanned) in options.Scanned.Select(p => (p, true)).Concat(options.Digital.Select(p => (p, false))))
{
    bookNumber++;
    var book = await Book.OpenAsync(worker, path);
    Console.WriteLine($"{Path.GetFileName(path)}: {book.PageCount} pages, {(scanned ? "scanned" : "digital, compared with its text layer")}");

    foreach (var pageIndex in await book.SamplePagesAsync(options.Pages, needText: !scanned))
    {
        var page = await book.RenderAsync(pageIndex, options.Dpi, engines.Min(e => e.MaxImageDimension));
        var reference = scanned ? null : await book.TextAsync(pageIndex);
        var row = new PageResult(bookNumber, Path.GetFileName(path), scanned, pageIndex, page.Width, page.Height, reference);

        foreach (var engine in engines)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var text = await engine.RecognizeAsync(page);
                row.Engines[engine.Name] = new EngineResult(text, sw.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                row.Engines[engine.Name] = new EngineResult($"[{ex.GetType().Name}: {ex.Message}]", double.NaN);
            }
        }
        report.Add(row, page, vocabulary);
        Console.WriteLine($"  page {pageIndex + 1}: " + string.Join(", ",
            engines.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Name} {row.Engines[e.Name].Milliseconds:F0} ms"))));
    }
    await book.CloseAsync();
}

report.Write();
foreach (var engine in engines) engine.Dispose();
Console.WriteLine();
Console.WriteLine(report.SummaryText());
Console.WriteLine($"Open {Path.Combine(options.Output, "report.html")} to read the pages side by side.");
return 0;

async Task TryAddAsync(Func<Task<IOcrCandidate>> create)
{
    try
    {
        var engine = await create();
        await engine.WarmUpAsync();
        engines.Add(engine);
        Console.WriteLine($"Engine ready: {engine.Name} ({engine.Description})");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Engine skipped: {ex.Message}");
    }
}
