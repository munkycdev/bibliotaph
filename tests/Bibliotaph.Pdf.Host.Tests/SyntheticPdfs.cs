using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Bibliotaph.Pdf.Host.Tests;

/// <summary>
/// Builds the test PDFs at test time, so no fixture cut from a purchased book ever goes to GitHub.
/// Each file is written once per test run into a temp folder.
/// </summary>
public sealed class SyntheticPdfs : IDisposable
{
    public const string Password = "bibliotaph-test-only";
    public const string KnownPhrase = "owlbear waits beneath the old mill";
    public const int KnownPhrasePage = 1;
    public const int ImageOnlyPage = 2;
    public const int PageCount = 3;

    static readonly Lock FontLock = new();

    public SyntheticPdfs()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("bibliotaph-pdfs-").FullName;
        EnsureFontResolver();

        var known = Build(encrypt: false);
        KnownText = Write("known-text.pdf", known);
        PasswordProtected = Write("password-aes256.pdf", Build(encrypt: true));
        Truncated = Write("truncated.pdf", known[..(known.Length / 2)]);
        BrokenXref = Write("broken-xref.pdf", BreakXref(known));
        NonAsciiName = Write(NonAsciiFileName, known);
        Scanned = Write("scanned.pdf", BuildScanned());
    }

    /// <summary>Characters outside Windows' ANSI code page, as bundle file names often have.</summary>
    public const string NonAsciiFileName = "Ryoko\u2019s Guide \u2014 \u00e9t\u00e9 \u5996\u602a.pdf";

    public const string Title = "Bibliotaph synthetic fixture";
    public const string Author = "Test Author";

    public string Directory { get; }

    /// <summary>Three pages, an outline and document info: a contents page, <see cref="KnownPhrase"/> on page 1, and an image-only page 2.</summary>
    public string KnownText { get; }

    /// <summary>The same document, AES-256 encrypted with <see cref="Password"/> as the open password.</summary>
    public string PasswordProtected { get; }

    /// <summary>The first half of <see cref="KnownText"/>'s bytes.</summary>
    public string Truncated { get; }

    /// <summary><see cref="KnownText"/>, saved under <see cref="NonAsciiFileName"/>.</summary>
    public string NonAsciiName { get; }

    /// <summary>
    /// One page showing <see cref="ScannedPhrase"/> as filled glyph outlines: it reads like text on screen but has no
    /// text layer, which is how a scan looks to the indexer.
    /// </summary>
    public string Scanned { get; }

    /// <summary><see cref="KnownText"/> with its startxref pointing at the wrong place, which PDFium repairs.</summary>
    public string BrokenXref { get; }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

    string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static byte[] Build(bool encrypt)
    {
        using var document = new PdfDocument();
        document.Info.Title = Title;
        document.Info.Author = Author;
        var font = new XFont(EmbeddedFontResolver.Family, 14);

        var contents = document.AddPage();
        using (var gfx = XGraphics.FromPdfPage(contents))
            gfx.DrawString("Contents and credits", font, XBrushes.Black, new XPoint(72, 96));

        var text = document.AddPage();
        using (var gfx = XGraphics.FromPdfPage(text))
        {
            gfx.DrawString("Chapter one", font, XBrushes.Black, new XPoint(72, 96));
            gfx.DrawString($"The {KnownPhrase} until midnight.", font, XBrushes.Black, new XPoint(72, 130));
        }

        var image = document.AddPage();
        using (var gfx = XGraphics.FromPdfPage(image))
        using (var stream = new MemoryStream(Bitmap(200, 120)))
        using (var picture = XImage.FromStream(stream))
            gfx.DrawImage(picture, 72, 72, 400, 240);

        // Outline: one chapter with one child, pointing at pages 1 and 2.
        var chapter = document.Outlines.Add("Chapter one", text);
        chapter.Outlines.Add("A picture", image);

        if (encrypt)
        {
            document.SecuritySettings.UserPassword = Password;
            document.SecuritySettings.OwnerPassword = Password + "-owner";
            document.SecurityHandler.SetEncryptionToV5();
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    public const string ScannedPhrase = "Goblin market";

    static byte[] BuildScanned()
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var path = new XGraphicsPath { FillMode = XFillMode.Winding };
            new GlyphOutlines(EmbeddedFontResolver.FontBytes()).AddString(path, ScannedPhrase, 40, new XPoint(72, 160));
            gfx.DrawPath(XBrushes.Black, path);
        }
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    /// <summary>Overwrites the startxref offset with one that points nowhere useful.</summary>
    static byte[] BreakXref(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var at = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException("PDFsharp output has no startxref.");
        var digitsStart = at + "startxref".Length;
        while (!char.IsAsciiDigit(text[digitsStart])) digitsStart++;
        var digitsEnd = digitsStart;
        while (char.IsAsciiDigit(text[digitsEnd])) digitsEnd++;

        var broken = (byte[])pdf.Clone();
        for (var i = digitsStart; i < digitsEnd; i++) broken[i] = (byte)'1';
        return broken;
    }

    /// <summary>A 24-bit BMP with a diagonal gradient: pixels, no text.</summary>
    static byte[] Bitmap(int width, int height)
    {
        var stride = (width * 3 + 3) & ~3;
        var pixelBytes = stride * height;
        var bmp = new byte[54 + pixelBytes];
        var w = new BinaryWriter(new MemoryStream(bmp));
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(bmp.Length); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24);
        w.Write(0); w.Write(pixelBytes); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = 54 + y * stride + x * 3;
                bmp[o] = (byte)(x * 255 / width);
                bmp[o + 1] = (byte)(y * 255 / height);
                bmp[o + 2] = 90;
            }
        }
        return bmp;
    }

    static void EnsureFontResolver()
    {
        lock (FontLock)
        {
            if (GlobalFontSettings.FontResolver is not EmbeddedFontResolver)
                GlobalFontSettings.FontResolver = new EmbeddedFontResolver();
        }
    }

    /// <summary>Every font request gets DM Sans, embedded in this assembly, so tests need no system fonts.</summary>
    sealed class EmbeddedFontResolver : IFontResolver
    {
        public const string Family = "DM Sans";
        const string FaceName = "DMSans-Regular";

        public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new(FaceName);

        public byte[] GetFont(string faceName) => FontBytes();

        public static byte[] FontBytes()
        {
            using var stream = typeof(SyntheticPdfs).Assembly.GetManifestResourceStream("DMSans-Regular.ttf")
                ?? throw new InvalidOperationException("DMSans-Regular.ttf is not embedded in the test assembly.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
    }
}
