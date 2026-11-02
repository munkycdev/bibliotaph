using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Desktop;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>
/// Storage connected on this PC, through a stand-in WebDAV server (<see cref="SmokeWebDav"/>): the OAuth providers
/// need a browser and a real account, but every provider's folders go through the same picker, scan and download.
/// </summary>
static partial class SmokeTest
{
    /// <summary>
    /// WebDAV connected in Settings > Library with its address and password, a folder in it picked from the Library's
    /// Add menu, previewed and added, its book downloaded, read and found by a search, opened (keeping a copy to read
    /// again) and kept offline, then the folder removed and the storage disconnected, forgetting its password.
    /// </summary>
    static async Task ConnectStorageAsync(IServiceProvider services, Window window)
    {
        var page = (LibrarySectionViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Library)).Selected;
        var storage = page.Storage;
        await WaitUntilAsync(window, () => storage is { HasAccounts: false, Options: [{ IsPrimary: true }, ..] }
            && storage.Options.Any(o => o.Kind.Id == "webdav")
            && Descendants<Button>(window).Any(b => b.Command == storage.ConnectCommand && b.IsVisible),
            () => $"Settings > Library's storage offers {string.Join(", ", storage.Options.Select(o => o.Label))} with {storage.Accounts.Count} connected.");
        await AuditAsync(window, "Settings > Library, no storage connected");

        Click(Descendants<Button>(window).First(b => b.Command == storage.ConnectCommand && b.CommandParameter is StorageConnectOption { Kind.Id: "webdav" }),
            "Connect WebDAV");
        await WaitUntilAsync(window, () => storage.IsWebDavOpen && Descendants<TextBox>(window).Any(t => t.Name == "WebDavAddress" && t.IsVisible),
            () => "Connect WebDAV didn't show its address and password.");
        storage.WebDavAddress = SmokeWebDav.Address;
        storage.WebDavUser = SmokeWebDav.User;
        Descendants<PasswordBox>(window).First(p => p.Name == "WebDavPassword").Password = SmokeWebDav.Password;
        await AuditAsync(window, "Settings > Library, connecting WebDAV");
        Click(Descendants<Button>(window).First(b => b.Command == storage.ConnectWebDavCommand && b.IsVisible), "Connect");
        await WaitUntilAsync(window, () => storage is { HasAccounts: true, IsWebDavOpen: false, IsConnecting: false }
            && Descendants<Button>(window).Any(b => b.Command == storage.DisconnectCommand && b.IsVisible),
            () => $"WebDAV wasn't connected: {(storage.WebDavProblem.Length > 0 ? storage.WebDavProblem : "no problem shown")}.");
        var account = storage.Accounts.Single();
        if (account is { NeedsReconnect: true } || account.Allowed.Length == 0)
            throw new InvalidOperationException($"The connected WebDAV account shows \"{account.Detail}\" and \"{account.Allowed}\".");
        if (services.GetRequiredService<IConnectionSecrets>().Find(account.Connection.Id) is null)
            throw new InvalidOperationException("Connecting WebDAV kept no password.");

        // The Library's Add menu opens the picker in Settings > Library.
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        library.AddFromStorageCommand.Execute(null);
        page = (LibrarySectionViewModel)(await ExpectSettingsAsync(window, shell, SettingsSection.Library, "Add a folder from storage")).Selected;
        StorageFolderPickerViewModel? picker = null;
        await WaitUntilAsync(window, () => (picker = page.Picker) is { IsLoading: false, Folders: [{ Name: SmokeWebDav.Folder }] }
            && Descendants<Border>(window).Any(b => b.Name == "StoragePicker" && b.IsVisible),
            () => $"The storage picker shows {(picker is null ? "nothing" : $"{picker.Folders.Count} folders, {picker.Problem}")}.");
        await AuditAsync(window, "Settings > Library, picking a folder in storage");
        Click(Descendants<Button>(window).First(b => b.Command == picker!.OpenCommand && b.CommandParameter is StorageFolderStep { Name: SmokeWebDav.Folder }),
            $"Open {SmokeWebDav.Folder}");
        await WaitUntilAsync(window, () => picker! is { IsLoading: false, IsEmpty: true, Crumbs.Count: 2 }, () => $"Opening {SmokeWebDav.Folder} shows {picker!.Crumbs.Count} crumbs.");
        Click(Descendants<Button>(window).First(b => b.Command == picker!.UseCommand && b.IsVisible), "Use this folder");
        PendingFolder? pending = null;
        await WaitUntilAsync(window, () => page is { IsPicking: false } && (pending = page.Pending.SingleOrDefault()) is { CanAdd: true }
            && pending.Shown.EndsWith($"› {SmokeWebDav.Folder}", StringComparison.Ordinal)
            && pending.Detail.StartsWith("1 PDF", StringComparison.Ordinal),
            () => $"The storage folder's preview says \"{pending?.Shown}\": \"{pending?.Detail}\".");
        var path = pending!.Path;
        if (!StoragePath.IsStoragePath(path)) throw new InvalidOperationException($"The storage folder is added as {path}.");
        Click(Descendants<Button>(window).First(b => b.Command == page.ConfirmAddCommand && ReferenceEquals(b.CommandParameter, pending)), "Add");

        var queries = services.GetRequiredService<LibraryQueries>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 90;
        LibraryEntry? book;
        while ((book = (await Task.Run(() => queries.ListAsync(new LibraryFilter()))).FirstOrDefault(e => e.Title == SmokeWebDav.Title)) is not { Searchable: true })
        {
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException($"The storage folder's book wasn't listed and read ({(book is null ? "no card" : "not searchable")}).");
            await Settle(window);
            await Task.Delay(200);
        }
        var found = await Task.Run(() => queries.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse($"\"{SmokeWebDav.Phrase}\"")), new LibraryFilter()));
        if (!found.Entries.Any(e => e.Pages.Any(p => p.DocumentId == book.DocumentId && p.PdfPage == 1)))
            throw new InvalidOperationException("A search doesn't find the storage folder's book at its second page.");

        // Opened, it's downloaded to the copies for reading, where Keep offline in its details keeps it.
        var copies = services.GetRequiredService<WebLibraryCopies>();
        var hash = (await services.GetRequiredService<LibraryStore>().GetSourceAsync(book.DocumentId))!.ContentHash;
        if (copies.Has(hash)) throw new InvalidOperationException("Indexing the storage folder's book left a copy among the books downloaded to read.");
        services.GetRequiredService<ReaderWindows>().OpenInMainWindow(new ViewerRequest(book.DocumentId, SmokeWebDav.Title, 1));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The storage folder's book didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, () => $"The storage folder's book didn't open (still {viewer.Mode}).");
        if (!viewer.IsPdf) throw new InvalidOperationException($"The storage folder's book didn't open: {viewer.EmptyTitle} {viewer.EmptyMessage}");
        if (!copies.Has(hash)) throw new InvalidOperationException("Opening the storage folder's book kept no copy of it.");
        navigation.GoBack();
        await Settle(window);

        navigation.NavigateTo(Route.Library);
        library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await library.RefreshAsync();
        await WaitUntilAsync(window, () => library.Items.Any(i => i.DocumentId == book.DocumentId), () => "The storage folder's book isn't in the Library.");
        await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == book.DocumentId));
        library.Inspector!.Pane!.Where = true;
        await WaitUntilAsync(window, () => library.Inspector is { CanKeepOffline: true, Locations.Count: > 0 }
            && Descendants<CheckBox>(window).Any(c => c.Name == "KeepOffline" && c.IsVisible),
            () => "The storage folder's book's details offer no Keep offline.");
        var inspector = library.Inspector!;
        if (inspector.Locations[0] is not { IsWeb: true, CanShowInExplorer: false } location
            || !location.Path.EndsWith($"› {SmokeWebDav.Folder} › {SmokeWebDav.Title}.pdf", StringComparison.Ordinal))
            throw new InvalidOperationException($"The details show {inspector.Locations[0]} for the storage folder's book.");
        ((IToggleProvider)new CheckBoxAutomationPeer(Descendants<CheckBox>(window).First(c => c.Name == "KeepOffline")).GetPattern(PatternInterface.Toggle)).Toggle();
        await WaitUntilAsync(window, () => inspector is { IsKeptOffline: true, OfflineText: "Kept on this PC, so it opens without a connection." },
            () => $"Keep offline says \"{inspector.OfflineText}\".");
        await AuditAsync(window, "a storage book's details, kept offline");
        library.CloseDetailsCommand.Execute(null);
        await Settle(window);

        // Removing the folder and disconnecting leave nothing behind for the checks after this one.
        page = (LibrarySectionViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Library)).Selected;
        await WaitUntilAsync(window, () => page.Storage.HasAccounts, () => "Settings > Library lost the WebDAV account.");
        var folder = page.Folders.FirstOrDefault(f => f.IsWeb && f.Path.EndsWith($"› {SmokeWebDav.Folder}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Settings > Library lists the storage folder as {string.Join(", ", page.Folders.Select(f => f.Path))}.");
        await page.RemoveCommand.ExecuteAsync(folder);
        await page.Storage.DisconnectCommand.ExecuteAsync(page.Storage.Accounts.Single());
        await WaitUntilAsync(window, () => !page.Storage.HasAccounts, () => "Disconnect left the WebDAV account listed.");
        if (services.GetRequiredService<IConnectionSecrets>().Find(account.Connection.Id) is not null)
            throw new InvalidOperationException("Disconnecting WebDAV kept its password.");
        if (services.GetRequiredService<PcDrives>().All.Count > 0) throw new InvalidOperationException("Disconnecting WebDAV left its drive.");
        await copies.ClearAsync(keepKept: false);
        navigation.GoBack();
        await Settle(window);
    }
}
