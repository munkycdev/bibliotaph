using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// <c>Bibliotaph.exe --screenshots &lt;folder&gt; --screenshot-files &lt;folder&gt; --data-root &lt;folder&gt;
/// [--screenshot-open "&lt;title&gt;|&lt;page&gt;"]</c>: the README's pictures. Adds the files folder (public-domain books,
/// fetched by tools\Screenshots\get-books.ps1) to an empty library, waits for it to be indexed, then saves the Library
/// as library.png and a book open in the reader as reader.png, in the light theme at 1440 by 900. Exits 0, or 1 on
/// any exception. A second run on the same data root skips straight to the pictures.
/// </summary>
static class Screenshots
{
    const int Width = 1440;
    const int Height = 900;
    static readonly TimeSpan IndexingLimit = TimeSpan.FromMinutes(30);

    public static async Task<int> RunAsync(IServiceProvider services, Window window, string output, string? files, string? open)
    {
        try
        {
            System.IO.Directory.CreateDirectory(output);
            await services.GetRequiredService<ThemeService>().SetPreferenceAsync(ThemePreference.Light);
            window.WindowState = WindowState.Normal;
            window.Width = Width;
            window.Height = Height;

            var roots = services.GetRequiredService<SourceRootStore>();
            var indexing = services.GetRequiredService<IndexingService>();
            if ((await roots.ListAsync()).Count == 0)
            {
                if (files is null) throw new InvalidOperationException("The library is empty: give --screenshot-files.");
                indexing.RequestScan((await roots.AddAsync(files)).Id);
            }
            await WaitForIndexingAsync(services.GetRequiredService<LibraryActivity>());

            var navigation = services.GetRequiredService<INavigationService>();
            var shell = services.GetRequiredService<ShellViewModel>();
            navigation.NavigateTo(Route.Library);
            var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
            await WaitAsync(window, () => library.Items.Count > 0, "The Library listed no books.");
            await SaveAsync(window, System.IO.Path.Combine(output, "library.png"));

            var (title, page) = Parse(open);
            var book = (title is null ? library.Items.FirstOrDefault(i => i.CanOpen)
                : library.Items.FirstOrDefault(i => i.CanOpen && i.Title.Contains(title, StringComparison.OrdinalIgnoreCase)))
                ?? throw new InvalidOperationException($"No book in the Library matches {title}.");
            // A whole page, with the thumbnails beside it, set before it opens so the page asked for stays in view.
            var readers = services.GetRequiredService<ReaderWindows>();
            readers.ShowsStrip = true;
            readers.OpenInMainWindow(new ViewerRequest(book.DocumentId, book.Title, page) { Zoom = Bibliotaph.Viewer.PdfPagesView.FitPage });
            var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The reader didn't open.");
            await WaitAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, "The book didn't open.");
            if (!viewer.IsPdf) throw new InvalidOperationException($"The book didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
            await SaveAsync(window, System.IO.Path.Combine(output, "reader.png"));
            Log.Information("Screenshots saved in {Folder}", output);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Screenshots failed");
            return 1;
        }
    }

    /// <summary>"Title|page", the page as the PDF counts them from 1; either part can be left out.</summary>
    static (string? Title, int PageIndex) Parse(string? open)
    {
        if (string.IsNullOrWhiteSpace(open)) return (null, 0);
        var parts = open.Split('|', 2);
        var title = parts[0].Trim() is { Length: > 0 } t ? t : null;
        var page = parts.Length > 1 && int.TryParse(parts[1], out var p) && p > 0 ? p - 1 : 0;
        return (title, page);
    }

    /// <summary>
    /// Until every file is read, indexed and has its scanned pages read. Cataloguing with AI isn't waited for: the
    /// pictures show what a library looks like before AI, or without it.
    /// </summary>
    static async Task WaitForIndexingAsync(LibraryActivity activity)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * IndexingLimit.TotalSeconds);
        string[] busy = ["Looking through", "Reading", "Indexing", "Waiting for disk space"];
        // A scan starts a moment after it is asked for, so the first few quiet readings don't count.
        var quiet = 0;
        while (quiet < 5)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            quiet = busy.Any(b => activity.Summary.StartsWith(b, StringComparison.Ordinal)) || activity.Summary == "Nothing to process" ? 0 : quiet + 1;
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"Indexing didn't finish: {activity.Summary}.");
        }
        Log.Information("Screenshots: library ready, {Summary}", activity.Summary);
    }

    static async Task WaitAsync(Window window, Func<bool> condition, string failure)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 30;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException(failure);
            await Settle(window);
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// Lets covers and pages finish loading, then draws the window's content, without its frame, to a PNG, on the
    /// window's own background, which the content leaves showing through.
    /// </summary>
    static async Task SaveAsync(Window window, string path)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(250);
            await Settle(window);
        }
        var content = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background ?? Brushes.White, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, bounds);
        }
        var bitmap = new RenderTargetBitmap((int)Math.Round(bounds.Width * dpi.DpiScaleX), (int)Math.Round(bounds.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new System.IO.MemoryStream();
        encoder.Save(png);
#pragma warning disable RS0030 // Only into the folder named by --screenshots, a developer's own; never near a source file.
        await System.IO.File.WriteAllBytesAsync(path, png.ToArray());
#pragma warning restore RS0030
    }

    static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
}
