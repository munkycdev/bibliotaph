using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// <c>Bibliotaph.exe --smoke-test --data-root &lt;folder&gt;</c>: opens the real window, visits every route in
/// light and dark, fills a small made-up library and browses and searches it in both themes, then exits 0, or 1 on
/// any exception or binding error. CI runs it on Windows so a broken
/// resource or template fails the build instead of the first launch. It is not a substitute for looking.
/// </summary>
static class SmokeTest
{
    public static async Task<int> RunAsync(IServiceProvider services, Window window)
    {
        var bindingErrors = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            if (window.Icon is null) throw new InvalidOperationException("The main window has no icon.");
            // Covers are encoded on indexing threads, so check the codec off the UI thread.
            await Task.Run(CheckImageCodec);
            var navigation = services.GetRequiredService<INavigationService>();
            var theme = services.GetRequiredService<ThemeService>();
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                foreach (var route in Enum.GetValues<Route>())
                {
                    navigation.NavigateTo(route);
                    await Settle(window);
                    Log.Information("Smoke test: {Route} rendered in {Theme}", route, preference);
                }
                while (navigation.GoBack()) await Settle(window);
            }

            await SeedLibraryAsync(services);
            foreach (var preference in new[] { ThemePreference.Light, ThemePreference.Dark })
            {
                await theme.SetPreferenceAsync(preference);
                await BrowseLibraryAsync(services, window);
                Log.Information("Smoke test: library browsed and searched in {Theme}", preference);
            }
            await theme.SetPreferenceAsync(ThemePreference.System);
            await Settle(window);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Smoke test failed");
            return 1;
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
        }

        if (bindingErrors.Errors.Count > 0)
        {
            foreach (var error in bindingErrors.Errors) Log.Error("Smoke test binding error: {Error}", error);
            return 1;
        }
        Log.Information("Smoke test passed");
        return 0;
    }

    /// <summary>
    /// A made-up library written straight to the catalog and index, so the Library has covers, a list, results and
    /// an inspector to draw. Its folder doesn't exist and indexing is paused, so nothing tries to read the files.
    /// </summary>
    static async Task SeedLibraryAsync(IServiceProvider services)
    {
        var indexing = services.GetRequiredService<IndexingService>();
        indexing.Pause(Lane.Index);
        indexing.Pause(Lane.Ocr);
        var root = await services.GetRequiredService<SourceRootStore>()
            .AddAsync(System.IO.Path.Combine(services.GetRequiredService<AppPaths>().Root, "smoke-library"));
        var library = services.GetRequiredService<LibraryStore>();
        var index = services.GetRequiredService<IndexStore>();
        (string Path, string Title, string[] Pages)[] books =
        [
            ("Setting/Gazetteer of the Marches.pdf", "Gazetteer of the Marches", ["Contents", "The red dragon sleeps beneath the mill.", "Goblins raid the tavern."]),
            ("Monsters/Dragon Lairs.pdf", "Dragon Lairs", ["A lich keeps a tavern ledger.", "Red dragon lair maps."]),
            ("Adventures/Haunted Inn.pdf", "Haunted Inn", ["The inn is haunted; a secret door hides behind the bar."]),
            ("Handouts/Tavern Map.png", "Tavern Map", []),
        ];
        var modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // The scanner reports paths with the platform's separator.
        static string Relative(string path) => path.Replace('/', System.IO.Path.DirectorySeparatorChar);
        await library.ReconcileRootAsync(root.Id, [.. books.Select(b => new ScannedFile(Relative(b.Path), 1000, modified, false))]);
        var files = await library.NextUnhashedAsync(books.Length, includeOnlineOnly: true);
        foreach (var file in files)
        {
            var book = books.Single(b => file.FullPath.EndsWith(Relative(b.Path), StringComparison.Ordinal));
            var hash = ContentHash.Parse(new string((char)('a' + Array.IndexOf(books, book)), ContentHash.HexLength));
            var (documentId, _) = await library.AttachHashAsync(file, hash) ?? throw new InvalidOperationException("A made-up file didn't attach.");
            var isImage = file.Format != SourceFormats.Pdf;
            await index.UpsertDocumentAsync(
                new DocRow
                {
                    DocumentId = documentId,
                    ContentHash = hash.Hex,
                    Format = file.Format,
                    DisplayTitle = book.Title,
                    PageCount = isImage ? null : book.Pages.Length,
                    WidthPx = isImage ? 1600 : null,
                    HeightPx = isImage ? 1200 : null,
                    FolderHint = System.IO.Path.GetDirectoryName(Relative(book.Path)),
                },
                [.. book.Pages.Select((_, i) => new PageRow(i, (i + 1).ToString(CultureInfo.InvariantCulture), 612, 792))], []);
            if (!isImage) await index.SetPageTextAsync(documentId, [.. book.Pages.Select((text, i) => new PageTextRow(i, text, "pdf", 1, false))]);
        }
    }

    /// <summary>The Library in covers and as a list, both search tabs, a query with a problem, and the inspector.</summary>
    static async Task BrowseLibraryAsync(IServiceProvider services, Window window)
    {
        var search = services.GetRequiredService<SearchState>();
        services.GetRequiredService<INavigationService>().NavigateTo(Route.Library);
        var page = services.GetRequiredService<ShellViewModel>().CurrentPage as LibraryViewModel
            ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        // Queries run off the UI thread, so an idle dispatcher doesn't mean the results are in: wait for them.
        await WaitUntilAsync(window, () => page.Items.Count == 4, () => $"The Library shows {page.Items.Count} books, not 4.");

        page.Layout = LibraryLayout.List;
        page.ShowFilters = true;
        await Settle(window);

        search.Search("dragon");
        await WaitUntilAsync(window, () => page.IsSearching && page.Items.Count > 0 && !page.IsEmpty, () => "Searching for dragon found no documents.");
        page.Tab = ResultsTab.Pages;
        await WaitUntilAsync(window, () => page.Hits.Count > 0, () => "Searching for dragon found no pages.");

        await page.OpenDetailsCommand.ExecuteAsync(page.Hits[0].Item);
        await Settle(window);
        if (page.Inspector is null) throw new InvalidOperationException("The inspector didn't open.");
        page.CloseDetailsCommand.Execute(null);

        search.Search("type:adventure tavern");
        await WaitUntilAsync(window, () => page.IssueText is not null, () => "A metadata field didn't say it arrives later.");

        search.Search("nothing-matches-this");
        await WaitUntilAsync(window, () => page.IsEmpty, () => "A search with no results didn't show the empty state.");

        // The search box's × ends the search; the box gets the text through SearchState like any search.
        var box = (TextBox)window.FindName("Search");
        var clear = box.Template.FindName("Clear", box) as Button ?? throw new InvalidOperationException("The search box has no clear button.");
        if (clear.Visibility != Visibility.Visible) throw new InvalidOperationException("The search box's × is hidden while it has text.");
        ((IInvokeProvider)new ButtonAutomationPeer(clear).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitUntilAsync(window, () => box.Text.Length == 0 && !search.IsSearching && clear.Visibility == Visibility.Collapsed,
            () => "The search box's × didn't clear the search.");
        page.Tab = ResultsTab.Documents;
        page.Layout = LibraryLayout.Grid;
        page.ShowFilters = false;
        await WaitUntilAsync(window, () => !page.IsSearching && page.Items.Count == 4, () => "Clearing the search didn't bring back all 4 books.");
    }

    /// <summary>Lets the window work until <paramref name="condition"/> holds, or fails after a few seconds.</summary>
    static async Task WaitUntilAsync(Window window, Func<bool> condition, Func<string> failure)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
        while (true)
        {
            await Settle(window);
            if (condition()) return;
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException(failure());
            await Task.Delay(50);
        }
    }

    /// <summary>WIC is only on Windows, so the cover codec is checked here rather than in the unit tests.</summary>
    static void CheckImageCodec()
    {
        var codec = new WpfImageCodec();
        var pixels = new byte[16 * 8 * 4];
        Array.Fill(pixels, (byte)200);
        using var jpeg = new System.IO.MemoryStream(codec.EncodeJpeg(pixels, 16, 8, 16 * 4));
        if (codec.ReadSize(jpeg) != (16, 8)) throw new InvalidOperationException("A JPEG cover did not read back at 16x8.");
        jpeg.Position = 0;
        using var thumbnail = new System.IO.MemoryStream(codec.Thumbnail(jpeg, 4) ?? throw new InvalidOperationException("No thumbnail was made."));
        if (codec.ReadSize(thumbnail)?.Width != 4) throw new InvalidOperationException("The thumbnail is not 4 px wide.");
        if (codec.ReadSize(new System.IO.MemoryStream([1, 2, 3])) is not null) throw new InvalidOperationException("Garbage read as an image.");
        Log.Information("Smoke test: image codec works off the UI thread");
    }

    static async Task Settle(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message is not null) Errors.Add(message);
        }
    }
}
