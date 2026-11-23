using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Settings › Online libraries on the Mac, as the Windows section: the services a library page can be read from, the
/// preview of what importing it does, the import with Undo, the books from earlier imports the page no longer lists,
/// and where store downloads go. The Windows one lives with its WebView2 browser, so the Mac has its own over WebKit.
/// </summary>
public sealed partial class MacOnlineLibraries(OnlineImports imports, ElsewhereService elsewhere, MacOnlineDownloads downloads,
    SettingsLinks links, IFilePicker files, ILogger<MacOnlineLibraries> log) : OnlineLibrariesSection
{
    OnlinePage? _page;

    /// <summary>The service whose browser read the page shown, so its next page adds to it.</summary>
    string? _fromBrowser;
    Func<string, CancellationToken, Task<byte[]?>>? _covers;

    public override SettingsSection Section => SettingsSection.OnlineLibraries;

    public override string Label => "Online libraries";

    /// <summary>Opens a service's browser; the Mac's window answers.</summary>
    public event EventHandler<OnlineBrowserRequest>? BrowserRequested;

    public override void OpenLibraryPage(IOnlineLibraryReader reader) => OpenBrowser(reader);

    /// <summary>Opens the service in Bibliotaph's browser at its library page, or at <paramref name="start"/> (a saved page, for checking a build).</summary>
    public void OpenBrowser(IOnlineLibraryReader reader, Uri? start = null)
    {
        Problem = null;
        BrowserRequested?.Invoke(this, new OnlineBrowserRequest(reader, (page, covers) => ReadBrowserPageAsync(reader, page, covers), start));
    }

    public MacOnlineDownloads Downloads => downloads;

    /// <summary>The services, in the order the readers are listed.</summary>
    public IReadOnlyList<MacOnlineService> Services => field ??= [.. imports.Readers.Select(r => new MacOnlineService(r, this))];

    public ObservableCollection<OnlineGroupItem> Groups { get; } = [];

    public ObservableCollection<MissingBookItem> Missing { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial OnlineImportPlan? Plan { get; private set; }

    public bool HasPlan => Plan is not null;

    public bool HasMissing => Missing.Count > 0;

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    public UndoSlot Undo { get; } = new(log);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsBusy { get; private set; }

    public string ImportLabel => OnlineImportText.ImportLabel(Chosen.Count);

    IReadOnlyList<OnlineImportRow> Chosen => [.. Groups.SelectMany(g => g.Items).Where(i => i.CanChoose && i.IsChosen).Select(i => i.Row)];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DownloadsLine), nameof(HasDownloadsFolder))]
    public partial string? DownloadsFolder { get; private set; }

    public bool HasDownloadsFolder => DownloadsFolder is not null;

    public string DownloadsLine => DownloadsFolder ?? "Not chosen: downloads go to your Downloads folder, outside your library.";

    [ObservableProperty]
    public partial string? DownloadsProblem { get; private set; }

    public override async Task LoadAsync()
    {
        try
        {
            DownloadsFolder = await downloads.FolderAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the downloads folder failed");
        }
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
        Undo.Clear();
        Problem = null;
        await ShowPlanAsync(reader, combined);
        links.Open(SettingsSection.OnlineLibraries);
        var books = combined.Books.Count == 1 ? "1 book" : $"{combined.Books.Count.ToString("N0", CultureInfo.CurrentCulture)} books";
        return $"Read {books}. Bibliotaph shows what importing them does; nothing is imported until you press Import there.";
    }

    [RelayCommand]
    async Task ChooseDownloadsFolderAsync()
    {
        if (files.PickFolder("Choose where store downloads go") is not { } folder) return;
        try
        {
            DownloadsProblem = await downloads.SetFolderAsync(folder);
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
            await downloads.ClearFolderAsync();
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
        if (SignOut is null) return;
        await SignOut();
        Undo.Say("Bibliotaph's browser is signed out of every service.");
    }

    /// <summary>Forgets the browser's sign-ins; the Mac's browser sets it.</summary>
    public Func<Task>? SignOut { get; set; }

    async Task ShowPlanAsync(IOnlineLibraryReader reader, OnlinePage page)
    {
        var plan = await Task.Run(() => imports.PlanAsync(reader, page));
        Groups.Clear();
        foreach (var outcomes in plan.Rows.GroupBy(r => r.Outcome).OrderBy(g => g.Key))
        {
            var group = new OnlineGroupItem(outcomes.Key, reader);
            foreach (var row in outcomes) group.Items.Add(new OnlineRowItem(row, this));
            Groups.Add(group);
        }
        Missing.Clear();
        foreach (var book in plan.Missing) Missing.Add(new MissingBookItem(book, this));
        OnPropertyChanged(nameof(HasMissing));

        Summary = OnlineImportText.Summary(reader, plan.Rows.Count, page.Shared);
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
            Undo.Offer(OnlineImportText.Imported(plan.Reader, result.Added, result.Changed, result.Covers), () => WhileBusyAsync(async () =>
            {
                await Task.Run(() => imports.UndoAsync(result.Undo));
                await ShowPlanAsync(plan.Reader, page);
            }));
            await ShowPlanAsync(plan.Reader, page);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Importing from {Service} failed", plan.Reader.Name);
            Problem = $"The books couldn't be imported: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    async Task WhileBusyAsync(Func<Task> undo)
    {
        IsBusy = true;
        try
        {
            await undo();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Removes a book the library page no longer lists, with Undo (never on its own).</summary>
    internal async Task RemoveAsync(MissingBookItem book)
    {
        if (await elsewhere.RemoveAsync(book.Book.EntryId) is not { } removed) return;
        Missing.Remove(book);
        OnPropertyChanged(nameof(HasMissing));
        Undo.Offer(OnlineImportText.Removed(book.Title), () => WhileBusyAsync(async () =>
        {
            await elsewhere.UndoRemoveAsync(removed);
            if (Plan is { } plan && _page is { } page) await ShowPlanAsync(plan.Reader, page);
        }));
    }
}

/// <summary>A service's browser to open, and what to do with a page read in it.</summary>
public sealed record OnlineBrowserRequest(IOnlineLibraryReader Reader, Func<OnlinePage, Func<string, CancellationToken, Task<byte[]?>>, Task<string>> Read,
    Uri? Start = null);

/// <summary>A service in Settings › Online libraries: its name, and opening it in the browser.</summary>
public sealed partial class MacOnlineService(IOnlineLibraryReader reader, MacOnlineLibraries page)
{
    public IOnlineLibraryReader Reader { get; } = reader;

    public string Name => Reader.Name;

    public string OpenLabel => $"Open {Name}";

    /// <summary>Its books are read on its own site, rather than bought from it as files.</summary>
    public bool ReadOnline => Reader.LinkField == MetadataFields.AlsoOwn;

    public string Explanation => ReadOnline
        ? $"Opens {Name} in Bibliotaph's own browser. Sign in there if it asks, open your library and press Read this page. "
            + "Only books you own are imported, each opening on " + Name + ", and Bibliotaph never sees your password."
        : $"Opens {Name} in Bibliotaph's own browser. Sign in there if it asks, open your library and press Read this page: books you haven't "
            + $"downloaded become books to get from {Name}. Its own Download buttons save into your downloads folder below, where Bibliotaph reads each file."
            + (Reader is DriveThruRpgReader ? " Your DMs Guild books are in the same library." : "");

    [RelayCommand]
    void Open() => page.OpenBrowser(Reader);
}

/// <summary>The preview's books with one outcome.</summary>
public sealed class OnlineGroupItem(OnlineOutcome outcome, IOnlineLibraryReader reader)
{
    public OnlineOutcome Outcome { get; } = outcome;

    public string Explanation { get; } = OnlineImportText.GroupExplanation(outcome, reader);

    public ObservableCollection<OnlineRowItem> Items { get; } = [];

    public string Heading => OnlineImportText.GroupHeading(Outcome, Items.Count);
}

/// <summary>A book in the preview, ticked to import unless there is nothing to do.</summary>
public sealed partial class OnlineRowItem(OnlineImportRow row, MacOnlineLibraries page) : ObservableObject
{
    public OnlineImportRow Row { get; } = row;

    public string Title => Row.Book.Title;

    public bool CanChoose => Row.Outcome != OnlineOutcome.Unchanged;

    [ObservableProperty]
    public partial bool IsChosen { get; set; } = row.Outcome != OnlineOutcome.Unchanged;

    partial void OnIsChosenChanged(bool value) => page.ChoiceChanged();

    public string Detail => OnlineImportText.Detail(Row);
}

/// <summary>A book from an earlier import the page doesn't list any more.</summary>
public sealed partial class MissingBookItem(MissingBook book, MacOnlineLibraries page)
{
    public MissingBook Book { get; } = book;

    public string Title => Book.Title;

    [RelayCommand]
    Task RemoveAsync() => page.RemoveAsync(this);
}
