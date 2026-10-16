using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Windows;
using Bibliotaph.App.ViewModels;
using Bibliotaph.App.Views;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>An EPUB read in the e-book reader.</summary>
static partial class SmokeTest
{
    const string EbookTitle = "Smoke Ebook";

    /// <summary>
    /// A made-up EPUB of three chapters, indexed by the real pipeline and opened in the reader: its page shows in the
    /// browser, Find goes to the page its word is on, the page box and the contents panel go to their pages, and the
    /// zoom steps. The reader is audited like the PDF one.
    /// </summary>
    static async Task ReadEbookAsync(IServiceProvider services, Window window)
    {
        // Left for the system's temp cleaning: the app may not delete folders.
        var folder = System.IO.Directory.CreateTempSubdirectory("bibliotaph-smoke-ebook-").FullName;
        WriteSmokeEpub(System.IO.Path.Combine(folder, $"{EbookTitle}.epub"));
        var root = await services.GetRequiredService<SourceRootStore>().AddAsync(folder);
        services.GetRequiredService<IJobControl>().RequestScan(root.Id);

        var queries = services.GetRequiredService<LibraryQueries>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        LibraryEntry? book = null;
        while ((book = (await Task.Run(() => queries.ListAsync(new LibraryFilter()))).FirstOrDefault(e => e.Title == EbookTitle)) is not { Searchable: true })
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"The EPUB wasn't indexed ({(book is null ? "no card" : "not searchable")}).");
            await Settle(window);
            await Task.Delay(200);
        }

        var shell = services.GetRequiredService<ShellViewModel>();
        services.GetRequiredService<ReaderWindows>().OpenInMainWindow(new ViewerRequest(book.DocumentId, EbookTitle, 0));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The EPUB didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsEbook || viewer.Mode == ViewerMode.Problem, () => $"The EPUB didn't open (still {viewer.Mode}).");
        if (!viewer.IsEbook) throw new InvalidOperationException($"The EPUB didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        var surface = FindChild<EbookView>(window) ?? throw new InvalidOperationException("The viewer has no e-book surface.");
        // The browser starts the first time a book opens, which takes longer than a page turn.
        deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        while (!surface.IsShowing)
        {
            if (viewer.Mode == ViewerMode.Problem) throw new InvalidOperationException($"The e-book reader failed: {viewer.EmptyTitle} {viewer.EmptyMessage}");
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException("The e-book reader's page didn't show.");
            await Settle(window);
            await Task.Delay(100);
        }
        var last = viewer.PageCount - 1;
        if (last < 3) throw new InvalidOperationException($"The EPUB has {viewer.PageCount} pages, not one per chapter and more for the long one.");
        if (viewer.Outline is not [{ Title: "Arrival" }, { Title: "The Long Road" } road, { Title: "The Lighthouse" }])
            throw new InvalidOperationException($"The contents panel shows {viewer.Outline.Count} entries, not the EPUB's three chapters.");

        // Find is answered by the page, and goes to the page the word is on: the last chapter's only page.
        viewer.Find.Query = "lantern";
        await viewer.Find.FindCommand.ExecuteAsync(null);
        if (!viewer.Find.Status.StartsWith("1 of 1", StringComparison.Ordinal)) throw new InvalidOperationException($"Find in the EPUB said {viewer.Find.Status}.");
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == last, () => $"Find left the EPUB at page {viewer.CurrentPageIndex + 1}, not {last + 1}.");
        viewer.Find.ClearCommand.Execute(null);

        viewer.PageEntry = "1";
        viewer.GoToPageCommand.Execute(null);
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == 0, () => $"Going to page 1 left the EPUB at page {viewer.CurrentPageIndex + 1}.");
        viewer.OpenOutlineEntryCommand.Execute(road);
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == road.PageIndex,
            () => $"The Long Road's contents entry left the EPUB at page {viewer.CurrentPageIndex + 1}, not {road.PageIndex + 1}.");

        viewer.StepEbookZoom(1);
        if (viewer.Zoom <= 1) throw new InvalidOperationException($"Zooming in on the EPUB left the zoom at {viewer.Zoom}.");
        viewer.StepEbookZoom(0);
        await AuditAsync(window, "an EPUB in the reader");

        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
        if (!surface.IsLoaded && surface.IsShowing) throw new InvalidOperationException("The e-book reader's page stayed open after Back.");
    }

    /// <summary>
    /// A Kindle book (the AZW3 the e-book tests use, which verify.ps1 puts in a folder beside the data root), indexed
    /// and read the same way: rebuilt from its records, shown in the reader, Find going to its matches and the
    /// contents to their pages.
    /// </summary>
    static async Task ReadKindleBookAsync(IServiceProvider services, Window window)
    {
        const string Title = "The Sample Road";
        var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(services.GetRequiredService<AppPaths>().Root)!, "kindle");
        if (!System.IO.File.Exists(System.IO.Path.Combine(folder, "sample.azw3"))) throw new InvalidOperationException($"The Kindle book isn't in {folder}.");
        var root = await services.GetRequiredService<SourceRootStore>().AddAsync(folder);
        services.GetRequiredService<IJobControl>().RequestScan(root.Id);

        var queries = services.GetRequiredService<LibraryQueries>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        LibraryEntry? book = null;
        while ((book = (await Task.Run(() => queries.ListAsync(new LibraryFilter()))).FirstOrDefault(e => e.Title == Title)) is not { Searchable: true })
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"The AZW3 wasn't indexed ({(book is null ? "no card" : "not searchable")}).");
            await Settle(window);
            await Task.Delay(200);
        }

        var shell = services.GetRequiredService<ShellViewModel>();
        services.GetRequiredService<ReaderWindows>().OpenInMainWindow(new ViewerRequest(book.DocumentId, Title, 0));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The AZW3 didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsEbook || viewer.Mode == ViewerMode.Problem, () => $"The AZW3 didn't open (still {viewer.Mode}).");
        if (!viewer.IsEbook) throw new InvalidOperationException($"The AZW3 didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        var surface = FindChild<EbookView>(window) ?? throw new InvalidOperationException("The viewer has no e-book surface.");
        await WaitUntilAsync(window, () => surface.IsShowing || viewer.Mode == ViewerMode.Problem, () => "The AZW3's page didn't show.");
        if (!viewer.Subtitle.Contains("AZW3", StringComparison.Ordinal)) throw new InvalidOperationException($"The AZW3's subtitle is {viewer.Subtitle}.");
        if (viewer.Outline.FirstOrDefault(o => o.Title == "The Lighthouse") is not { } lighthouse || viewer.Outline.All(o => o.Title != "The Crossing"))
            throw new InvalidOperationException($"The AZW3's contents panel shows {string.Join(", ", viewer.Outline.Select(o => o.Title))}.");

        // The lantern is named in the first chapter's link and in the last chapter.
        viewer.Find.Query = "lantern";
        await viewer.Find.FindCommand.ExecuteAsync(null);
        if (!viewer.Find.Status.StartsWith("1 of 2", StringComparison.Ordinal)) throw new InvalidOperationException($"Find in the AZW3 said {viewer.Find.Status}.");
        viewer.Find.ClearCommand.Execute(null);
        viewer.OpenOutlineEntryCommand.Execute(lighthouse);
        await WaitUntilAsync(window, () => viewer.CurrentPageIndex == lighthouse.PageIndex,
            () => $"The Lighthouse's contents entry left the AZW3 at page {viewer.CurrentPageIndex + 1}, not {lighthouse.PageIndex + 1}.");

        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }

    /// <summary>Three chapters: a short one, one long enough for several pages, and one with a word found nowhere else.</summary>
    static void WriteSmokeEpub(string path)
    {
        var road = new StringBuilder();
        for (var i = 1; i <= 24; i++)
            road.Append("<p>Mile ").Append(i).Append(" of the road ran on past fields and fences, low walls and old stones, ")
                .Append("under a sky that kept its grey all day while the carts went by and the dust settled behind them.</p>");
        string Chapter(string title, string body) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>" + title
            + "</title></head><body><h1>" + title + "</h1>" + body + "</body></html>";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text, CompressionLevel level = CompressionLevel.Optimal)
        {
            using var entry = zip.CreateEntry(name, level).Open();
            entry.Write(Encoding.UTF8.GetBytes(text));
        }
        Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">"
            + "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        Add("OEBPS/content.opf", "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\">"
            + "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:uuid:5e1f0c3a-0000-4000-8000-000000000001</dc:identifier>"
            + $"<dc:title>{EbookTitle}</dc:title><dc:language>en</dc:language></metadata><manifest>"
            + "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
            + "<item id=\"c1\" href=\"c1.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"c2\" href=\"c2.xhtml\" media-type=\"application/xhtml+xml\"/>"
            + "<item id=\"c3\" href=\"c3.xhtml\" media-type=\"application/xhtml+xml\"/></manifest>"
            + "<spine><itemref idref=\"c1\"/><itemref idref=\"c2\"/><itemref idref=\"c3\"/></spine></package>");
        Add("OEBPS/nav.xhtml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">"
            + "<head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol><li><a href=\"c1.xhtml\">Arrival</a></li>"
            + "<li><a href=\"c2.xhtml\">The Long Road</a></li><li><a href=\"c3.xhtml\">The Lighthouse</a></li></ol></nav></body></html>");
        Add("OEBPS/c1.xhtml", Chapter("Arrival", "<p>The travellers reached the village at dusk and asked for the road north.</p>"));
        Add("OEBPS/c2.xhtml", Chapter("The Long Road", road.ToString()));
        Add("OEBPS/c3.xhtml", Chapter("The Lighthouse", "<p>At the end of the road the keeper lit the lantern and watched the sea.</p>"));
    }
}
