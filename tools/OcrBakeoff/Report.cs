using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace OcrBakeoff;

sealed record EngineResult(string Text, double Milliseconds);

sealed record PageResult(int BookNumber, string Book, bool Scanned, int PageIndex, int Width, int Height, string? Reference)
{
    public Dictionary<string, EngineResult> Engines { get; } = [];
}

static partial class Words
{
    [GeneratedRegex(@"\p{L}[\p{L}'’]*")]
    private static partial Regex Word();

    /// <summary>Lower-cased words of two letters or more, apostrophes kept, digits and punctuation dropped.</summary>
    public static IEnumerable<string> Of(string text) =>
        Word().Matches(text).Select(m => m.Value.Replace('’', '\'').ToLowerInvariant()).Where(w => w.Length >= 2);

    /// <summary>Share of the reference's words (counted with repeats) that the OCR text also has.</summary>
    public static double Recall(string reference, string ocr)
    {
        var wanted = Of(reference).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var total = wanted.Values.Sum();
        if (total == 0) return double.NaN;
        var found = 0;
        foreach (var word in Of(ocr))
        {
            if (wanted.TryGetValue(word, out var left) && left > 0)
            {
                wanted[word] = left - 1;
                found++;
            }
        }
        return (double)found / total;
    }

    /// <summary>Share of the OCR text's words of three letters or more that are in the vocabulary.</summary>
    public static double KnownRate(string ocr, HashSet<string> vocabulary)
    {
        var words = Of(ocr).Where(w => w.Length >= 3).ToList();
        return words.Count == 0 || vocabulary.Count == 0 ? double.NaN : (double)words.Count(vocabulary.Contains) / words.Count;
    }
}

/// <summary>summary.csv, pages.csv, report.html, and per page the image and each engine's text.</summary>
sealed class Report(string folder, IReadOnlyList<string> engines)
{
    readonly List<(PageResult Page, Dictionary<string, double> Score, string Image)> _rows = [];

    public void Add(PageResult page, PageImage image, HashSet<string> vocabulary)
    {
        var pageFolder = Path.Combine(folder, $"book{page.BookNumber}", $"page-{page.PageIndex + 1:D4}");
        Directory.CreateDirectory(pageFolder);
        foreach (var (engine, result) in page.Engines)
            File.WriteAllText(Path.Combine(pageFolder, engine + ".txt"), result.Text);
        if (page.Reference is not null) File.WriteAllText(Path.Combine(pageFolder, "text-layer.txt"), page.Reference);
        var imagePath = Path.Combine(pageFolder, "page.png");
        File.WriteAllBytes(imagePath, Png.GreyThumbnail(image, 900));

        var score = page.Engines.ToDictionary(e => e.Key,
            e => page.Reference is not null ? Words.Recall(page.Reference, e.Value.Text) : Words.KnownRate(e.Value.Text, vocabulary));
        _rows.Add((page, score, Path.GetRelativePath(folder, imagePath).Replace('\\', '/')));
    }

    /// <summary>One line per book and engine: speed, and the score that kind of book allows.</summary>
    IEnumerable<string[]> SummaryRows()
    {
        foreach (var book in _rows.GroupBy(r => (r.Page.BookNumber, r.Page.Book, r.Page.Scanned)))
        {
            foreach (var engine in engines)
            {
                var ms = book.Select(r => r.Page.Engines[engine].Milliseconds).Where(double.IsFinite).Order().ToList();
                var scores = book.Select(r => r.Score[engine]).Where(double.IsFinite).ToList();
                yield return
                [
                    book.Key.Book,
                    book.Key.Scanned ? "scanned" : "digital",
                    engine,
                    book.Count().ToString(CultureInfo.InvariantCulture),
                    Format(Percentile(ms, 0.5), "F0"),
                    Format(Percentile(ms, 0.95), "F0"),
                    book.Key.Scanned ? "known words" : "words recalled",
                    scores.Count == 0 ? "" : Format(scores.Average() * 100, "F1") + "%",
                ];
            }
        }
    }

    static readonly string[] SummaryHeader = ["book", "kind", "engine", "pages", "ms_p50", "ms_p95", "score_kind", "score"];

    public string SummaryText()
    {
        var rows = SummaryRows().Prepend(SummaryHeader).ToList();
        var widths = Enumerable.Range(0, SummaryHeader.Length).Select(c => rows.Max(r => r[c].Length)).ToArray();
        return string.Join('\n', rows.Select(r => string.Join("  ", r.Select((cell, c) => cell.PadRight(widths[c])))));
    }

    public void Write()
    {
        File.WriteAllLines(Path.Combine(folder, "summary.csv"), SummaryRows().Prepend(SummaryHeader).Select(Csv));
        File.WriteAllLines(Path.Combine(folder, "pages.csv"),
            _rows.SelectMany(r => engines.Select(e => new[]
            {
                r.Page.Book, r.Page.Scanned ? "scanned" : "digital", (r.Page.PageIndex + 1).ToString(CultureInfo.InvariantCulture),
                $"{r.Page.Width}x{r.Page.Height}", e, Format(r.Page.Engines[e].Milliseconds, "F1"), Format(r.Score[e], "F3"),
            })).Prepend(["book", "kind", "pdf_page", "pixels", "engine", "ms", "score"]).Select(Csv));
        File.WriteAllText(Path.Combine(folder, "report.html"), Html());
    }

    string Html()
    {
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><meta charset="utf-8"><title>OCR bake-off</title>
            <style>
              body { font: 14px system-ui, sans-serif; margin: 24px; }
              table { border-collapse: collapse; } td, th { border: 1px solid #ccc; padding: 4px 8px; text-align: left; }
              .page { display: grid; grid-template-columns: 450px repeat(auto-fit, minmax(260px, 1fr)); gap: 12px; margin: 24px 0; }
              .page img { width: 450px; border: 1px solid #ccc; }
              pre { white-space: pre-wrap; font: 12px ui-monospace, monospace; background: #f6f6f3; padding: 8px; margin: 4px 0 0; max-height: 640px; overflow: auto; }
            </style>
            <h1>OCR bake-off</h1>
            <p>Scanned books are scored by the share of recognised words found in the digital books' vocabulary; digital books by the share of their text layer's words the engine recovered. Times are per page, inside the engine.</p>
            """);
        html.Append("<table><tr>").AppendJoin("", SummaryHeader.Select(h => $"<th>{h}</th>")).Append("</tr>");
        foreach (var row in SummaryRows())
            html.Append("<tr>").AppendJoin("", row.Select(c => $"<td>{WebUtility.HtmlEncode(c)}</td>")).Append("</tr>");
        html.Append("</table>");

        foreach (var (page, score, image) in _rows)
        {
            html.Append(CultureInfo.InvariantCulture, $"<h2>{WebUtility.HtmlEncode(page.Book)}, PDF page {page.PageIndex + 1}</h2><div class=\"page\"><img src=\"{image}\" alt=\"\">");
            if (page.Reference is not null)
                html.Append("<div><b>Text layer</b><pre>").Append(WebUtility.HtmlEncode(page.Reference)).Append("</pre></div>");
            foreach (var engine in engines)
            {
                var result = page.Engines[engine];
                html.Append(CultureInfo.InvariantCulture, $"<div><b>{engine}</b> {Format(result.Milliseconds, "F0")} ms, score {Format(score[engine] * 100, "F1")}%<pre>")
                    .Append(WebUtility.HtmlEncode(result.Text)).Append("</pre></div>");
            }
            html.Append("</div>");
        }
        return html.ToString();
    }

    static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];

    static string Format(double value, string format) => double.IsFinite(value) ? value.ToString(format, CultureInfo.InvariantCulture) : "";

    static string Csv(string[] cells) => string.Join(',', cells.Select(c => c.Contains(',') || c.Contains('"') ? "\"" + c.Replace("\"", "\"\"") + "\"" : c));
}

/// <summary>A minimal greyscale PNG writer, so the report needs no imaging library.</summary>
static class Png
{
    static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] GreyThumbnail(PageImage page, int maxWidth)
    {
        var factor = Math.Max(1, (int)Math.Ceiling(page.Width / (double)maxWidth));
        var width = page.Width / factor;
        var height = page.Height / factor;
        var grey = new byte[page.Width * page.Height];
        Grey.Fill(page, grey, page.Width);

        // Box-filter down by an integer factor; each PNG row starts with filter type 0.
        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                    for (var dx = 0; dx < factor; dx++)
                        sum += grey[(y * factor + dy) * page.Width + x * factor + dx];
                raw[y * (width + 1) + 1 + x] = (byte)(sum / (factor * factor));
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; // bit depth; colour type 0 (grey), default compression, filter and interlace
        Chunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
            Chunk(output, "IDAT", compressed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        output.Write(length);
        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData, 4);
        output.Write(typeAndData);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc(typeAndData));
        output.Write(crc);
    }

    static uint Crc(byte[] bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    static void WriteBigEndian(byte[] buffer, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), value);
}
