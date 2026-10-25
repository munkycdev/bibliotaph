using System.IO.Compression;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Spike.Bench;

// Made-up PDFs for the browser reading and caching checks, sized like Dave's library: median about 2 MB, 90th
// percentile about 49 MB. Nothing here comes from a real book.
// Usage: Spike.Corpus <folder> <font.ttf>
var folder = args[0];
GlobalFontSettings.FontResolver = new OneFont(File.ReadAllBytes(args[1]));
Directory.CreateDirectory(folder);
Write("text-40.pdf", pages: 40, imageEvery: 0);
Write("text-300.pdf", pages: 300, imageEvery: 0);
Write("illustrated-120.pdf", pages: 120, imageEvery: 1);

void Write(string name, int pages, int imageEvery)
{
    var random = new Random(name.Length * 7919 + pages);
    using var document = new PdfDocument();
    document.Info.Title = name;
    var font = new XFont("Spike", 10);
    for (var p = 0; p < pages; p++)
    {
        var page = document.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        var y = 60.0;
        if (imageEvery > 0 && p % imageEvery == 0)
        {
            using var image = XImage.FromStream(new MemoryStream(NoisePng(random, 360, 360)));
            gfx.DrawImage(image, 72, y, 200, 200);
            y += 216;
        }
        foreach (var line in Wrap(Text.Page(random), 95))
        {
            if (y > page.Height.Point - 60) break;
            gfx.DrawString(line, font, XBrushes.Black, new XPoint(72, y));
            y += 12.5;
        }
        gfx.DrawString((p + 1).ToString(), font, XBrushes.Black, new XPoint(page.Width.Point / 2, page.Height.Point - 30));
    }
    var path = Path.Combine(folder, name);
    document.Save(path);
    Console.WriteLine($"{name}: {pages} pages, {new FileInfo(path).Length / 1e6:0.0} MB");
}

static IEnumerable<string> Wrap(string text, int width)
{
    var line = new StringBuilder();
    foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.Length + word.Length + 1 > width) { yield return line.ToString(); line.Clear(); }
        if (line.Length > 0) line.Append(' ');
        line.Append(word);
    }
    if (line.Length > 0) yield return line.ToString();
}

// Random pixels, so the picture doesn't compress: it stands in for the art that makes RPG books large.
static byte[] NoisePng(Random random, int width, int height)
{
    var raw = new byte[height * (width * 3 + 1)];
    random.NextBytes(raw);
    for (var row = 0; row < height; row++) raw[row * (width * 3 + 1)] = 0;
    using var png = new MemoryStream();
    png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    var header = new byte[13];
    WriteInt(header, 0, width);
    WriteInt(header, 4, height);
    header[8] = 8;
    header[9] = 2;
    Chunk(png, "IHDR", header);
    using (var data = new MemoryStream())
    {
        using (var z = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        Chunk(png, "IDAT", data.ToArray());
    }
    Chunk(png, "IEND", []);
    return png.ToArray();
}

static void Chunk(Stream png, string type, byte[] data)
{
    var length = new byte[4];
    WriteInt(length, 0, data.Length);
    png.Write(length);
    var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
    png.Write(body);
    var crc = new byte[4];
    WriteInt(crc, 0, (int)System.IO.Hashing.Crc32.HashToUInt32(body));
    png.Write(crc);
}

static void WriteInt(byte[] buffer, int offset, int value)
{
    buffer[offset] = (byte)(value >> 24);
    buffer[offset + 1] = (byte)(value >> 16);
    buffer[offset + 2] = (byte)(value >> 8);
    buffer[offset + 3] = (byte)value;
}

sealed class OneFont(byte[] font) : IFontResolver
{
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new("spike");
    public byte[] GetFont(string faceName) => font;
}
