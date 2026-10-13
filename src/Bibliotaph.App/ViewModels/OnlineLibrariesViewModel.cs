using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using Bibliotaph.App.Services;
using Bibliotaph.App.Views;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Settings &gt; Online libraries: the services a library page can be read from, the preview of what importing it
/// does, the import with Undo, and the books from earlier imports the page no longer lists. With them, how books owned
/// online show and open, which were in Settings &gt; Library, so everything about online libraries is in one place. One
/// section for the whole app, so a preview is still here after a look at the Library.
/// </summary>
public sealed partial class OnlineLibrariesViewModel(OnlineImports imports, ElsewhereService elsewhere, OnlineBrowsers browsers,
    INavigationService navigation, SettingsLinks links, SiteIconImages siteIcons, OnlineReaders onlineReaders,
    ILogger<OnlineLibrariesViewModel> log) : SettingsSectionViewModel
{
    OnlinePage? _page;

    /// <summary>The service whose browser read the page shown, so its next page adds to it.</summary>
    string? _fromBrowser;
    Func<string, CancellationToken, Task<byte[]?>>? _covers;
    Func<Task>? _undo;

    public override SettingsSection Section => SettingsSection.OnlineLibraries;

    public override string Label => "Online libraries";

    /// <summary>The services, in the order the readers are listed.</summary>
    public IReadOnlyList<OnlineServiceViewModel> Services => field ??= [.. imports.Readers.Select(r => new OnlineServiceViewModel(r, this))];

    /// <summary>The preview, grouped by what importing does, new books first.</summary>
    public ObservableCollection<OnlineGroupViewModel> Groups { get; } = [];

    /// <summary>Books from earlier imports the page doesn't list any more, each with Remove from library.</summary>
    public ObservableCollection<MissingBookViewModel> Missing { get; } = [];

    /// <summary>The plan the preview shows: its reader, its rows and the books missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial OnlineImportPlan? Plan { get; private set; }

    public bool HasPlan => Plan is not null;

    public bool HasMissing => Missing.Count > 0;

    /// <summary>"Your D&amp;D Beyond library: 30 books. 2 books shared with you were left out."</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>Why a page couldn't be read.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    /// <summary>What the last import or removal did, with Undo beside it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    public partial string? Message { get; private set; }

    public bool CanUndo => Message is not null && _undo is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(UndoCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>"Import 12 books", for the books ticked.</summary>
    public string ImportLabel => Chosen.Count switch
    {
        0 => "Import",
        1 => "Import 1 book",
        var n => $"Import {n.ToString("N0", CultureInfo.CurrentCulture)} books",
    };

    IReadOnlyList<OnlineImportRow> Chosen => [.. Groups.SelectMany(g => g.Items).Where(i => i.CanChoose && i.IsChosen).Select(i => i.Row)];

    [RelayCommand]
    Task ImportSavedPageAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a library page you saved",
            Filter = "Web pages (*.html;*.htm)|*.html;*.htm",
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? ReadSavedPageAsync(dialog.FileName) : Task.CompletedTask;
    }

    /// <summary>
    /// Reads a library page saved with Save as, Webpage complete, and shows what importing it does. Its covers are the
    /// images saved beside it.
    /// </summary>
    public async Task ReadSavedPageAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        Problem = null;
        try
        {
            var saved = await Task.Run(() => imports.ReadSavedPageAsync(path));
            if (saved is null)
            {
                Problem = "That page isn't a library page Bibliotaph can read. Open your library on the service, sign in if asked, "
                    + "and save the page with Save as, choosing Webpage, complete.";
                return;
            }
            _page = saved.Page;
            _fromBrowser = null;
            _covers = imports.SavedCovers(saved.Images);
            Message = null;
            _undo = null;
            await ShowPlanAsync(saved.Reader, saved.Page);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "A saved library page couldn't be read");
            Problem = $"The page couldn't be read: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Opens the service in Bibliotaph's browser at its library page, or at <paramref name="start"/> (the smoke test's
    /// made-up page). Null, with the reason shown, when the browser can't run here.
    /// </summary>
    public OnlineBrowserWindow? OpenBrowser(IOnlineLibraryReader reader, Uri? start = null)
    {
        if (OnlineBrowsers.RuntimeVersion is null)
        {
            Problem = "Bibliotaph's browser needs the Microsoft Edge WebView2 Runtime, which Windows 11 includes. Install it from Microsoft, "
                + "or import a page you saved instead.";
            return null;
        }
        Problem = null;
        return browsers.Open(reader, (page, covers) => ReadBrowserPageAsync(reader, page, covers), start);
    }

    /// <summary>
    /// Read this page in the browser: the books read show in the preview here, added to those of the page read before
    /// in the same browser, so a library over several pages builds up. Says what happened, for the browser to show.
    /// </summary>
    async Task<string> ReadBrowserPageAsync(IOnlineLibraryReader reader, OnlinePage page, Func<string, CancellationToken, Task<byte[]?>> covers)
    {
        var combined = _fromBrowser == reader.Key && _page is { } before ? before.With(page) : page;
        _page = combined;
        _fromBrowser = reader.Key;
        _covers = covers;
        Message = null;
        _undo = null;
        Problem = null;
        await ShowPlanAsync(reader, combined);
        if (navigation.Current is not SettingsViewModel || !IsSelected) links.Open(SettingsSection.OnlineLibraries);
        Application.Current.MainWindow?.Activate();
        var books = combined.Books.Count == 1 ? "1 book" : $"{combined.Books.Count.ToString("N0", CultureInfo.CurrentCulture)} books";
        return $"Read {books}. Bibliotaph shows what importing them does; nothing is imported until you press Import there.";
    }

    /// <summary>The folder store downloads go to, or null for the browser's own Downloads folder.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadsLine), nameof(HasDownloadsFolder))]
    public partial string? DownloadsFolder { get; private set; }

    public bool HasDownloadsFolder => DownloadsFolder is not null;

    public string DownloadsLine => DownloadsFolder ?? "Not chosen: downloads go to your Downloads folder, outside your library.";

    /// <summary>Why the folder picked can't be the downloads folder.</summary>
    [ObservableProperty]
    public partial string? DownloadsProblem { get; private set; }

    public override async Task LoadAsync()
    {
        try
        {
            DownloadsFolder = await browsers.Downloads.FolderAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the downloads folder failed");
        }
        _loadingSettings = true;
        try
        {
            ShowSiteIcons = await siteIcons.IsOnAsync();
            ReadOnlineInApp = await onlineReaders.OpensInAppAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the online library settings failed");
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    bool _loadingSettings;

    /// <summary>
    /// Show site icons: a book owned online shows its site's icon in its badge, fetched once from the site. Off, it
    /// shows a plain cloud and nothing is fetched.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowSiteIcons { get; set; } = true;

    /// <summary>
    /// Read books owned online in Bibliotaph: Open shows the service's page in the online reader. Off, it opens in the
    /// everyday browser.
    /// </summary>
    [ObservableProperty]
    public partial bool ReadOnlineInApp { get; set; } = true;

    async partial void OnShowSiteIconsChanged(bool value)
    {
        if (_loadingSettings) return;
        try
        {
            await siteIcons.SetOnAsync(value);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving the site icons setting failed");
        }
    }

    async partial void OnReadOnlineInAppChanged(bool value)
    {
        if (_loadingSettings) return;
        try
        {
            await onlineReaders.SetOpensInAppAsync(value);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Saving the online reader setting failed");
        }
    }

    /// <summary>Choose a downloads folder: a library folder or one inside it, so what is downloaded joins the library.</summary>
    [RelayCommand]
    async Task ChooseDownloadsFolderAsync()
    {
        if (LibraryFolders.PickOne("Choose where store downloads go") is not { } folder) return;
        await UseDownloadsFolderAsync(folder);
    }

    /// <summary>Makes <paramref name="folder"/> the downloads folder, or says why it can't be (the smoke test picks it here).</summary>
    public async Task UseDownloadsFolderAsync(string folder)
    {
        try
        {
            DownloadsProblem = await browsers.Downloads.SetFolderAsync(folder);
            if (DownloadsProblem is null) DownloadsFolder = folder;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Setting the downloads folder failed");
            DownloadsProblem = "The downloads folder couldn't be saved. The log has the details.";
        }
    }

    [RelayCommand]
    async Task ClearDownloadsFolderAsync()
    {
        try
        {
            await browsers.Downloads.ClearFolderAsync();
            (DownloadsFolder, DownloadsProblem) = (null, null);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Clearing the downloads folder failed");
        }
    }

    /// <summary>Sign out of all services: the browser forgets every sign-in it kept.</summary>
    [RelayCommand]
    async Task SignOutAsync()
    {
        if (OnlineBrowsers.RuntimeVersion is null) return;
        try
        {
            await browsers.SignOutAsync();
            _undo = null;
            Message = "Bibliotaph's browser is signed out of every service.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            log.LogWarning(ex, "Signing the browser out failed");
            Problem = $"The browser couldn't sign out: {ex.Message}";
        }
    }

    async Task ShowPlanAsync(IOnlineLibraryReader reader, OnlinePage page)
    {
        var plan = await Task.Run(() => imports.PlanAsync(reader, page));
        Groups.Clear();
        foreach (var outcomes in plan.Rows.GroupBy(r => r.Outcome).OrderBy(g => g.Key))
        {
            var group = new OnlineGroupViewModel(outcomes.Key, reader);
            foreach (var row in outcomes) group.Items.Add(new OnlineRowViewModel(row, this));
            Groups.Add(group);
        }
        Missing.Clear();
        foreach (var book in plan.Missing) Missing.Add(new MissingBookViewModel(book, this));
        OnPropertyChanged(nameof(HasMissing));

        static string Books(int n) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {(n == 1 ? "book" : "books")}";
        var shared = page.Shared switch
        {
            0 => "",
            1 => " 1 book shared with you was left out.",
            var n => $" {Books(n)} shared with you were left out.",
        };
        Summary = $"Your {reader.Name} library: {Books(plan.Rows.Count)}.{shared}";
        Plan = plan;
        ChoiceChanged();
    }

    internal void ChoiceChanged()
    {
        OnPropertyChanged(nameof(ImportLabel));
        ImportCommand.NotifyCanExecuteChanged();
    }

    bool CanImport() => !IsBusy && Plan is not null && Chosen.Count > 0;

    /// <summary>Imports the books ticked, in one go, then shows the page again as it now stands.</summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    async Task ImportAsync()
    {
        if (Plan is not { } plan || _page is not { } page) return;
        IsBusy = true;
        try
        {
            var chosen = Chosen;
            var result = await Task.Run(() => imports.ImportAsync(plan, chosen, _covers));
            _undo = async () =>
            {
                await Task.Run(() => imports.UndoAsync(result.Undo));
                await ShowPlanAsync(plan.Reader, page);
            };
            Message = (result.Added, result.Changed) switch
            {
                (0, 0) => "Nothing needed changing.",
                (var added, 0) => $"Added {Count(added, "book")} owned on {plan.Reader.Name}.",
                (0, var changed) => $"Updated {Count(changed, "card")} already in your library.",
                var (added, changed) => $"Added {Count(added, "book")} owned on {plan.Reader.Name} and updated {Count(changed, "card")} already in your library.",
            };
            await ShowPlanAsync(plan.Reader, page);
        }
        finally
        {
            IsBusy = false;
        }
    }

    static string Count(int n, string noun) => $"{n.ToString("N0", CultureInfo.CurrentCulture)} {noun}{(n == 1 ? "" : "s")}";

    [RelayCommand(CanExecute = nameof(CanRunUndo))]
    async Task UndoAsync()
    {
        if (_undo is not { } undo) return;
        IsBusy = true;
        try
        {
            _undo = null;
            await undo();
            Message = null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanRunUndo() => !IsBusy;

    /// <summary>Removes a book the library page no longer lists, with Undo (never on its own).</summary>
    internal async Task RemoveAsync(MissingBookViewModel book)
    {
        if (await elsewhere.RemoveAsync(book.Book.EntryId) is not { } removed) return;
        Missing.Remove(book);
        OnPropertyChanged(nameof(HasMissing));
        _undo = async () =>
        {
            await elsewhere.UndoRemoveAsync(removed);
            if (Plan is { } plan && _page is { } page) await ShowPlanAsync(plan.Reader, page);
        };
        Message = $"Removed {book.Title} from your library.";
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>A field's name as the preview says it: "Edition".</summary>
    internal static string Describe(IReadOnlyList<MetadataField> fields) => string.Join(", ", fields.Select(f => f.Label));
}

/// <summary>A service in Settings &gt; Online libraries: its name, and opening it in the browser.</summary>
public sealed partial class OnlineServiceViewModel(IOnlineLibraryReader reader, OnlineLibrariesViewModel page)
{
    public IOnlineLibraryReader Reader { get; } = reader;

    public string Name => Reader.Name;

    public string OpenLabel => $"Open {Name}";

    public string Explanation => Reader.LinkField == MetadataFields.AlsoOwn
        ? $"Opens {Name} in Bibliotaph's own browser. Sign in there if it asks, open your library and press Read this page. "
            + "Only books you own are imported, each opening on " + Name + ", and Bibliotaph never sees your password. "
            + "A page saved with Save page or Save as (Webpage, complete) works too."
        : $"Opens {Name} in Bibliotaph's own browser. Sign in there if it asks, open your library and press Read this page: books you haven't "
            + $"downloaded become books to get from {Name}. Its own Download buttons save into your downloads folder below, where Bibliotaph reads each file. "
            + "A saved page works too." + (Reader is DriveThruRpgReader ? " Your DMs Guild books are in the same library." : "");

    [RelayCommand]
    void Open() => page.OpenBrowser(Reader);
}

/// <summary>The preview's books with one outcome.</summary>
public sealed class OnlineGroupViewModel(OnlineOutcome outcome, IOnlineLibraryReader reader)
{
    public OnlineOutcome Outcome { get; } = outcome;

    public string Label { get; } = outcome switch
    {
        OnlineOutcome.New => "New",
        OnlineOutcome.AddToCard => "Already in your library",
        OnlineOutcome.Update => "Imported before, with more to fill in",
        _ => "Imported before",
    };

    public string Explanation { get; } = outcome switch
    {
        OnlineOutcome.New => reader.LinkField == MetadataFields.AlsoOwn
            ? $"Each becomes a book owned elsewhere, opening on {reader.Name}."
            : $"Each becomes a book to get from {reader.Name}, until you download its file.",
        OnlineOutcome.AddToCard => $"A card with its title is here already: it gets {reader.Name} and the link, rather than a second card.",
        OnlineOutcome.Update => "Only fields that are empty are filled. Nothing you have set changes.",
        _ => "Nothing to change.",
    };

    public ObservableCollection<OnlineRowViewModel> Items { get; } = [];

    public string Heading => $"{Label} · {Items.Count.ToString("N0", CultureInfo.CurrentCulture)}";
}

/// <summary>A book in the preview, ticked to import unless there is nothing to do.</summary>
public sealed partial class OnlineRowViewModel(OnlineImportRow row, OnlineLibrariesViewModel page) : ObservableObject
{
    public OnlineImportRow Row { get; } = row;

    public string Title => Row.Book.Title;

    public bool CanChoose => Row.Outcome != OnlineOutcome.Unchanged;

    [ObservableProperty]
    public partial bool IsChosen { get; set; } = row.Outcome != OnlineOutcome.Unchanged;

    partial void OnIsChosenChanged(bool value) => page.ChoiceChanged();

    /// <summary>"Gilded Press · adventure · 5e · 2019", "Goes on your card The Ember Vault", "Fills Edition, Type".</summary>
    public string Detail => Row.Outcome switch
    {
        OnlineOutcome.AddToCard when Row.CardTitle != Title => $"Goes on your card {Row.CardTitle}.",
        OnlineOutcome.AddToCard => "Goes on its card.",
        OnlineOutcome.Update => $"Fills {OnlineLibrariesViewModel.Describe(Row.Fills)}.",
        _ => string.Join(" · ", new object?[] { Row.Book.Publisher, Row.Book.Type, Row.Book.Edition, Row.Book.Year }.OfType<object>()),
    };
}

/// <summary>A book from an earlier import the page doesn't list any more.</summary>
public sealed partial class MissingBookViewModel(MissingBook book, OnlineLibrariesViewModel page)
{
    public MissingBook Book { get; } = book;

    public string Title => Book.Title;

    [RelayCommand]
    Task RemoveAsync() => page.RemoveAsync(this);
}
