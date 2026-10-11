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
    /// Online libraries (O2): a made-up D&amp;D Beyond library page, saved with its covers beside the data root by
    /// verify.ps1, imported from Library &gt; Online libraries. The preview, the two new books with their links and
    /// covers, the preview again with both imported, then Undo and the library as it was.
    /// </summary>
    static async Task ImportOnlineLibraryAsync(IServiceProvider services, Window window, int books)
    {
        var root = System.IO.Path.GetDirectoryName(services.GetRequiredService<AppPaths>().Root)!;
        var saved = System.IO.Path.Combine(root, "online", "dndbeyond-library.html");
        if (!System.IO.File.Exists(saved))
        {
            Log.Warning("Smoke test: no saved library page at {Path}, so importing one is left out", saved);
            return;
        }

        var navigation = services.GetRequiredService<INavigationService>();
        var shell = services.GetRequiredService<ShellViewModel>();
        navigation.NavigateTo(Route.Library);
        await Settle(window);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library route didn't open the Library.");
        await ChooseAsync(window, "OwnedElsewhere", library.OpenOnlineLibrariesCommand, "Import from an online library");
        await WaitUntilAsync(window, () => shell.CurrentPage is OnlineLibrariesViewModel, () => "Online libraries didn't open its page.");
        var page = (OnlineLibrariesViewModel)shell.CurrentPage!;
        if (page.Services is not [{ Name: "D&D Beyond" }]) throw new InvalidOperationException("Online libraries doesn't list D&D Beyond.");

        // The file picker can't be driven here, so the page is read as the picker would read it.
        await page.ReadSavedPageAsync(saved);
        await Settle(window);
        if (page.Problem is { } problem) throw new InvalidOperationException($"The saved page wasn't read: {problem}");
        if (page.Summary != "Your D&D Beyond library: 2 books. 1 book shared with you was left out.")
            throw new InvalidOperationException($"The preview says “{page.Summary}”.");
        if (page.Groups is not [{ Outcome: OnlineOutcome.New, Items.Count: 2 }] || page.HasMissing)
            throw new InvalidOperationException($"The preview's groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");
        if (!Descendants<TextBlock>(window).Any(t => t.Text == "The Ember Vault" && t.IsVisible)) throw new InvalidOperationException("The preview doesn't list the first book.");
        await AuditAsync(window, "an online library's import preview");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ImportOnline"), "Import 2 books");
        await WaitUntilAsync(window, () => !page.IsBusy && page.Message == "Added 2 books owned on D&D Beyond.",
            () => $"Importing says “{page.Message}”.");
        if (page.Groups is not [{ Outcome: OnlineOutcome.Unchanged, Items.Count: 2 }])
            throw new InvalidOperationException($"After importing, the preview's groups are {string.Join(", ", page.Groups.Select(g => g.Heading))}.");

        var visible = await services.GetRequiredService<LibraryStore>().GetVisibleEntryIdsAsync();
        var cards = await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter(visible)));
        var vault = cards.FirstOrDefault(c => c.Title == "The Ember Vault") ?? throw new InvalidOperationException("The imported book isn't in the library.");
        if (cards.Count != books + 2 || !vault.IsElsewhere || vault.OnlineLink != "https://www.dndbeyond.com/sources/dnd/ember-vault" || vault.Cover is null)
            throw new InvalidOperationException($"The library has {cards.Count} cards; the imported book links to {vault.OnlineLink} with cover {vault.Cover}.");
        await AuditAsync(window, "an online library imported");

        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "UndoOnline"), "Undo the import");
        await WaitUntilAsync(window, () => !page.IsBusy && page.Message is null && page.Groups is [{ Outcome: OnlineOutcome.New }],
            () => $"Undo left “{page.Message}”.");
        cards = await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter(visible)));
        if (cards.Count != books) throw new InvalidOperationException($"After Undo the library has {cards.Count} cards, not {books}.");

        await ReadInBrowserAsync(window, page, saved);
        navigation.GoBack();
        await Settle(window);
    }

    /// <summary>
    /// The in-app browser opened at the made-up page instead of D&amp;D Beyond's: Read this page lights up once the
    /// reader recognises it, and pressing it shows its books in the preview. Pressed again, the same books count once.
    /// </summary>
    static async Task ReadInBrowserAsync(Window window, OnlineLibrariesViewModel page, string saved)
    {
        if (OnlineBrowsers.RuntimeVersion is null)
        {
            Log.Warning("Smoke test: there is no WebView2 Runtime, so the in-app browser is left out");
            return;
        }
        var browser = page.OpenBrowser(page.Services[0].Reader, new Uri(saved)) ?? throw new InvalidOperationException($"The browser didn't open: {page.Problem}");
        try
        {
            // The browser's first start takes a while on a fresh profile.
            var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + System.Diagnostics.Stopwatch.Frequency * 60;
            while (!browser.State.CanRead)
            {
                if (System.Diagnostics.Stopwatch.GetTimestamp() > deadline)
                    throw new InvalidOperationException($"Read this page didn't light up on the saved page: “{browser.State.Status}”");
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
    }

    /// <summary>
    /// The online reader (O2b) on the made-up library page standing in for a book's page: it opens in the main window
    /// with the page loaded, keeps the book's note, pops out into a window of its own and comes back.
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
    /// Add this page (O2b): the page in view goes into the current binder as a link labelled with the page's title, and
    /// opening it from the binder's list opens the online reader at that page. The binder is deleted afterwards.
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
            await WaitUntilAsync(window, () => reader.Sessions.Message?.StartsWith("Added", StringComparison.Ordinal) == true,
                () => $"Add this page didn't add the page ({reader.Sessions.Message ?? "no message"}).");
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
