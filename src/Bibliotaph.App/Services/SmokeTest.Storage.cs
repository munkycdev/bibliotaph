using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
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
    /// Add menu, previewed and added, its book downloaded, read and found by a search, then the folder removed and the
    /// storage disconnected, forgetting its password.
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

        // Removing the folder and disconnecting leave nothing behind for the checks after this one.
        var folder = page.Folders.FirstOrDefault(f => f.IsWeb && f.Path.EndsWith($"› {SmokeWebDav.Folder}", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Settings > Library lists the storage folder as {string.Join(", ", page.Folders.Select(f => f.Path))}.");
        await page.RemoveCommand.ExecuteAsync(folder);
        await page.Storage.DisconnectCommand.ExecuteAsync(page.Storage.Accounts.Single());
        await WaitUntilAsync(window, () => !page.Storage.HasAccounts, () => "Disconnect left the WebDAV account listed.");
        if (services.GetRequiredService<IConnectionSecrets>().Find(account.Connection.Id) is not null)
            throw new InvalidOperationException("Disconnecting WebDAV kept its password.");
        if (services.GetRequiredService<PcDrives>().All.Count > 0) throw new InvalidOperationException("Disconnecting WebDAV left its drive.");
        navigation.GoBack();
        await Settle(window);
    }
}
