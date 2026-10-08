using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

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
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            reason = "OCR needs Windows 10 version 1809 or later.";
            return null;
        }
        var engine = WindowsOcrEngine.TryCreate();
        reason = engine is null ? "Windows OCR has no language installed for this user (Settings > Time & language > Language & region)." : null;
        return engine;
    }
}

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
