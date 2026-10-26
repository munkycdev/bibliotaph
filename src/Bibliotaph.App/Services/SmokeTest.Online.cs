using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

static partial class SmokeTest
{
    /// <summary>
    /// Online libraries: a made-up D&amp;D Beyond library page, put beside the data root by verify.ps1, read in
    /// Bibliotaph's browser from Settings &gt; Online libraries, which the Library's Import from an online library opens.
    /// The preview, the two new books with their links, the preview again with both imported, then Undo and the library
    /// as it was.
    /// </summary>
    static async Task ImportOnlineLibraryAsync(IServiceProvider services, Window window, int books)
    {
        var root = System.IO.Path.GetDirectoryName(services.GetRequiredService<AppPaths>().Root)!;
        var saved = System.IO.Path.Combine(root, "online", "dndbeyond-library.html");
        if (!System.IO.File.Exists(saved))
        {
            Log.Warning("Smoke test: no made-up library page at {Path}, so importing one is left out", saved);
            return;
        }

        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        navigation.NavigateTo(Route.Library);
        await Settle(window);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        await ChooseAsync(window, "AddMenu", library.OpenOnlineLibrariesCommand, "Import from an online library");
        var page = (OnlineLibrariesViewModel)(await ExpectSettingsAsync(window, shell, SettingsSection.OnlineLibraries, "The Library's Import from an online library")).Selected;
        // How books owned online show and open is here, not in Settings > Library, so everything online is in one place.
        await Settle(window);
        if (Descendants<CheckBox>(window).Count(c => c.Name is "ShowSiteIcons" or "ReadOnlineInApp" && c.IsVisible) != 2)
            throw new InvalidOperationException("Settings > Online libraries doesn't show Show site icons and Read books owned online in Bibliotaph.");
        if (page.Services.Select(s => s.Name).ToList() is not ["D&D Beyond", "Demiplane", "Humble Bundle", "DriveThruRPG"])
            throw new InvalidOperationException($"Online libraries lists {string.Join(", ", page.Services.Select(s => s.Name))}.");

        if (!await ReadInBrowserAsync(window, page, saved))
        {
            navigation.GoBack();
            await Settle(window);
            return;
        }
        await Settle(window);
        if (page.Summary != "Your D&D Beyond library: 2 books. 1 book shared with you was left out.")
            throw new InvalidOperationException($"The preview says “{page.Summary}”.");
        if (page.Groups is not [{ Outcome: OnlineOutcome.New, Items.Count: 2 }] || page.HasMissing)
            throw new InvalidOperationException($"The preview's groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");
        if (!Descendants<TextBlock>(window).Any(t => t.Text == "The Ember Vault" && t.IsVisible)) throw new InvalidOperationException("The preview doesn't list the first book.");
        await AuditAsync(window, "an online library's import preview");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ImportOnline"), "Import 2 books");
        await WaitUntilAsync(window, () => !page.IsBusy && page.Undo.Message == "Added 2 books owned on D&D Beyond.",
            () => $"Importing says “{page.Undo.Message}”.");
        if (page.Groups is not [{ Outcome: OnlineOutcome.Unchanged, Items.Count: 2 }])
            throw new InvalidOperationException($"After importing, the preview's groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");

        var visible = await services.GetRequiredService<LibraryStore>().GetVisibleEntryIdsAsync();
        var cards = await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter(visible)));
        var vault = cards.FirstOrDefault(c => c.Title == "The Ember Vault") ?? throw new InvalidOperationException("The imported book isn't in the library.");
        // Its covers are pictures beside the made-up page, not the service's; the import's own tests check covers.
        if (cards.Count != books + 2 || !vault.IsElsewhere || vault.OnlineLink != "https://www.dndbeyond.com/sources/dnd/ember-vault")
            throw new InvalidOperationException($"The library has {cards.Count} cards; the imported book links to {vault.OnlineLink}.");
        await AuditAsync(window, "an online library imported");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "UndoOnline"), "Undo the import");
        await WaitUntilAsync(window, () => !page.IsBusy && page.Undo.Message is null && page.Groups is [{ Outcome: OnlineOutcome.New }],
            () => $"Undo left “{page.Undo.Message}”.");
        cards = await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter(visible)));
        if (cards.Count != books) throw new InvalidOperationException($"After Undo the library has {cards.Count} cards, not {books}.");

        navigation.GoBack();
        await Settle(window);
    }

    /// <summary>
    /// The in-app browser opened at the made-up page instead of D&amp;D Beyond's: Read this page lights up once the
    /// reader recognises it, and pressing it shows its books in the preview. Pressed again, the same books count once.
    /// False, with nothing read, where there is no WebView2 Runtime to run it.
    /// </summary>
    static async Task<bool> ReadInBrowserAsync(Window window, OnlineLibrariesViewModel page, string saved)
    {
        if (OnlineBrowsers.RuntimeVersion is null)
        {
            Log.Warning("Smoke test: there is no WebView2 Runtime, so importing from an online library is left out");
            return false;
        }
        var browser = page.OpenBrowser(page.Services[0].Reader, new Uri(saved)) ?? throw new InvalidOperationException($"The browser didn't open: {page.Problem}");
        try
        {
            // The browser's first start takes a while on a fresh profile.
            var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60;
            while (!browser.State.CanRead)
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                    throw new InvalidOperationException($"Read this page didn't light up on the made-up page: “{browser.State.Status}”");
                await Task.Delay(200);
            }
            await AuditAsync(browser, "the in-app browser on a library page");

            for (var time = 1; time <= 2; time++)
            {
                var before = browser.State.Status;
                Click(Descendants<Button>(browser).FirstOrDefault(b => b.Name == "ReadPage"), "Read this page");
                await WaitUntilAsync(window, () => browser.State.Status != before && browser.State.CanRead,
                    () => $"Read this page said “{browser.State.Status}”.");
                if (!browser.State.Status.StartsWith("Read 2 books.", StringComparison.Ordinal) || page.Groups is not [{ Outcome: OnlineOutcome.New, Items.Count: 2 }])
                    throw new InvalidOperationException($"Read this page ({time}) said “{browser.State.Status}”; the preview's groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");
                // Pressed again, it says so afresh.
                browser.State.Status = "";
            }
        }
        finally
        {
            browser.Close();
        }
        return true;
    }

    /// <summary>
    /// The online reader on the made-up library page standing in for a book's page: it opens in the main window with the
    /// page loaded, keeps the book's note, pops out into a window of its own and comes back.
    /// </summary>
    static async Task ReadOnlineBookAsync(IServiceProvider services, Window window)
    {
        var saved = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(services.GetRequiredService<AppPaths>().Root)!, "online", "dndbeyond-library.html");
        if (OnlineBrowsers.RuntimeVersion is null || !System.IO.File.Exists(saved))
        {
            Log.Warning("Smoke test: no WebView2 Runtime or no made-up page, so the online reader is left out");
            return;
        }
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        var notes = services.GetRequiredService<NotesService>();
        var visible = await services.GetRequiredService<LibraryStore>().GetVisibleEntryIdsAsync();
        var book = (await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter(visible))))[0];
        var noteBefore = await notes.GetEntryNoteAsync(book.EntryId);

        var readers = services.GetRequiredService<OnlineReaders>();
        var request = new OnlineReaderRequest(book.EntryId, "Smoke online book", new Uri(saved).AbsoluteUri) { Place = "D&D Beyond" };
        if (await readers.OpenAsync(request) is { } problem) throw new InvalidOperationException($"The online reader didn't open: {problem}");
        await WaitUntilAsync(window, () => shell.CurrentPage is OnlineReaderViewModel, () => "The online reader didn't show in the main window.");
        var reader = (OnlineReaderViewModel)shell.CurrentPage!;
        await WaitForPageAsync(window, reader);
        if (Descendants<Button>(window).Any(b => b.Name is "OnlineReturn" && b.IsVisible)) throw new InvalidOperationException("The main window's online reader offers Return.");
        await AuditAsync(window, "the online reader");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "OnlineNote"), "Note");
        await WaitUntilAsync(window, () => reader.ShowNote, () => "Note didn't show the book's note.");
        reader.NoteText = "Smoke note from the online reader";
        await reader.SaveNoteAsync();
        if (await notes.GetEntryNoteAsync(book.EntryId) != "Smoke note from the online reader") throw new InvalidOperationException("The online reader didn't keep the note.");
        reader.NoteText = noteBefore ?? "";
        await reader.SaveNoteAsync();

        await AddOnlinePageAsync(services, window, reader);

        var windows = services.GetRequiredService<ReaderWindows>();
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "OnlinePopOut"), "Pop out");
        await WaitUntilAsync(window, () => windows.Find(m => m is OnlineReaderViewModel) is not null && shell.CurrentPage is not OnlineReaderViewModel,
            () => "Pop out didn't move the online reader to a window of its own.");
        var popped = windows.Find(m => m is OnlineReaderViewModel)!;
        var poppedReader = (OnlineReaderViewModel)popped.Model;
        await WaitForPageAsync(popped, poppedReader);
        Click(Descendants<Button>(popped).FirstOrDefault(b => b.Name == "OnlineReturn"), "Return to main window");
        await WaitUntilAsync(window, () => windows.Find(m => m is OnlineReaderViewModel) is null && shell.CurrentPage is OnlineReaderViewModel,
            () => "Return to main window didn't bring the online reader back.");
        navigation.GoBack();
        await Settle(window);
    }

    /// <summary>
    /// Add this page: the page in view goes into the current binder as a link labelled with the page's title, opening it
    /// from the binder's list opens the online reader at that page, and the binder's reader shows it there in place of its
    /// file reader. The binder is deleted afterwards.
    /// </summary>
    static async Task AddOnlinePageAsync(IServiceProvider services, Window window, OnlineReaderViewModel reader)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        var sessions = services.GetRequiredService<SessionsService>();
        var binder = await sessions.CreateAsync("Smoke online pages");
        await services.GetRequiredService<SessionDirectory>().LoadAsync();
        try
        {
            Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "OnlineAddPage" && b.IsVisible), "Add this page");
            await WaitUntilAsync(window, () => reader.Sessions.Undo.Message?.StartsWith("Added", StringComparison.Ordinal) == true,
                () => $"Add this page didn't add the page ({reader.Sessions.Undo.Message ?? "no message"}).");
            var item = Assert1((await sessions.GetAsync(binder.Id))!.Items, "the binder's items");
            if (item.Link != reader.Address || item.EntryId != reader.Request.EntryId || string.IsNullOrEmpty(item.Label))
                throw new InvalidOperationException($"Add this page added {item.Link} labelled {item.Label ?? "nothing"}, not {reader.Address}.");

            services.GetRequiredService<SessionPages>().Edit(binder.Id);
            await WaitUntilAsync(window, () => shell.CurrentPage is SessionPackViewModel { IsLoaded: true, HasItems: true }, () => "Edit binder didn't open the binder's list.");
            var pack = (SessionPackViewModel)shell.CurrentPage!;
            var row = pack.Rows.OfType<SessionItemRow>().Single();
            if (!row.OpensOnline || row.IsDimmed) throw new InvalidOperationException($"The binder's online page doesn't open online ({row.Reason}).");
            await AuditAsync(window, "a binder with a page owned online");
            pack.OpenItemCommand.Execute(row);
            await WaitUntilAsync(window, () => shell.CurrentPage is OnlineReaderViewModel { Request.At: { } at } && at == item.Link,
                () => "Opening the binder's online page didn't open the online reader at it.");
            await WaitForPageAsync(window, (OnlineReaderViewModel)shell.CurrentPage!);
            navigation.GoBack();
            await WaitUntilAsync(window, () => shell.CurrentPage is SessionPackViewModel, () => "Back didn't return to the binder's list.");

            // The binder's own reader shows the page in the online reader, in place of its file reader; the binder pops out, not the page.
            services.GetRequiredService<SessionPages>().Open(binder.Id);
            await WaitUntilAsync(window, () => shell.CurrentPage is SessionReaderViewModel { IsLoaded: true, Online.Request.At: { } at } && at == item.Link,
                () => "The binder's reader didn't show its online page in the online reader.");
            await WaitForPageAsync(window, ((SessionReaderViewModel)shell.CurrentPage!).Online!);
            if (Descendants<Button>(window).Any(b => b.Name is "OnlinePopOut" && b.IsVisible)) throw new InvalidOperationException("The binder's online page offers its own Pop out.");
            await AuditAsync(window, "a binder's reader at a page owned online");
            navigation.GoBack();
            await WaitUntilAsync(window, () => shell.CurrentPage is SessionPackViewModel, () => "Back didn't return to the binder's list from its reader.");
            navigation.GoBack();
            await WaitUntilAsync(window, () => shell.CurrentPage == reader, () => "Back didn't return to the online reader.");
            await WaitForPageAsync(window, reader);
        }
        finally
        {
            await sessions.DeleteAsync(binder.Id);
            await services.GetRequiredService<SessionDirectory>().LoadAsync();
        }
    }

    static T Assert1<T>(IReadOnlyList<T> items, string what) =>
        items.Count == 1 ? items[0] : throw new InvalidOperationException($"Expected one of {what}, found {items.Count}.");

    /// <summary>
    /// Downloads: a downloads folder outside the library refused, one inside it chosen, and a made-up store's Download (a
    /// link with a download name, clicked on the made-up page) saved there by the in-app browser, listed in its window and
    /// read into the library.
    /// </summary>
    static async Task DownloadFromStoreAsync(IServiceProvider services, Window window)
    {
        var data = System.IO.Path.GetDirectoryName(services.GetRequiredService<AppPaths>().Root)!;
        var saved = System.IO.Path.Combine(data, "online", "dndbeyond-library.html");
        if (OnlineBrowsers.RuntimeVersion is null || !System.IO.File.Exists(saved))
        {
            Log.Warning("Smoke test: no WebView2 Runtime or no made-up page, so downloading is left out");
            return;
        }
        var roots = await services.GetRequiredService<SourceRootStore>().ListAsync();
        var root = roots.FirstOrDefault(r => System.IO.Directory.Exists(r.Path)) ?? throw new InvalidOperationException("No library folder can be reached.");
        var inside = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Store downloads")).FullName;
        var outside = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(data, "not-a-library")).FullName;

        var page = (OnlineLibrariesViewModel)(await OpenSettingsAsync(services, window, SettingsSection.OnlineLibraries)).Selected;
        await page.UseDownloadsFolderAsync(outside);
        if (page.DownloadsProblem is null || page.HasDownloadsFolder) throw new InvalidOperationException("A downloads folder outside the library was taken.");
        await page.UseDownloadsFolderAsync(inside);
        if (page.DownloadsProblem is not null || page.DownloadsFolder != inside) throw new InvalidOperationException($"The downloads folder wasn't taken: {page.DownloadsProblem}");
        await Settle(window);
        await AuditAsync(window, "Settings > Online libraries with a downloads folder");

        var downloads = services.GetRequiredService<OnlineDownloads>();
        // A store's row: its browser signs in and downloads, and a page that isn't its library has nothing to read.
        var store = page.Services.First(s => s.Name == "Humble Bundle").Reader;
        var browser = page.OpenBrowser(store, new Uri(saved)) ?? throw new InvalidOperationException($"The browser didn't open: {page.Problem}");
        try
        {
            var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60;
            while (Descendants<Microsoft.Web.WebView2.Wpf.WebView2>(browser).FirstOrDefault()?.CoreWebView2 is not { DocumentTitle.Length: > 0 })
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"The made-up page didn't load: “{browser.State.Status}”");
                await Task.Delay(200);
            }
            await Task.Delay(800);
            if (browser.State.CanRead) throw new InvalidOperationException("A store's browser offers to read a page that isn't its library.");
            var web = Descendants<Microsoft.Web.WebView2.Wpf.WebView2>(browser).First().CoreWebView2;
            var pdf = Convert.ToBase64String(SmokePdf("Smoke store download"));
            await web.ExecuteScriptAsync(
                "(() => { const a = document.createElement('a'); a.href = 'data:application/pdf;base64," + pdf
                + "'; a.download = 'Smoke store download.pdf'; document.body.appendChild(a); a.click(); })();");
            static bool Ours(OnlineDownload d) => d.Name.StartsWith("Smoke store download", StringComparison.Ordinal);
            await WaitUntilAsync(browser, () => downloads.Items.FirstOrDefault(Ours) is { InProgress: false },
                () => $"The download didn't finish ({string.Join("; ", downloads.Items.Select(d => $"{d.Name}: {d.Status}"))}).");
            var download = downloads.Items.First(Ours);
            if (!download.Saved || download.RootId != root.Id || !System.IO.File.Exists(download.Path)
                || !string.Equals(System.IO.Path.GetDirectoryName(download.Path), inside, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The download went to {download.Path} ({download.Status}), not the downloads folder.");
            if (!browser.State.HasDownloads) throw new InvalidOperationException("The browser window doesn't list the download.");
            await AuditAsync(browser, "the in-app browser with a download");

            // A Download button that opens a new tab: Bibliotaph hosts the tab, saves the file to the downloads folder
            // too, and the tab, showing nothing, goes away.
            await web.ExecuteScriptAsync(
                "(() => { const w = window.open('', '_blank'); setTimeout(() => { const a = w.document.createElement('a'); a.href = 'data:application/pdf;base64," + pdf
                + "'; a.download = 'Smoke tab download.pdf'; w.document.body.appendChild(a); a.click(); }, 1500); })();");
            static bool InTab(OnlineDownload d) => d.Name.StartsWith("Smoke tab download", StringComparison.Ordinal);
            await WaitUntilAsync(browser, () => downloads.Items.FirstOrDefault(InTab) is { InProgress: false },
                () => $"The download from a new tab didn't finish ({string.Join("; ", downloads.Items.Select(d => $"{d.Name}: {d.Status}"))}).");
            var tab = downloads.Items.First(InTab);
            if (!tab.Saved || !string.Equals(System.IO.Path.GetDirectoryName(tab.Path), inside, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The download from a new tab went to {tab.Path} ({tab.Status}), not the downloads folder.");
            await WaitUntilAsync(browser, () => !Application.Current.Windows.OfType<Views.OnlinePopupWindow>().Any(),
                () => $"The new tab that only downloaded stayed open (shown: {Application.Current.Windows.OfType<Views.OnlinePopupWindow>().Any(w => w.IsVisible)}).");

            var relative = System.IO.Path.GetRelativePath(root.Path, download.Path);
            var library = services.GetRequiredService<LibraryStore>();
            var found = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60;
            while (!(await library.GetRelativePathsAsync()).Any(p => SamePath(p.RelativePath, relative)))
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() > found) throw new InvalidOperationException($"The library didn't read the download {relative}.");
                await Task.Delay(250);
            }
        }
        finally
        {
            browser.Close();
            await page.ClearDownloadsFolderCommand.ExecuteAsync(null);
        }
        await Settle(window);
    }

    static bool SamePath(string a, string b) => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>A one-page PDF saying <paramref name="text"/>, as a store might sell.</summary>
    static byte[] SmokePdf(string text)
    {
        var stream = $"BT /F1 24 Tf 72 700 Td ({text}) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = pdf.Length;
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(System.Globalization.CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }

    /// <summary>Waits for the online reader's browser to show the made-up page; the browser takes a while to start.</summary>
    static async Task WaitForPageAsync(Window window, OnlineReaderViewModel reader)
    {
        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60;
        while (Descendants<Microsoft.Web.WebView2.Wpf.WebView2>(window).FirstOrDefault()?.CoreWebView2?.Source is not { } source
            || !source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                throw new InvalidOperationException($"The online reader didn't show the page ({reader.Notice ?? "no notice"}).");
            await Task.Delay(200);
        }
        await Settle(window);
    }

    /// <summary>Opens a button's menu, as a click does, and chooses the item with <paramref name="command"/>.</summary>
    static async Task ChooseAsync(Window window, string button, System.Windows.Input.ICommand command, string name)
    {
        var opener = Descendants<Button>(window).FirstOrDefault(b => b.Name == button && b.IsVisible);
        Click(opener, button);
        await WaitUntilAsync(window, () => opener!.ContextMenu is { IsOpen: true }, () => $"The {button} button didn't open its menu.");
        var menu = opener!.ContextMenu!;
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Command == command) ?? throw new InvalidOperationException($"The menu has no {name}.");
        menu.IsOpen = false;
        if (!item.IsEnabled) throw new InvalidOperationException($"{name} is disabled.");
        item.Command.Execute(item.CommandParameter);
        await Settle(window);
    }
}
