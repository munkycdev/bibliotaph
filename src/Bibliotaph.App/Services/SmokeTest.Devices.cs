using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>
/// Settings > Devices, and the web library's books on a PC connected to a stand-in for bibliotaph.online
/// (<see cref="SmokeWebLibrary"/>): connecting for real needs the website and a browser.
/// </summary>
static partial class SmokeTest
{
    static async Task ShowDevicesAsync(IServiceProvider services, Window window)
    {
        var page = (DevicesSectionViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Devices)).Selected;
        await WaitUntilAsync(window, () => page.StatusLine == "Not connected."
            && Descendants<Button>(window).Any(b => b.Command == page.ConnectCommand && b.IsVisible)
            && !Descendants<Button>(window).Any(b => b.Command == page.DisconnectCommand && b.IsVisible),
            () => $"Settings > Devices says {page.StatusLine ?? "nothing"}.");
        await AuditAsync(window, "Settings > Devices, not connected");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }

    /// <summary>
    /// Connected to the stand-in web library: its book listed and found by a search without being downloaded, then
    /// downloaded to open it, kept offline from its details, counted in Settings > Devices, and its folder gone on
    /// Disconnect while the copy kept offline stays.
    /// </summary>
    static async Task ShowWebLibraryBooksAsync(IServiceProvider services, Window window)
    {
        const string Title = SmokeWebLibrary.Title;
        var copies = services.GetRequiredService<WebLibraryCopies>();
        var shelf = services.GetRequiredService<WebShelfSync>();
        var roots = services.GetRequiredService<SourceRootStore>();
        services.GetRequiredService<IWebLibraryKey>().Remember("smoke-test-only");
        await shelf.SyncNowAsync();

        var queries = services.GetRequiredService<LibraryQueries>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        LibraryEntry? book;
        while ((book = (await Task.Run(() => queries.ListAsync(new LibraryFilter()))).FirstOrDefault(e => e.Title == Title)) is not { Searchable: true })
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"The web library's book wasn't listed and read ({(book is null ? "no card" : "not searchable")}).");
            await Settle(window);
            await Task.Delay(200);
        }
        var found = await Task.Run(() => queries.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse($"\"{SmokeWebLibrary.Phrase}\"")), new LibraryFilter()));
        if (!found.Entries.Any(e => e.Pages.Any(p => p.DocumentId == book.DocumentId && p.PdfPage == 1)))
            throw new InvalidOperationException("A search doesn't find the web library's book at its second page.");
        if (copies.Has(SmokeWebLibrary.Hash)) throw new InvalidOperationException("The web library's book was downloaded before it was opened.");

        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        services.GetRequiredService<ReaderWindows>().OpenInMainWindow(new ViewerRequest(book.DocumentId, Title, 1));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The web library's book didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, () => $"The web library's book didn't open (still {viewer.Mode}).");
        if (!viewer.IsPdf) throw new InvalidOperationException($"The web library's book didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        if (!copies.Has(SmokeWebLibrary.Hash)) throw new InvalidOperationException("Opening the web library's book kept no copy of it.");
        // Open in another app hands Windows the downloaded copy, not the web library's path.
        var launcher = (SmokeShellLauncher)services.GetRequiredService<IShellLauncher>();
        var elsewhere = await services.GetRequiredService<OtherApps>().OpenAsync(book.DocumentId);
        var last = launcher.Opened is [.., var path] ? path : null;
        if (elsewhere is not null || last is null || !last.StartsWith(copies.Folder, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Open in another app for the web library's book opened {last ?? "nothing"} ({elsewhere}).");
        navigation.GoBack();
        await Settle(window);

        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await library.RefreshAsync();
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == book.DocumentId), () => "The web library's book isn't in the Library.");
        await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == book.DocumentId));
        library.Inspector!.Pane!.Where = true;
        await WaitUntilAsync(window, () => library.Inspector is { CanKeepOffline: true, Locations.Count: > 0 }
            && Descendants<CheckBox>(window).Any(c => c.Name == "KeepOffline" && c.IsVisible),
            () => "The web library's book's details offer no Keep offline.");
        var inspector = library.Inspector!;
        if (inspector.Locations[0] is not { IsWeb: true, CanShowInExplorer: false } location
            || location.Path != $"bibliotaph.online › OneDrive · Smoke › RPG › Adventures › {Title}.pdf")
            throw new InvalidOperationException($"The details show {inspector.Locations[0]} for the web library's book.");
        ((IToggleProvider)new CheckBoxAutomationPeer(Descendants<CheckBox>(window).First(c => c.Name == "KeepOffline")).GetPattern(PatternInterface.Toggle)).Toggle();
        await WaitUntilAsync(window, () => inspector is { IsKeptOffline: true, OfflineText: "Kept on this PC, so it opens without a connection." },
            () => $"Keep offline says \"{inspector.OfflineText}\".");
        await AuditAsync(window, "a web library book's details, kept offline");
        library.CloseDetailsCommand.Execute(null);
        await Settle(window);

        var page = (DevicesSectionViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Devices)).Selected;
        await WaitUntilAsync(window, () => page.IsConnected
            && page.CopiesLine?.StartsWith("1 book from your web library downloaded to this PC", StringComparison.Ordinal) == true
            && page.CopiesLine.EndsWith(", 1 kept offline.", StringComparison.Ordinal)
            && Descendants<ComboBox>(window).Any(c => c.Name == "WebCopiesRoom" && c.IsVisible),
            () => $"Settings > Devices says \"{page.StatusLine}\" and \"{page.CopiesLine}\".");
        await AuditAsync(window, "Settings > Devices, connected, with a book kept offline");
        await page.DisconnectCommand.ExecuteAsync(null);
        await shelf.SyncNowAsync();
        if ((await roots.ListAsync()).Any(r => WebLibraryPaths.IsWebPath(r.Path)))
            throw new InvalidOperationException("Disconnecting left the web library's folder in the library.");
        if (!copies.Has(SmokeWebLibrary.Hash)) throw new InvalidOperationException("Disconnecting deleted the copy kept offline.");
        await copies.ClearAsync(keepKept: false);
        navigation.GoBack();
        await Settle(window);
    }
}
