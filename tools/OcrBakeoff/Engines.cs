using System.Runtime.InteropServices.WindowsRuntime;
using Tesseract;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace OcrBakeoff;

interface IOcrCandidate : IDisposable
{
    string Name { get; }
    string Description { get; }

    /// <summary>The largest width or height the engine accepts.</summary>
    int MaxImageDimension { get; }

    Task<string> RecognizeAsync(PageImage page);

    /// <summary>Recognises a small blank image, so model loading isn't timed against the first page.</summary>
    async Task WarmUpAsync()
    {
        var pixels = new byte[200 * 100 * 4];
        Array.Fill(pixels, (byte)0xFF);
        await RecognizeAsync(new PageImage(pixels, 200, 100, 800));
    }
}

/// <summary>Windows.Media.Ocr: ships with Windows, uses the user's installed OCR languages.</summary>
sealed class WindowsOcr : IOcrCandidate
{
    readonly OcrEngine _engine;

    public WindowsOcr()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR has no language installed for this user (Settings > Time & language > Language).");
    }

    public string Name => "windows";
    public string Description => $"Windows.Media.Ocr, {_engine.RecognizerLanguage.DisplayName}";
    public int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    public async Task<string> RecognizeAsync(PageImage page)
    {
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(page.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            page.Width, page.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(bitmap);
        return string.Join('\n', result.Lines.Select(l => l.Text));
    }

    public void Dispose() { }
}

/// <summary>Tesseract 5 LSTM with the tessdata_fast or tessdata_best English model.</summary>
sealed class TesseractOcr : IOcrCandidate
{
    readonly TesseractEngine _engine;
    readonly string _model;

    TesseractOcr(TesseractEngine engine, string model)
    {
        _engine = engine;
        _model = model;
    }

    public string Name => "tesseract-" + _model;
    public string Description => $"Tesseract {_engine.Version}, tessdata_{_model} eng";
    public int MaxImageDimension => 32767;

    public static async Task<IOcrCandidate> CreateAsync(string tessDataRoot, string model)
    {
        var folder = Path.Combine(tessDataRoot, model);
        var file = Path.Combine(folder, "eng.traineddata");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(folder);
            var url = $"https://github.com/tesseract-ocr/tessdata_{model}/raw/main/eng.traineddata";
            Console.WriteLine($"Downloading {url}");
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(file, bytes);
        }
        return new TesseractOcr(new TesseractEngine(folder, "eng", EngineMode.LstmOnly), model);
    }

    public Task<string> RecognizeAsync(PageImage page)
    {
        using var pix = Pix.LoadFromMemory(Bmp.GreyBitmap(page));
        using var result = _engine.Process(pix, PageSegMode.Auto);
        return Task.FromResult(result.GetText().Trim());
    }

    public void Dispose() => _engine.Dispose();
}

static class Bmp
{
    /// <summary>An 8-bit greyscale, bottom-up BMP of the page, which Leptonica reads directly.</summary>
    public static byte[] GreyBitmap(PageImage page)
    {
        var stride = (page.Width + 3) & ~3;
        const int HeaderBytes = 14 + 40 + 256 * 4;
        var bmp = new byte[HeaderBytes + stride * page.Height];
        using var w = new BinaryWriter(new MemoryStream(bmp));
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(bmp.Length); w.Write(0); w.Write(HeaderBytes);
        w.Write(40); w.Write(page.Width); w.Write(page.Height); w.Write((short)1); w.Write((short)8);
        w.Write(0); w.Write(stride * page.Height); w.Write(11811); w.Write(11811); w.Write(256); w.Write(0);
        for (var i = 0; i < 256; i++) { w.Write((byte)i); w.Write((byte)i); w.Write((byte)i); w.Write((byte)0); }
        Grey.Fill(page, bmp.AsSpan(HeaderBytes), stride, bottomUp: true);
        return bmp;
    }
}

static class Grey
{
    /// <summary>BGRA to luminance, row by row, into a buffer with the given stride.</summary>
    public static void Fill(PageImage page, Span<byte> target, int stride, bool bottomUp = false)
    {
        for (var y = 0; y < page.Height; y++)
        {
            var row = page.Pixels.AsSpan(y * page.Stride, page.Width * 4);
            var output = target.Slice((bottomUp ? page.Height - 1 - y : y) * stride, page.Width);
            for (var x = 0; x < page.Width; x++)
                output[x] = (byte)((row[x * 4] * 29 + row[x * 4 + 1] * 150 + row[x * 4 + 2] * 77) >> 8);
        }
    }
}
