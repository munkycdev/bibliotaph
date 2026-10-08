using System.Diagnostics;
using System.Security.Cryptography;
using Bibliotaph.Spike.Contracts;
using Bibliotaph.Spike.Harness;
using Bibliotaph.Spike.WorkerHost;

var manifestPath = Path.Combine("corpus", "manifest.json");
var outDir = Path.Combine("results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
var allPages = false;
var selfTest = false;
long memoryMb = 1024;
string? only = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--manifest": manifestPath = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--all-pages": allPages = true; break;
        case "--memory-mb": memoryMb = long.Parse(args[++i]); break;
        case "--only": only = args[++i]; break;
        case "--self-test": selfTest = true; break;
        default:
            Console.Error.WriteLine($"Unknown argument {args[i]}.");
            Console.Error.WriteLine("Usage: Harness [--manifest corpus\\manifest.json] [--out results\\run] [--all-pages] [--memory-mb 1024] [--only <text>] [--self-test]");
            return 2;
    }
}

var options = new WorkerOptions { MemoryLimitBytes = memoryMb * 1024 * 1024 };
return selfTest ? await SelfTest.RunAsync(options) : await CorpusRun.RunAsync(manifestPath, outDir, allPages, only, options);

static class SelfTest
{
    /// <summary>Proves the isolation gates: a crash, a hang and a memory runaway are contained and the worker comes back.</summary>
    public static async Task<int> RunAsync(WorkerOptions options)
    {
        var failures = 0;
        await using var worker = new WorkerClient(options with { MemoryLimitBytes = 512L * 1024 * 1024 });

        async Task Check(string name, Func<Task<string?>> test)
        {
            var sw = Stopwatch.StartNew();
            string? problem;
            try { problem = await test(); }
            catch (Exception ex) { problem = $"{ex.GetType().Name}: {ex.Message}"; }
            if (problem is not null) failures++;
            Console.WriteLine($"{(problem is null ? "PASS" : "FAIL")}  {name,-44} {sw.ElapsedMilliseconds,6} ms  {problem}");
        }

        async Task<string?> Ping() =>
            (await worker.SendAsync(new Request { Op = Op.Ping }, TimeSpan.FromSeconds(10))).Ok ? null : "ping failed";

        async Task<string?> Expect<TException>(Request request, TimeSpan timeout, Func<Response, bool>? alsoAccept = null) where TException : Exception
        {
            // A worker that cannot start would "fail" every fault test for the wrong reason.
            if (await Ping() is { } unhealthy) return $"worker not healthy before the test: {unhealthy}";
            try
            {
                var response = await worker.SendAsync(request, timeout);
                return alsoAccept?.Invoke(response) == true ? null : $"expected {typeof(TException).Name}, got a response";
            }
            catch (TException) { return null; }
        }

        await Check("worker starts and answers", Ping);
        await Check("crash is contained", () => Expect<WorkerCrashedException>(new Request { Op = Op.Crash }, TimeSpan.FromSeconds(10)));
        await Check("worker restarts after crash", Ping);
        await Check("hang is killed at timeout (3 s)", () => Expect<WorkerTimeoutException>(new Request { Op = Op.Hang }, TimeSpan.FromSeconds(3)));
        await Check("worker restarts after hang", Ping);
        if (OperatingSystem.IsWindows())
        {
            // Either the job object kills the worker, or the allocation fails inside it; both are contained.
            await Check("memory runaway stopped at 512 MB", () => Expect<WorkerCrashedException>(
                new Request { Op = Op.AllocateUntilKilled }, TimeSpan.FromSeconds(60), r => r is { Ok: false, Error: ErrorKind.Internal }));
            await Check("worker restarts after memory runaway", Ping);
        }
        else
        {
            Console.WriteLine("SKIP  memory runaway (needs a Windows job object)");
        }
        Console.WriteLine($"Worker restarts: {worker.Restarts}");
        Console.WriteLine(failures == 0 ? "Self-test passed." : $"Self-test FAILED ({failures}).");
        return failures == 0 ? 0 : 1;
    }
}

static class CorpusRun
{
    const int SampleTarget = 25;
    const int FitWidthPixels = 1400;       // fit-width on a 1440p-class window at 100% scaling
    const int TileSize = 1024;
    const int MaxCrashesPerFile = 3;
    static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(120);
    static readonly TimeSpan PageTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(300);

    public static async Task<int> RunAsync(string manifestPath, string outDir, bool allPages, string? only, WorkerOptions options)
    {
        var manifest = Manifest.Load(manifestPath);
        var files = manifest.Files.Where(f => only is null || f.Path.Contains(only, StringComparison.OrdinalIgnoreCase)).ToList();
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"{files.Count} files · results in {Path.GetFullPath(outDir)}");

        // Ask for any passwords up front so the run can be left alone.
        var passwords = files.Select(ResolvePassword).ToList();

        // The same file may appear twice (for example, with and without its password).
        var before = files.Select(f => f.FullPath).Distinct().ToDictionary(p => p, FileFacts.Read);

        using var filesCsv = new Csv(Path.Combine(outDir, "files.csv"),
            "file", "category", "size_mb", "outcome", "pages", "sampled", "encrypted", "can_copy", "can_print",
            "labels_defined", "labels_differ", "open_cold_ms", "open_warm_ms", "render_p50_ms", "render_p95_ms",
            "render_200_p95_ms", "tile_400_ms", "text_pages_no_text", "text_p95_ms", "char_boxes_ms",
            "find_word", "find_ms", "find_hits", "find_on_expected_page", "find_has_rects",
            "peak_mb", "worker_restarts", "hash_ms", "error");
        using var pagesCsv = new Csv(Path.Combine(outDir, "pages.csv"),
            "file", "pdf_page", "label", "width_pt", "height_pt", "zoom", "pixels", "render_ms", "worker_render_ms",
            "text_chars", "text_ms", "outcome");

        var overall = 0;
        for (var n = 0; n < files.Count; n++)
        {
            var file = files[n];
            Console.Write($"{Path.GetFileName(file.FullPath),-60} ");
            var result = await RunFileAsync(file, passwords[n], allPages, options, pagesCsv);
            filesCsv.Row(file.Path, file.Category, before[file.FullPath].Length / 1048576.0, result.Outcome, result.Pages, result.Sampled,
                result.Encrypted, result.CanCopy, result.CanPrint, result.LabelsDefined, result.LabelsDiffer,
                result.OpenColdMs, result.OpenWarmMs, P(result.Render100, 50), P(result.Render100, 95), P(result.Render200, 95),
                result.TileMs, result.PagesWithoutText, P(result.TextMs, 95), result.CharBoxesMs,
                result.FindWord, result.FindMs, result.FindHits, result.FindOnExpectedPage, result.FindHasRects,
                result.PeakBytes / 1048576.0, result.Restarts, before[file.FullPath].HashMs, result.Error);
            Console.WriteLine($"{result.Outcome,-16} p95 {P(result.Render100, 95):0} ms  peak {result.PeakBytes / 1048576.0:0} MB");
            if (result.Outcome != "ok") overall = 1;
        }

        // Source integrity: nothing the harness touched may have changed.
        using var integrityCsv = new Csv(Path.Combine(outDir, "integrity.csv"), "file", "unchanged", "size_before", "size_after", "modified_before", "modified_after");
        var changed = 0;
        foreach (var (path, a) in before)
        {
            var b = FileFacts.Read(path);
            var same = a.Sha256 == b.Sha256 && a.Length == b.Length && a.LastWriteUtc == b.LastWriteUtc;
            if (!same) changed++;
            integrityCsv.Row(path, same, a.Length, b.Length, a.LastWriteUtc.ToString("O"), b.LastWriteUtc.ToString("O"));
        }
        Console.WriteLine(changed == 0 ? "Source integrity: all files unchanged." : $"Source integrity: {changed} FILE(S) CHANGED.");
        return changed == 0 ? overall : 3;
    }

    static async Task<FileResult> RunFileAsync(ManifestEntry file, string? password, bool allPages, WorkerOptions options, Csv pagesCsv)
    {
        var result = new FileResult();
        // A fresh worker per file keeps peak-memory readings per file and stops one bad file affecting the next.
        await using var worker = new WorkerClient(options);
        try
        {
            var openRequest = new Request { Op = Op.Open, Path = file.FullPath, Password = password };
            var sw = Stopwatch.StartNew();
            var open = await worker.SendAsync(openRequest, OpenTimeout);
            result.OpenColdMs = sw.Elapsed.TotalMilliseconds;
            if (!open.Ok || open.Doc is null)
            {
                result.Outcome = $"open-{open.Error}".ToLowerInvariant();
                result.Error = open.Message;
                return result;
            }

            var doc = open.Doc;
            await worker.SendAsync(new Request { Op = Op.Close, DocId = doc.DocId });
            sw.Restart();
            doc = (await worker.SendAsync(openRequest, OpenTimeout)).Doc ?? doc;
            result.OpenWarmMs = sw.Elapsed.TotalMilliseconds;

            result.Pages = doc.PageCount;
            result.Encrypted = doc.IsEncrypted;
            result.CanCopy = doc.CanCopy;
            result.CanPrint = doc.CanPrint;
            result.LabelsDefined = doc.PageLabels.Count(l => l is not null);
            result.LabelsDiffer = doc.PageLabels.Select((l, i) => l is not null && l != (i + 1).ToString()).Count(d => d);

            var sample = SamplePages(doc.PageCount, allPages);
            result.Sampled = sample.Count;
            var crashes = 0;
            var docId = doc.DocId;
            string? textForFind = null;
            var textPageForFind = -1;

            async Task Reopen()
            {
                var again = await worker.SendAsync(openRequest, OpenTimeout);
                docId = again.Doc?.DocId ?? throw new WorkerException("Could not reopen after a worker restart.");
            }

            foreach (var index in sample)
            {
                var size = doc.PageSizes[index];
                var label = doc.PageLabels[index];
                if (size.Width <= 0)
                {
                    pagesCsv.Row(file.Path, index + 1, label, size.Width, size.Height, null, null, null, null, null, null, "no-size");
                    continue;
                }

                foreach (var zoom in new[] { 1.0, 2.0 })
                {
                    var scale = FitWidthPixels * zoom / size.Width;
                    var outcome = "ok";
                    double? ms = null, workerMs = null;
                    string? pixels = null;
                    try
                    {
                        sw.Restart();
                        var (render, _) = await worker.RenderAsync(new Request { DocId = docId, PageIndex = index, Scale = scale }, PageTimeout);
                        ms = sw.Elapsed.TotalMilliseconds;
                        workerMs = render.WorkerMs;
                        if (render.Ok && render.Render is { } info)
                        {
                            pixels = $"{info.Width}x{info.Height}";
                            (zoom == 1.0 ? result.Render100 : result.Render200).Add(ms.Value);
                        }
                        else outcome = $"failed-{render.Error}".ToLowerInvariant();
                    }
                    catch (WorkerException ex)
                    {
                        outcome = ex is WorkerTimeoutException ? "timeout" : "crash";
                        result.Error ??= ex.Message;
                        if (++crashes > MaxCrashesPerFile) throw;
                        await Reopen();
                    }
                    pagesCsv.Row(file.Path, index + 1, label, size.Width, size.Height, zoom, pixels, ms, workerMs, null, null, outcome);
                }

                try
                {
                    sw.Restart();
                    var text = await worker.SendAsync(new Request { Op = Op.ExtractText, DocId = docId, PageIndex = index }, PageTimeout);
                    var textMs = sw.Elapsed.TotalMilliseconds;
                    var visible = text.Text?.Text.Count(c => !char.IsWhiteSpace(c)) ?? 0;
                    result.TextMs.Add(textMs);
                    if (visible < 20) result.PagesWithoutText++;
                    else if (textForFind is null || Math.Abs(index - doc.PageCount / 2) < Math.Abs(textPageForFind - doc.PageCount / 2))
                    {
                        textForFind = text.Text!.Text;
                        textPageForFind = index;
                    }
                    pagesCsv.Row(file.Path, index + 1, label, size.Width, size.Height, "text", null, null, null, visible, textMs, text.Ok ? "ok" : $"failed-{text.Error}".ToLowerInvariant());
                }
                catch (WorkerException ex)
                {
                    result.Error ??= ex.Message;
                    if (++crashes > MaxCrashesPerFile) throw;
                    await Reopen();
                }
            }

            // Character boxes are what search highlights are drawn from.
            if (textPageForFind >= 0)
            {
                sw.Restart();
                var boxes = await worker.SendAsync(new Request { Op = Op.ExtractText, DocId = docId, PageIndex = textPageForFind, IncludeCharBoxes = true }, PageTimeout);
                if (boxes.Ok) result.CharBoxesMs = sw.Elapsed.TotalMilliseconds;
            }

            // High zoom: one 1024 px tile from the middle of the first page at 400%.
            var first = sample[0];
            if (doc.PageSizes[first].Width > 0)
            {
                var scale = FitWidthPixels * 4.0 / doc.PageSizes[first].Width;
                var fullW = (int)(doc.PageSizes[first].Width * scale);
                var fullH = (int)(doc.PageSizes[first].Height * scale);
                var tile = new PixelRect(Math.Max(0, fullW / 2 - TileSize / 2), Math.Max(0, fullH / 2 - TileSize / 2),
                    Math.Min(TileSize, fullW), Math.Min(TileSize, fullH));
                sw.Restart();
                var (render, _) = await worker.RenderAsync(new Request { DocId = docId, PageIndex = first, Scale = scale, Tile = tile }, PageTimeout);
                if (render.Ok) result.TileMs = sw.Elapsed.TotalMilliseconds;
            }

            // Whole-book search for a word known to be on a page, checked against that page.
            var word = file.FindWord ?? PickWord(textForFind);
            if (word is not null)
            {
                var expected = file.FindExpectedPage ?? textPageForFind + 1;
                sw.Restart();
                var find = await worker.SendAsync(new Request { Op = Op.Find, DocId = docId, PageIndex = -1, Query = word }, FindTimeout);
                result.FindMs = sw.Elapsed.TotalMilliseconds;
                result.FindWord = word;
                result.FindHits = find.Hits?.Count;
                var onPage = find.Hits?.Where(h => h.PageIndex == expected - 1).ToList() ?? [];
                result.FindOnExpectedPage = onPage.Count > 0;
                result.FindHasRects = onPage.Any(h => h.Rects.Any(r => r.Right > r.Left && r.Top > r.Bottom));
            }

            result.Outcome = crashes == 0 ? "ok" : "ok-with-restarts";
        }
        catch (WorkerTimeoutException ex)
        {
            result.Outcome = "timeout";
            result.Error = ex.Message;
        }
        catch (WorkerException ex)
        {
            result.Outcome = "crash";
            result.Error = ex.Message;
        }
        finally
        {
            result.PeakBytes = worker.PeakMemoryBytes ?? 0;
            result.Restarts = worker.Restarts;
        }
        return result;
    }

    static List<int> SamplePages(int count, bool all)
    {
        if (all || count <= SampleTarget) return Enumerable.Range(0, count).ToList();
        var set = new SortedSet<int> { 0, 1, 2, count / 2, count - 1 };
        for (var i = 0; set.Count < SampleTarget; i++) set.Add((int)((long)i * (count - 1) / (SampleTarget - 1)) % count);
        return set.ToList();
    }

    static string? PickWord(string? text) =>
        text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '"', '\'', '(', ')', '!', '?'))
            .FirstOrDefault(w => w.Length >= 7 && w.All(char.IsLetter));

    static string? ResolvePassword(ManifestEntry file)
    {
        if (file.Password is null) return null;
        if (file.Password.StartsWith("fixture:", StringComparison.Ordinal)) return file.Password["fixture:".Length..];
        if (file.Password != "prompt") throw new InvalidDataException($"{file.Path}: password must be \"prompt\" or \"fixture:<value>\".");

        Console.Write($"Password for {Path.GetFileName(file.FullPath)} (Enter to skip): ");
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return chars.Count == 0 ? null : new string(chars.ToArray());
    }

    static double? P(List<double> values, int percentile)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}

sealed class FileResult
{
    public string Outcome = "not-run";
    public string? Error;
    public int? Pages, Sampled, LabelsDefined, LabelsDiffer, FindHits;
    public int PagesWithoutText;
    public bool? Encrypted, CanCopy, CanPrint, FindOnExpectedPage, FindHasRects;
    public double? OpenColdMs, OpenWarmMs, TileMs, CharBoxesMs, FindMs;
    public string? FindWord;
    public long PeakBytes;
    public int Restarts;
    public readonly List<double> Render100 = [], Render200 = [], TextMs = [];
}

sealed record FileFacts(long Length, DateTime LastWriteUtc, string Sha256, double HashMs)
{
    public static FileFacts Read(string path)
    {
        var sw = Stopwatch.StartNew();
        var info = new FileInfo(path);
        if (!info.Exists) return new FileFacts(-1, default, "missing", 0);
        // Open read-only and allow others to read and write, so hashing never locks the user's file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return new FileFacts(info.Length, info.LastWriteTimeUtc, hash, sw.Elapsed.TotalMilliseconds);
    }
}
