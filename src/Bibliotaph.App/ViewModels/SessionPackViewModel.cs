using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>Move to section in an item's menu: the item, and where it goes (<see cref="SessionStore.NoSection"/> for before the first section).</summary>
public sealed record SectionMove(SessionItemRow Item, long SectionId, string Label);

/// <summary>
/// A session pack's page (slice 3 plan, choices 11 to 15, the mockup's session page): its title and date, its items in
/// order, numbered, under their sections, and its notes beside them. Items are reordered by Move up and Move down
/// (Alt+Up and Alt+Down) or by dragging; each opens at its pages, without moving the book's saved reading position.
/// Opening the page makes the pack the current one, which Add to session adds to.
/// </summary>
public sealed partial class SessionPackViewModel : PageViewModel
{
    /// <summary>The headings Add section suggests (choice 11).</summary>
    public static IReadOnlyList<string> SuggestedSections { get; } = ["Adventure", "Maps", "Encounters", "Rules"];

    readonly SessionsService _sessions;
    readonly SessionPages _sessionPages;
    readonly LibraryPages _pages;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly ReaderWindows _readers;
    readonly INavigationService _navigation;
    readonly ILogger<SessionPackViewModel> _log;
    readonly DispatcherTimer _notesTimer;
    int _version;
    bool _notesDirty;

    public SessionPackViewModel(SessionActions actions, SessionsService sessions, SessionPages sessionPages, LibraryPages pages, LibraryQueries queries,
        CoverImages covers, ReaderWindows readers, INavigationService navigation, ILogger<SessionPackViewModel> log)
    {
        Actions = actions;
        _sessions = sessions;
        _sessionPages = sessionPages;
        _pages = pages;
        _queries = queries;
        _covers = covers;
        _readers = readers;
        _navigation = navigation;
        _log = log;
        // Notes are saved as they are typed, once typing pauses.
        _notesTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, async (_, _) => await SaveNotesAsync(), Dispatcher.CurrentDispatcher);
        _notesTimer.Stop();
    }

    public long PackId { get; set; }

    public override Route Route => Route.SessionPack;

    public override Route NavRoute => Route.Sessions;

    public override string Section => "Binders";

    public override Route? SectionRoute => Route.Sessions;

    public override string Title => Pack?.Title ?? "Binder";

    public SessionActions Actions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Eyebrow), nameof(CountLabel))]
    public partial SessionPackInfo? Pack { get; private set; }

    /// <summary>"SATURDAY, 17 OCTOBER 2026", or "BINDER" without a date.</summary>
    public string Eyebrow => (SessionDirectory.DateLabel(Pack?.Date) ?? "Binder").ToUpperInvariant();

    /// <summary>"3 items · Your order, your notes".</summary>
    public string CountLabel => $"{SessionDirectory.CountLabel(Pack?.ItemCount ?? 0)} · Your order, your notes";

    /// <summary>Section headings and items, in the order they show.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BeginCommand))]
    public partial bool HasItems { get; private set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Gone: deleted elsewhere, or never there.</summary>
    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    partial void OnNotesChanged(string value)
    {
        if (!IsLoaded) return;
        _notesDirty = true;
        _notesTimer.Stop();
        _notesTimer.Start();
    }

    /// <summary>Raised after the rows were rebuilt with an item to focus, as after Move up, so the keyboard stays on it.</summary>
    public event EventHandler<long>? FocusItemRequested;

    public override async Task LoadAsync()
    {
        Actions.Done -= OnDone;
        Actions.Done += OnDone;
        await Actions.Directory.LoadAsync();
        // Opening the pack makes it the current one.
        await RefreshAsync(open: true);
    }

    public override void Unload()
    {
        Actions.Done -= OnDone;
        Actions.Close();
        if (_notesDirty) _ = SaveNotesAsync();
    }

    async void OnDone(object? sender, EventArgs e) => await RefreshAsync();

    public async Task RefreshAsync(bool open = false, long? focus = null)
    {
        var version = ++_version;
        try
        {
            var contents = await Task.Run(() => open ? _sessions.OpenAsync(PackId) : _sessions.GetAsync(PackId));
            if (version != _version) return;
            if (contents is null)
            {
                IsMissing = true;
                Pack = null;
                Rows.Clear();
                HasItems = false;
                IsLoaded = true;
                return;
            }
            var rows = await SessionPackRows.BuildAsync(_sessions, _queries, _covers, contents);
            if (version != _version) return;
            Pack = contents.Pack;
            if (!_notesDirty)
            {
                IsLoaded = false; // so setting the notes doesn't save them back
                Notes = contents.Pack.Notes ?? "";
            }
            Rows.Clear();
            foreach (var row in rows)
            {
                if (row is SessionItemRow item) item.Edited += OnItemEdited;
                Rows.Add(row);
            }
            HasItems = contents.Items.Count > 0;
            IsMissing = false;
            IsLoaded = true;
            if (focus is { } id) FocusItemRequested?.Invoke(this, id);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Showing session pack {PackId} failed", PackId);
        }
    }

    async void OnItemEdited(object? sender, EventArgs e)
    {
        if (sender is not SessionItemRow row) return;
        await RunAsync(() => _sessions.UpdateItemAsync(row.Id, row.Label, row.Note), "Saving the item failed");
    }

    async Task SaveNotesAsync()
    {
        _notesTimer.Stop();
        if (!_notesDirty) return;
        _notesDirty = false;
        var (pack, notes) = (PackId, Notes);
        await RunAsync(() => _sessions.SetNotesAsync(pack, notes), "Saving the session's notes failed");
    }

    /// <summary>Opens an item at its pages in the main window (choice 14). The book's saved reading position stays as it was.</summary>
    [RelayCommand]
    async Task OpenItem(SessionItemRow row)
    {
        if (await RequestAsync(row) is { } request) _readers.OpenInMainWindow(request);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    async Task OpenItemInNewWindow(SessionItemRow row)
    {
        if (await RequestAsync(row) is { } request) await _readers.OpenInNewWindowAsync(request);
    }

    async Task<ViewerRequest?> RequestAsync(SessionItemRow row)
    {
        if (!row.CanOpen)
        {
            Actions.Say(row.Reason ?? "This item can't be opened right now.");
            return null;
        }
        return await SessionPackRows.RequestAsync(row, Pack?.Title ?? "", _queries);
    }

    [RelayCommand]
    Task MoveUp(SessionItemRow row) => MoveAsync(row, -1);

    [RelayCommand]
    Task MoveDown(SessionItemRow row) => MoveAsync(row, 1);

    async Task MoveAsync(SessionItemRow row, int by)
    {
        if (await RunAsync(() => _sessions.MoveItemAsync(row.Id, by), "Moving the item failed")) await RefreshAsync(focus: row.Id);
    }

    [RelayCommand]
    async Task MoveToSection(SectionMove move)
    {
        if (await RunAsync(() => _sessions.MoveItemToAsync(move.Item.Id, move.SectionId, int.MaxValue), "Moving the item failed")) await RefreshAsync(focus: move.Item.Id);
    }

    /// <summary>A drop (choice 12): the dragged item takes the place of the item it was dropped on, or goes first under a heading.</summary>
    public async Task DropAsync(SessionItemRow dragged, object target)
    {
        var (section, index) = target switch
        {
            SessionItemRow item when item.Id != dragged.Id => (item.Item.SectionId ?? SessionStore.NoSection, item.Item.Position),
            SessionSectionRow heading => (heading.Id, 0),
            _ => (-1L, 0),
        };
        if (section < 0) return;
        if (await RunAsync(() => _sessions.MoveItemToAsync(dragged.Id, section, index), "Moving the item failed")) await RefreshAsync(focus: dragged.Id);
    }

    [RelayCommand]
    Task RemoveItem(SessionItemRow row) => Actions.RemoveAsync([row.Id], row.Heading, Pack?.Title ?? "the binder");

    [RelayCommand]
    void ShowInFolder(SessionItemRow row)
    {
        if (row.Target.LastPath is not { } path) return;
        if (File.Exists(path)) DownloadCheckViewModel.ShowInFolder(path);
        else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder)) DownloadCheckViewModel.ShowInFolder(folder);
        else Actions.Say($"{path} can't be reached right now.");
    }

    [RelayCommand]
    Task AddSuggestedSection(string name) => Actions.AddSectionAsync(PackId, name);

    [RelayCommand]
    void AddSection() => Actions.AddSection(PackId);

    [RelayCommand]
    void RenameSection(SessionSectionRow row) => Actions.RenameSection(row.Id, row.Name);

    [RelayCommand]
    Task DeleteSection(SessionSectionRow row) => Actions.DeleteSectionAsync(row.Id, row.Name);

    [RelayCommand]
    void Edit()
    {
        if (Pack is { } pack) Actions.Edit(pack);
    }

    [RelayCommand]
    async Task Duplicate()
    {
        if (Pack is { } pack && await Actions.DuplicateAsync(pack) is { } copy) _sessionPages.Open(copy.Id);
    }

    /// <summary>Delete: back to Sessions, which says so, with Undo. The books stay.</summary>
    [RelayCommand]
    async Task Delete()
    {
        if (Pack is not { } pack) return;
        if (_notesDirty) await SaveNotesAsync();
        if (await Actions.DeleteAsync(pack) is not { } deleted) return;
        _sessionPages.OpenList();
        if (_navigation.Current is SessionsViewModel sessions) sessions.ShowDeleted(deleted);
    }

    /// <summary>Begin session (choice 16): run mode, at the first item.</summary>
    [RelayCommand(CanExecute = nameof(HasItems))]
    void Begin() => _sessionPages.Begin(PackId);

    /// <summary>Find resources (the mockup): the Library, to add books and pages from.</summary>
    [RelayCommand]
    void FindResources() => _pages.Open(null);

    /// <summary>The Library limited to this pack's books, to edit or tidy them together.</summary>
    [RelayCommand]
    void ShowBooks()
    {
        if (Pack is { } pack) _pages.Open(SessionDirectory.ScopeFor(pack));
    }

    [RelayCommand]
    void Escape() => Actions.Dialog?.CancelCommand.Execute(null);

    async Task<bool> RunAsync(Func<Task> action, string failure) => await RunAsync(async () => { await action(); return true; }, failure);

    async Task<bool> RunAsync(Func<Task<bool>> action, string failure)
    {
        try
        {
            return await Task.Run(action);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Failure}", failure);
            Actions.Say("That didn't work. The log has the details.");
            return false;
        }
    }
}
