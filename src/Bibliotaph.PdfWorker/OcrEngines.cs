using System.Runtime.InteropServices;
#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
#else
using System.Globalization;
using System.Text;
#endif

namespace Bibliotaph.PdfWorker;

/// <summary>Reads text from a rendered page. Implementations are created lazily and used from one thread.</summary>
interface IOcrEngine
{
    string Name { get; }

    /// <summary>The largest width or height in pixels the engine accepts.</summary>
    int MaxImageDimension { get; }

    /// <summary>Words with boxes in pixels of the given bitmap (BGRA, top-down), and the text line by line.</summary>
    (string Text, List<(string Word, double X, double Y, double Width, double Height)> Words) Recognize(IntPtr bgra, int width, int height, int stride);
}

static class OcrEngines
{
    /// <summary>Windows OCR in the user's languages, or null with the reason when it can't run here.</summary>
    public static IOcrEngine? Create(out string? reason)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            reason = "OCR needs Windows 10 version 1809 or later.";
            return null;
        }
        var engine = WindowsOcrEngine.TryCreate();
        reason = engine is null ? "Windows OCR has no language installed for this user (Settings > Time & language > Language & region)." : null;
        return engine;
#else
        return TesseractOcrEngine.TryCreate(out reason);
#endif
    }
}

#if WINDOWS

/// <summary>Windows.Media.Ocr: ships with Windows, so Bibliotaph packages no OCR binaries or language data.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows10.0.17763.0")]
sealed class WindowsOcrEngine : IOcrEngine
{
    readonly OcrEngine _engine;

    WindowsOcrEngine(OcrEngine engine) => _engine = engine;

    public static WindowsOcrEngine? TryCreate() =>
        OcrEngine.TryCreateFromUserProfileLanguages() is { } engine ? new WindowsOcrEngine(engine) : null;

    public string Name => $"Windows OCR ({_engine.RecognizerLanguage.LanguageTag})";

    public int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    public (string Text, List<(string Word, double X, double Y, double Width, double Height)> Words) Recognize(IntPtr bgra, int width, int height, int stride)
    {
        var pixels = new byte[width * 4 * height];
        for (var y = 0; y < height; y++) Marshal.Copy(bgra + y * stride, pixels, y * width * 4, width * 4);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        // The worker handles one request at a time on one thread, so blocking here is the simple, correct choice.
        var result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        var words = new List<(string, double, double, double, double)>();
        foreach (var line in result.Lines)
            foreach (var word in line.Words)
                words.Add((word.Text, word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height));
        return (string.Join('\n', result.Lines.Select(l => l.Text)), words);
    }
}
#endif

#if !WINDOWS

/// <summary>
/// Tesseract (Apache-2.0) through its C API, for the web server's Linux image, which installs it with its English
/// data. Loaded once and kept, so its model isn't read again for every page.
/// </summary>
sealed partial class TesseractOcrEngine : IOcrEngine, IDisposable
{
    const string Library = "libtesseract.so.5";
    const string Language = "eng";

    IntPtr _api;

    TesseractOcrEngine(IntPtr api) => _api = api;

    public static TesseractOcrEngine? TryCreate(out string? reason)
    {
        IntPtr api;
        try
        {
            api = TessBaseAPICreate();
        }
        catch (DllNotFoundException)
        {
            reason = "Tesseract isn't installed on this server.";
            return null;
        }

        // Null data path: Tesseract's own default, or TESSDATA_PREFIX when it's set.
        if (TessBaseAPIInit3(api, null, Language) != 0)
        {
            TessBaseAPIDelete(api);
            reason = $"Tesseract has no {Language} language data on this server.";
            return null;
        }
        reason = null;
        return new TesseractOcrEngine(api);
    }

    public string Name => $"Tesseract ({Language})";

    /// <summary>Tesseract has no limit of its own; this keeps a poster-sized page from taking the worker's memory.</summary>
    public int MaxImageDimension => 10000;

    public (string Text, List<(string Word, double X, double Y, double Width, double Height)> Words) Recognize(IntPtr bgra, int width, int height, int stride)
    {
        // Grey, worked out here: given four bytes a pixel, Tesseract would read PDFium's BGRA as RGBA.
        var grey = new byte[width * height];
        unsafe
        {
            var source = (byte*)bgra;
            for (var y = 0; y < height; y++)
            {
                var row = source + (long)y * stride;
                for (var x = 0; x < width; x++)
                {
                    var pixel = row + x * 4;
                    grey[y * width + x] = (byte)((pixel[2] * 299 + pixel[1] * 587 + pixel[0] * 114) / 1000);
                }
            }
        }

        TessBaseAPISetImage(_api, grey, width, height, 1, width);
        // The page is rendered at the stage's dpi; without it Tesseract guesses, and warns.
        TessBaseAPISetSourceResolution(_api, 300);
        var tsv = TessBaseAPIGetTsvText(_api, 0);
        try
        {
            return tsv == IntPtr.Zero ? (string.Empty, []) : FromTsv(Marshal.PtrToStringUTF8(tsv) ?? string.Empty);
        }
        finally
        {
            if (tsv != IntPtr.Zero) TessDeleteText(tsv);
            TessBaseAPIClear(_api);
        }
    }

    /// <summary>
    /// Tesseract's TSV: one row per block, paragraph, line and word (level 5), with its box in pixels from the top left
    /// and its text last. A line's words, in order, make a line of the text.
    /// </summary>
    internal static (string Text, List<(string Word, double X, double Y, double Width, double Height)> Words) FromTsv(string tsv)
    {
        var words = new List<(string, double, double, double, double)>();
        var text = new StringBuilder();
        (string, string, string)? line = null;
        foreach (var row in tsv.Split('\n'))
        {
            var cells = row.TrimEnd('\r').Split('\t');
            if (cells.Length < 12 || cells[0] != "5") continue;
            var word = cells[11].Trim();
            if (word.Length == 0) continue;

            var thisLine = (cells[2], cells[3], cells[4]);
            if (line is { } last && last != thisLine) text.Append('\n');
            else if (line is not null) text.Append(' ');
            line = thisLine;
            text.Append(word);
            words.Add((word, Number(cells[6]), Number(cells[7]), Number(cells[8]), Number(cells[9])));
        }
        return (text.ToString(), words);
    }

    static double Number(string cell) => double.Parse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture);

    public void Dispose()
    {
        if (_api == IntPtr.Zero) return;
        TessBaseAPIEnd(_api);
        TessBaseAPIDelete(_api);
        _api = IntPtr.Zero;
    }

    [LibraryImport(Library)]
    private static partial IntPtr TessBaseAPICreate();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int TessBaseAPIInit3(IntPtr handle, string? dataPath, string language);

    [LibraryImport(Library)]
    private static partial void TessBaseAPISetImage(IntPtr handle, byte[] imageData, int width, int height, int bytesPerPixel, int bytesPerLine);

    [LibraryImport(Library)]
    private static partial void TessBaseAPISetSourceResolution(IntPtr handle, int ppi);

    [LibraryImport(Library)]
    private static partial IntPtr TessBaseAPIGetTsvText(IntPtr handle, int pageNumber);

    [LibraryImport(Library)]
    private static partial void TessDeleteText(IntPtr text);

    [LibraryImport(Library)]
    private static partial void TessBaseAPIClear(IntPtr handle);

    [LibraryImport(Library)]
    private static partial void TessBaseAPIEnd(IntPtr handle);

    [LibraryImport(Library)]
    private static partial void TessBaseAPIDelete(IntPtr handle);
}
#endif
