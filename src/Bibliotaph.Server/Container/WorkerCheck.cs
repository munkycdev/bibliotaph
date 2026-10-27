using System.Globalization;
using System.Text;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;

namespace Bibliotaph.Server.Container;

/// <summary>
/// "worker-check": the server's PDF worker, as the image starts it, reads a book in its folder, text and OCR, and can't
/// open one in a library's folder. CI runs it against the built image, where the paths are the real ones; it exits 1
/// when the worker isn't confined or reads what it shouldn't.
/// </summary>
public static class WorkerCheck
{
    const string Phrase = "BIBLIOTAPH WORKER CHECK";
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var paths = services.GetRequiredService<ServerPaths>();
        var worker = services.GetRequiredService<PdfWorkerPool>().Index;
        var book = Pdf();
        var inside = Path.Combine(paths.Downloads, "worker-check.pdf");
        var outside = Path.Combine(paths.Users, "worker-check.pdf");
        Directory.CreateDirectory(paths.Users);
        Directory.CreateDirectory(paths.Downloads);
#pragma warning disable RS0030 // A made-up book of the check's own, never a user's file.
        File.WriteAllBytes(inside, book);
        File.WriteAllBytes(outside, book);
#pragma warning restore RS0030
        try
        {
            var ping = await worker.SendAsync(new Request { Op = Op.Ping }, Timeout);
            if (!Check("the worker answers", ping.Ok, ping.Message)) return 1;
            if (!Check("the worker is confined", ping.Message?.StartsWith("Confined", StringComparison.Ordinal) == true, ping.Message)) return 1;

            var open = await worker.SendAsync(new Request { Op = Op.Open, Path = inside }, Timeout);
            if (!Check("it opens a book being read", open.Ok, open.Message)) return 1;
            var text = await worker.SendAsync(new Request { Op = Op.ExtractText, DocId = open.Doc!.DocId, PageIndex = 0 }, Timeout);
            if (!Check("it reads the book's text", text.Ok && text.Text!.Text.Contains(Phrase, StringComparison.Ordinal), text.Message)) return 1;
            var ocr = await worker.SendAsync(new Request { Op = Op.Ocr, DocId = open.Doc.DocId, PageIndex = 0, Scale = 300 / 72.0 }, Timeout);
            if (!Check("it reads the page by OCR", ocr.Ok && ocr.Ocr!.Text.Contains("WORKER", StringComparison.OrdinalIgnoreCase), ocr.Message ?? ocr.Ocr?.Text)) return 1;

            var library = await worker.SendAsync(new Request { Op = Op.Open, Path = outside }, Timeout);
            if (!Check("it can't open a file in the users folder", !library.Ok, library.Message)) return 1;
            return 0;
        }
        finally
        {
#pragma warning disable RS0030
            File.Delete(inside);
            File.Delete(outside);
#pragma warning restore RS0030
            await services.GetRequiredService<PdfWorkerPool>().DisposeAsync();
        }
    }

    static bool Check(string what, bool passed, string? detail)
    {
        Console.WriteLine($"{(passed ? "ok  " : "FAIL")} {what}{(detail is { Length: > 0 } ? $": {detail}" : "")}");
        return passed;
    }

    /// <summary>One page with the phrase in large Helvetica, written out with its cross-reference table.</summary>
    static byte[] Pdf()
    {
        var content = $"BT /F1 36 Tf 40 700 Td ({Phrase}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
