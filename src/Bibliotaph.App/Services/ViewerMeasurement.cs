using System.Diagnostics;
using System.Windows.Threading;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Viewer;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// <c>Bibliotaph.exe --measure-viewer</c>: opens up to <see cref="MaxDocuments"/> PDFs from the library in the viewer
/// worker and renders a spread of their pages the way the viewer does (a quarter-scale preview, the fit-width page,
/// and 512-pixel tiles at 300%), then logs p50 and p95 against the gates in the architecture doc. Books are named by
/// their document id only, so the log carries no titles.
/// </summary>
static class ViewerMeasurement
{
    const int MaxDocuments = 20;
    const int PagesPerDocument = 8;
    const double FitWidthPixels = 1100;   // a fit-width page in a maximized window on a 1080p screen
    const double PreviewFraction = 0.25;
    const double TileZoom = 3.0;
    const int TileSize = 512;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var library = services.GetRequiredService<LibraryStore>();
        var queries = services.GetRequiredService<LibraryQueries>();
        var vault = services.GetRequiredService<IPasswordVault>();
        // Given back after the renderer below has closed its document.
        await using var lease = services.GetRequiredService<PdfWorkerPool>().LeaseViewer();
        var worker = lease.Worker;
        var previews = new List<double>();
        var digital = new List<double>();
        var scanned = new List<double>();
        var tiles = new List<double>();
        try
        {
            var visible = await library.GetVisibleEntryIdsAsync();
            var pdfs = await Task.Run(() => queries.ListAsync(new LibraryFilter(visible, KindFilter.Books, LibrarySort.RecentlyAdded)));
            Log.Information("Measuring the viewer on up to {Documents} of {Pdfs} PDFs, {Pages} pages each", MaxDocuments, pdfs.Count, PagesPerDocument);

            await using var renderer = new PdfRenderer(worker, Dispatcher.CurrentDispatcher);
            var measured = 0;
            foreach (var entry in pdfs)
            {
                if (measured == MaxDocuments) break;
                // A book inside a ZIP would time its extraction too.
                if (await library.GetSourceAsync(entry.DocumentId) is not { InArchive: false } source) continue;
                DocInfo doc;
                try
                {
                    doc = await renderer.OpenAsync(source.FullPath, vault.Find(source.ContentHash));
                }
                catch (PdfOpenException ex)
                {
                    Log.Information("Viewer: skipped document {DocumentId} ({Reason})", entry.DocumentId, ex.Kind);
                    continue;
                }
                measured++;
                var (fromPreview, fromDigital, fromScanned, fromTile) = (previews.Count, digital.Count, scanned.Count, tiles.Count);
                foreach (var page in Spread(doc.PageCount))
                {
                    var size = page < doc.PageSizes.Count && doc.PageSizes[page].Width > 0 ? doc.PageSizes[page] : new PageSize(612, 792);
                    var scale = FitWidthPixels / size.Width;
                    // A page with no text of its own is a scan; its gate is looser.
                    var isScan = !(await renderer.GetTextAsync(page)).Text.Any(char.IsLetterOrDigit);
                    previews.Add(await TimeAsync(() => renderer.RenderBitmapAsync(page, scale * PreviewFraction)));
                    (isScan ? scanned : digital).Add(await TimeAsync(() => renderer.RenderBitmapAsync(page, scale)));
                    var tileScale = TileZoom * 96 / 72;
                    var tile = new PixelRect(Math.Max(0, (int)(size.Width * tileScale / 2) - TileSize / 2),
                        Math.Max(0, (int)(size.Height * tileScale / 2) - TileSize / 2), TileSize, TileSize);
                    tiles.Add(await TimeAsync(() => renderer.RenderBitmapAsync(page, tileScale, tile)));
                }
                Log.Information("Viewer: document {DocumentId}, {PageCount} pages: preview p95 {Preview:F0} ms, page p95 {Page:F0} ms, tile p95 {Tile:F0} ms",
                    entry.DocumentId, doc.PageCount, Percentile(previews[fromPreview..], 0.95),
                    Percentile([.. digital[fromDigital..], .. scanned[fromScanned..]], 0.95), Percentile(tiles[fromTile..], 0.95));
            }
            await renderer.CloseAsync();

            Gate("Preview (first pixels on a blank page)", previews, 250);
            Gate("Page, digital", digital, 150);
            Gate("Page, scanned", scanned, 400);
            Gate("Tile at 300%", tiles, 300);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Measuring the viewer failed");
            return 1;
        }
    }

    /// <summary>Up to <see cref="PagesPerDocument"/> pages spread through the book, first and last included.</summary>
    static IEnumerable<int> Spread(int pageCount)
    {
        if (pageCount <= 0) return [];
        var count = Math.Min(PagesPerDocument, pageCount);
        return count == 1 ? [0] : Enumerable.Range(0, count).Select(i => (int)Math.Round(i * (pageCount - 1) / (double)(count - 1))).Distinct();
    }

    static async Task<double> TimeAsync(Func<Task> render)
    {
        var started = Stopwatch.GetTimestamp();
        await render();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    static void Gate(string what, List<double> times, double targetMs)
    {
        if (times.Count == 0)
        {
            Log.Information("Viewer {What}: nothing measured", what);
            return;
        }
        var p95 = Percentile(times, 0.95);
        Log.Information("Viewer {What}: p50 {P50:F0} ms, p95 {P95:F0} ms over {Count} renders; target p95 under {Target:F0} ms: {Verdict}",
            what, Percentile(times, 0.5), p95, times.Count, targetMs, p95 < targetMs ? "PASS" : "FAIL");
    }

    /// <summary>Nearest-rank percentile.</summary>
    static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
