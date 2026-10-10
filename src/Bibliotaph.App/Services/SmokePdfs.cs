using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bibliotaph.App.Services;

/// <summary>
/// Two made-up PDFs for the smoke test (slice 4i), built in memory by hand since the app has no PDF writer: one locked
/// with a password, and one protected by a security handler no reader has, as DRM looks to PDFium. Nothing in them
/// comes from a real book. The Pdf.Host tests compile this file too, to check PDFium reads them as the smoke test
/// expects.
/// </summary>
public static class SmokePdfs
{
    /// <summary>The locked book's open password. Made up, for the smoke test only.</summary>
    public const string Password = "smoke-test-only";

    /// <summary>What the locked book's one page says, which a search finds once it is unlocked.</summary>
    public const string LockedPhrase = "lantern beneath the locked gate";

    /// <summary>Permissions with every right but copying text out (bit 5 clear), as a store's PDF often has.</summary>
    const int NoCopying = -20;

    /// <summary>The padding string of the standard security handler (ISO 32000-1, 7.6.3.3).</summary>
    static readonly byte[] Padding =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    /// <summary>
    /// One page saying <see cref="LockedPhrase"/>, encrypted with the standard handler, revision 2 (40-bit RC4), so
    /// it opens only with <see cref="Password"/>; and it forbids copying text.
    /// </summary>
    public static byte[] Locked()
    {
        var id = FileId("locked");
        var owner = Rc4(Md5(Pad(Password + "-owner"))[..5], Pad(Password));
        var key = Md5([.. Pad(Password), .. owner, .. BitConverter.GetBytes(NoCopying), .. id])[..5];
        var user = Rc4(key, Padding);
        var content = Rc4(ObjectKey(key, 4), Latin1($"BT /F1 18 Tf 72 700 Td ({LockedPhrase}) Tj ET"));
        return Build(content, $"/Filter /Standard /V 1 /R 2 /Length 40 /O <{Convert.ToHexString(owner)}> /U <{Convert.ToHexString(user)}> /P {NoCopying}", id);
    }

    /// <summary>One page under a security handler that isn't the standard one: PDFium reports it as a security error.</summary>
    public static byte[] Protected() =>
        Build(Latin1("BT /F1 18 Tf 72 700 Td (A protected page) Tj ET"), "/Filter /BibliotaphSmokeRights /V 1 /Length 40", FileId("protected"));

    /// <summary>A catalog, one page, its content stream (object 4), a standard font and the encryption dictionary.</summary>
    static byte[] Build(byte[] content, string encryption, byte[] id)
    {
        var objects = new List<byte[]>
        {
            Latin1("<< /Type /Catalog /Pages 2 0 R >>"),
            Latin1("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Latin1("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>"),
            Stream(content),
            Latin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Latin1($"<< {encryption} >>"),
        };

        using var pdf = new System.IO.MemoryStream();
        pdf.Write(Latin1("%PDF-1.4\n%âãÏÓ\n"));
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            pdf.Write(Latin1($"{i + 1} 0 obj\n"));
            pdf.Write(objects[i]);
            pdf.Write(Latin1("\nendobj\n"));
        }
        var xref = pdf.Position;
        var table = new StringBuilder($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) table.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        var hex = Convert.ToHexString(id);
        table.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Encrypt {objects.Count} 0 R /ID [<{hex}> <{hex}>] >>\nstartxref\n{xref}\n%%EOF\n");
        pdf.Write(Latin1(table.ToString()));
        return pdf.ToArray();
    }

    static byte[] Stream(byte[] content) => [.. Latin1($"<< /Length {content.Length} >>\nstream\n"), .. content, .. Latin1("\nendstream")];

    static byte[] FileId(string name) => Md5(Latin1("bibliotaph-smoke-" + name));

    /// <summary>A password as the handler takes it: its first 32 bytes, padded out with <see cref="Padding"/>.</summary>
    static byte[] Pad(string password)
    {
        var bytes = Latin1(password);
        return [.. bytes.Take(32), .. Padding.Take(32 - Math.Min(32, bytes.Length))];
    }

    /// <summary>The key for one object's strings and streams (algorithm 1): the file key with its number and generation.</summary>
    static byte[] ObjectKey(byte[] key, int number) =>
        Md5([.. key, (byte)number, (byte)(number >> 8), (byte)(number >> 16), 0, 0])[..Math.Min(16, key.Length + 5)];

    // The PDF standard security handler is built on MD5 and RC4. This makes a made-up test file in that format; nothing
    // here protects anything.
#pragma warning disable CA5351
    static byte[] Md5(byte[] data) => MD5.HashData(data);
#pragma warning restore CA5351

    static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return output;
    }

    static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);
}
