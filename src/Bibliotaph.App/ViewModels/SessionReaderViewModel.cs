using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Reading;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A binder's page: its pages listed down the left, numbered under their sections, where a book's chapters sit in the
/// reader; the reader shows the page in hand, with every other page of the book still reachable; the binder's notes
/// sit in a panel on the right that folds away. Next and Previous (Ctrl+Right and Ctrl+Left) step through the pages,
/// which are renamed in the list (F2). Edit binder opens the list page for sections, order and notes. Pop out moves
/// the whole binder to a window of its own, one window per binder. At hand opens a page of a binder kept at hand in a
/// reader window, so the page in hand stays put. When the next page is in another book, that book is opened ahead of
/// time with the page drawn, so Next shows it at once.
/// </summary>
public sealed partial class SessionReaderViewModel : PageViewModel
{
    readonly SessionsService _sessions;
    readonly SessionPages _sessionPages;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly ReaderWindows _readers;
    readonly OnlineReaders _online;
    readonly SessionDirectory _directory;
    readonly PinDirectory _pins;
    readonly ILogger<SessionReaderViewModel> _log;
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    readonly UiTimer _notesTimer;
    int _version;
    int _ownWrites;
    bool _notesDirty;
    bool _loadingNotes;
    long? _currentId;
    ViewerRequest? _startAt;

    public SessionReaderViewModel(SessionsService sessions, SessionPages sessionPages, LibraryQueries queries, CoverImages covers, ReaderWindows readers,
        SessionDirectory directory, PinDirectory pins, OnlineReaders online, TimeProvider time, ILogger<SessionReaderViewModel> log)
    {
        _online = online;
        _directory = directory;
        _pins = pins;
        _sessions = sessions;
        _sessionPages = sessionPages;
        _queries = queries;
        _covers = covers;
        _readers = readers;
        _log = log;
        _notesTimer = new UiTimer(time, TimeSpan.FromMilliseconds(700), repeat: false, SaveNotesAsync);
    }

    public long PackId { get; set; }

    /// <summary>
    /// True for a binder in a pop-out window: its reader leases a viewer worker of its own. Set before the page is
    /// shown, as the reader is made the first time it's asked for.
    /// </summary>
    public bool IsPoppedOut { get; set; }

    public override Route Route => Route.SessionPack;

    public override Route NavRoute => Route.Sessions;

    public override string? PinKey => PinDirectory.BinderKey(PackId);

    /// <summary>Pin or Unpin: the binder in the sidebar's Pinned group.</summary>
    public string PinLabel => _pins.PinLabel(PinTarget.Binder(PackId));

    [RelayCommand]
    Task TogglePin() => _pins.ToggleAsync(PinTarget.Binder(PackId));

    void OnPinsChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(PinLabel));

    public override string Section => "Binders";

    public override Route? SectionRoute => Route.Sessions;

    public override string Title => Pack?.Title ?? "Binder";

    public override bool ScrollsItself => true;

    /// <summary>The reader for the page in hand.</summary>
    public ViewerViewModel Viewer => field ??= CreateViewer();

    /// <summary>
    /// The item in hand when it is a page owned online: the online reader, in the reader's place, so Next and Previous
    /// step between files and pages online alike. Null for a file, or when such pages open in the everyday browser.
    /// </summary>
    [ObservableProperty]
    public partial OnlineReaderViewModel? Online { get; private set; }

    ViewerViewModel CreateViewer()
    {
        var viewer = _readers.Create(null, poppedOut: IsPoppedOut, inBinder: true);
        viewer.PropertyChanged += OnViewerChanged;
        viewer.Notes.Items.CollectionChanged += (_, _) => RefreshItemNotes();
        return viewer;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Eyebrow))]
    [NotifyCanExecuteChangedFor(nameof(AddPageCommand))]
    public partial SessionPackInfo? Pack { get; private set; }

    /// <summary>"SATURDAY, 17 OCTOBER 2026", or "BINDER" without a date.</summary>
    public string Eyebrow => (SessionDirectory.DateLabel(Pack?.Date) ?? "Binder").ToUpperInvariant();

    /// <summary>Section headings and items, in the order they show.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>The items alone, in order, for Next and Previous.</summary>
    IReadOnlyList<SessionItemRow> Items => [.. Rows.OfType<SessionItemRow>()];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Counter), nameof(IsOutsideItem))]
    [NotifyCanExecuteChangedFor(nameof(NextItemCommand), nameof(PreviousItemCommand), nameof(BackToItemCommand))]
    public partial SessionItemRow? Current { get; private set; }

    partial void OnCurrentChanged(SessionItemRow? oldValue, SessionItemRow? newValue)
    {
        oldValue?.IsCurrent = false;
        newValue?.IsCurrent = true;
        RefreshItemNotes();
    }

    /// <summary>The page notes on the item in hand: on its pages, or all of them for a whole book.</summary>
    public ObservableCollection<PageNoteRow> ItemNotes { get; } = [];

    [ObservableProperty]
    public partial bool HasItemNotes { get; private set; }

    void RefreshItemNotes()
    {
        ItemNotes.Clear();
        if (Current is { Target: { DocumentId: { } documentId } target } current)
        {
            foreach (var note in Viewer.Notes.Items)
                if (note.Note.EntryId == current.Item.EntryId && (current.Item.Range is null || note.Overlaps(documentId, target.FirstPage, target.LastPage)))
                    ItemNotes.Add(note);
        }
        HasItemNotes = ItemNotes.Count > 0;
    }

    [ObservableProperty]
    public partial bool HasItems { get; private set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>"p. 42–44 · 1 of 3", "Whole book · 2 of 3".</summary>
    public string Counter
    {
        get
        {
            if (Current is not { } current) return "";
            var items = Items;
            var index = items.ToList().FindIndex(i => i.Id == current.Id);
            var pages = current.Item.Range is null ? "Whole book" : SessionItemRow.Pages(current.Item.Range);
            return $"{pages} · {(index + 1).ToString("N0", CultureInfo.CurrentCulture)} of {items.Count.ToString("N0", CultureInfo.CurrentCulture)}";
        }
    }

    /// <summary>The reader has gone to a page outside the item's range: the counter offers the way back.</summary>
    public bool IsOutsideItem => Current is { Item.Range: not null } current && Viewer.IsPdf
        && (Viewer.CurrentPageIndex < current.Target.FirstPage || Viewer.CurrentPageIndex > current.Target.LastPage);

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    partial void OnNotesChanged(string value)
    {
        if (_loadingNotes) return;
        _notesDirty = true;
        _notesTimer.Start();
    }

    /// <summary>The notes panel is open; it folds away to give the reader the room.</summary>
    [ObservableProperty]
    public partial bool ShowNotes { get; set; } = true;

    /// <summary>Raised when a page's name is ready to be typed, so the view puts the keyboard in its box.</summary>
    public event EventHandler<SessionItemRow>? RenameStarted;

    /// <summary>Where a new page opens: at this item, at the reader's page and zoom there, as when a binder moves between windows.</summary>
    public void StartAt(long? itemId, ViewerRequest? at)
    {
        _currentId = itemId;
        _startAt = at;
    }

    /// <summary>The item in hand and the reader's place in it, for the same binder in another window.</summary>
    public (long? ItemId, ViewerRequest? At) Here() => (Current?.Id, Current is null ? null : Viewer.Here());

    public override async Task LoadAsync()
    {
        _sessions.Changed -= OnSessionsChanged;
        _sessions.Changed += OnSessionsChanged;
        _directory.Changed -= OnDirectoryChanged;
        _directory.Changed += OnDirectoryChanged;
        _pins.Changed -= OnPinsChanged;
        _pins.Changed += OnPinsChanged;
        OnPropertyChanged(nameof(PinLabel));
        HasAtHand = _directory.HasAtHandBesides(PackId);
        var version = ++_version;
        try
        {
            // Opening the binder makes it the current one, which Add page adds to. That change is this page's own, so it
            // doesn't rebuild the list under the load (which would leave the reader empty on coming Back).
            SessionPackContents? contents;
            Interlocked.Increment(ref _ownWrites);
            try
            {
                contents = await Task.Run(() => _sessions.OpenAsync(PackId));
            }
            finally
            {
                Interlocked.Decrement(ref _ownWrites);
            }
            var rows = contents is null ? null : await SessionPackRows.BuildAsync(_sessions, _queries, _covers, contents);
            if (version != _version) return;
            if (!Show(contents, rows)) return;
            // Coming Back to the binder reopens the item in hand where the reader left it.
            var items = Items;
            if (_currentId is { } id && items.FirstOrDefault(i => i.Id == id) is { } again)
            {
                Current = again;
                if (_startAt is { } at)
                {
                    _startAt = null;
                    await Viewer.ShowAsync(at);
                }
                else if (again.CanOpen && Viewer.Here() is not null) await Viewer.LoadAsync();
                else if (again.OpensOnline && Online is { } online) await ShowOnlineAsync(online.Here());
                else await OpenAsync(again);
                ReadAheadAsync().FireAndForget();
            }
            else if (items.Count > 0) await OpenAsync(items[0]);
            else
            {
                ShowOnline(null);
                Viewer.ShowUnavailable("This binder has nothing in it yet.", "Add books and pages to it from your library, or use Add page in the reader.");
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Opening binder {PackId} failed", PackId);
        }
    }

    /// <summary>Shows the binder's title, notes and rows; false when it's gone.</summary>
    bool Show(SessionPackContents? contents, IReadOnlyList<object>? rows)
    {
        IsLoaded = true;
        if (contents is null)
        {
            Pack = null;
            Rows.Clear();
            HasItems = false;
            Current = null;
            ShowOnline(null);
            Viewer.ShowUnavailable("This binder isn't there any more.", "It was deleted. Go back to Binders to pick another.");
            return false;
        }
        Pack = contents.Pack;
        if (!_notesDirty)
        {
            _loadingNotes = true;
            Notes = contents.Pack.Notes ?? "";
            _loadingNotes = false;
        }
        Rows.Clear();
        foreach (var row in rows ?? [])
        {
            if (row is SessionItemRow item) item.Edited += OnItemEdited;
            Rows.Add(row);
        }
        HasItems = Items.Count > 0;
        return true;
    }

    public override void Unload()
    {
        _version++;
        _directory.Changed -= OnDirectoryChanged;
        _pins.Changed -= OnPinsChanged;
        // Changes made while the page is away are picked up by the next load, not by a refresh racing it.
        IsLoaded = false;
        _sessions.Changed -= OnSessionsChanged;
        Viewer.Unload();
        Online?.Unload();
        if (_notesDirty) SaveNotesAsync().FireAndForget();
    }

    public override async Task CloseWindowAsync()
    {
        _version++;
        _directory.Changed -= OnDirectoryChanged;
        _pins.Changed -= OnPinsChanged;
        _sessions.Changed -= OnSessionsChanged;
        if (_notesDirty) await SaveNotesAsync();
        Online?.Unload();
        await Viewer.CloseWindowAsync();
    }

    /// <summary>
    /// The binder changed somewhere else, such as a page added from another reader or its list page in the main window.
    /// The list is rebuilt and the page in hand stays as it is. This page's own writes are left out: each one already
    /// shows its result.
    /// </summary>
    void OnSessionsChanged(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _ownWrites) > 0) return;
        _dispatcher.InvokeAsync(async () =>
        {
            if (IsLoaded) await RefreshRowsAsync();
        });
    }

    /// <summary>
    /// Rebuilds the list, keeping the item in hand, or opening <paramref name="open"/> instead; with
    /// <paramref name="reopen"/>, the item in hand opens again, as after Try again on one whose folder was offline.
    /// </summary>
    async Task RefreshRowsAsync(long? open = null, bool reopen = false)
    {
        var version = ++_version;
        try
        {
            var contents = await Task.Run(() => _sessions.GetAsync(PackId));
            var rows = contents is null ? null : await SessionPackRows.BuildAsync(_sessions, _queries, _covers, contents);
            if (version != _version) return;
            var editing = Items.FirstOrDefault(i => i.IsRenaming);
            if (!Show(contents, rows)) return;
            var items = Items;
            if (open is { } id && items.FirstOrDefault(i => i.Id == id) is { } added) await OpenAsync(added);
            else if (Current is { } current && items.FirstOrDefault(i => i.Id == current.Id) is { } same)
            {
                Current = same;
                var moved = Moved(current.Target, same.Target);
                if (reopen || (moved && !same.CanOpen)) await OpenAsync(same);
                else
                {
                    // Its pages moved, as with Use this page on a Needs review card: the reader follows without reopening the binder.
                    if (moved && await SessionPackRows.RequestAsync(same, Pack?.Title ?? "", _queries) is { } request && Current == same)
                        await Viewer.FollowAsync(request);
                    // The page after it may have changed.
                    ReadAheadAsync().FireAndForget();
                }
            }
            else if (items.Count > 0) await OpenAsync(items[0]);
            else
            {
                Current = null;
                Viewer.ShowUnavailable("This binder has nothing in it yet.", "Add books and pages to it from your library, or use Add page in the reader.");
            }
            // A name being typed when the list was rebuilt stays open for typing.
            if (editing is not null && items.FirstOrDefault(i => i.Id == editing.Id) is { } again) Rename(again, editing.Draft);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Showing binder {PackId} failed", PackId);
        }
    }

    /// <summary>Whether an item opens somewhere else than it did: another file or pages, or another way of finding them.</summary>
    static bool Moved(SessionItemTarget before, SessionItemTarget after) =>
        (before.DocumentId, before.FirstPage, before.LastPage, before.State) != (after.DocumentId, after.FirstPage, after.LastPage, after.State);

    /// <summary>A click or Enter on an item in the list.</summary>
    [RelayCommand]
    Task OpenItem(SessionItemRow row) => OpenAsync(row);

    /// <summary>Some other binder is kept at hand during games: At hand shows.</summary>
    [ObservableProperty]
    public partial bool HasAtHand { get; private set; }

    void OnDirectoryChanged(object? sender, EventArgs e) => HasAtHand = _directory.HasAtHandBesides(PackId);

    /// <summary>
    /// At hand's list: the other binders kept at hand with their pages, those for a game system of this binder's books
    /// first, then those for any system, then the rest, each by title.
    /// </summary>
    public async Task<IReadOnlyList<AtHandGroup>> AtHandAsync()
    {
        var packs = _directory.All.Where(p => p.KeepAtHand && p.Id != PackId).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (packs.Count == 0) return [];
        try
        {
            List<EntryId> entries = [.. Items.Select(i => i.Item.EntryId).Distinct()];
            var systems = entries.Count == 0 ? [] : (await Task.Run(() => _queries.GetFacetCountsAsync("system", new LibraryFilter(entries))))
                .Select(f => f.Value).Where(v => v != SearchQuery.Unknown).ToHashSet();
            var binders = new List<AtHandBinder>();
            foreach (var pack in packs)
            {
                if (await Task.Run(() => _sessions.GetAsync(pack.Id)) is not { } contents) continue;
                var rows = await SessionPackRows.BuildAsync(_sessions, _queries, _covers, contents);
                binders.Add(new AtHandBinder(contents.Pack, [.. rows.OfType<SessionItemRow>()]));
            }
            var matching = binders.Where(b => b.Pack.GameSystem is { } system && systems.Contains(system)).ToList();
            var any = binders.Where(b => b.Pack.GameSystem is null).ToList();
            AtHandGroup[] groups =
            [
                new("For this binder's game systems", matching),
                new("For any game system", any),
                new("Other game systems", [.. binders.Except(matching).Except(any)]),
            ];
            return [.. groups.Where(g => g.Binders.Count > 0)];
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Listing the binders kept at hand failed");
            return [];
        }
    }

    /// <summary>A page picked in At hand opens in a reader window of its own, keeping no reading position.</summary>
    [RelayCommand]
    async Task OpenAtHand(AtHandPage page)
    {
        try
        {
            if (SessionPackRows.OnlineRequest(page.Row) is { } online) await _online.OpenInWindowAsync(online);
            else if (await SessionPackRows.RequestAsync(page.Row, page.BinderTitle, _queries) is { } request) await _readers.OpenInNewWindowAsync(request);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Opening a page kept at hand failed");
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    Task NextItem() => StepAsync(1);

    bool CanGoNext() => Current is { } current && Items is var items && items.Count > 0 && items[^1].Id != current.Id;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    Task PreviousItem() => StepAsync(-1);

    bool CanGoPrevious() => Current is { } current && Items is [var first, ..] && first.Id != current.Id;

    Task StepAsync(int by)
    {
        var items = Items.ToList();
        var index = Current is { } current ? items.FindIndex(i => i.Id == current.Id) : -1;
        var next = index + by;
        return next >= 0 && next < items.Count ? OpenAsync(items[next]) : Task.CompletedTask;
    }

    /// <summary>The item at its first page again, after the reader went elsewhere in the book.</summary>
    [RelayCommand(CanExecute = nameof(HasCurrent))]
    Task BackToItem() => Current is { } current ? OpenAsync(current) : Task.CompletedTask;

    bool HasCurrent() => Current is not null;

    async Task OpenAsync(SessionItemRow row)
    {
        Current = row;
        _currentId = row.Id;
        if (row.OpensOnline)
        {
            if (SessionPackRows.OnlineRequest(row) is { } online && await _online.OpensInAppAsync() && OnlineBrowsers.RuntimeVersion is not null)
            {
                if (Current == row) await ShowOnlineAsync(online);
            }
            else
            {
                // Settings > Library sends pages owned online to the everyday browser, or Bibliotaph's browser can't run here.
                ShowOnline(null);
                Viewer.ShowUnavailable($"{row.Heading} is a page on {row.OnlinePlace ?? "the web"}.",
                    "Pages owned online open in your everyday browser: open it from the binder's page, or from At hand.");
            }
        }
        else if (!row.CanOpen)
        {
            ShowOnline(null);
            // Its folder offline or its file gone: the reader says what to do, and Try again opens it once it's back.
            if (row.Target.Away is { } away) Viewer.ShowAway(away, () => RefreshRowsAsync(reopen: true));
            else Viewer.ShowUnavailable($"{row.Heading} can't be opened right now.", row.Reason ?? "Its file can't be reached.");
        }
        else
        {
            ShowOnline(null);
            var request = await SessionPackRows.RequestAsync(row, Pack?.Title ?? "", _queries);
            if (Current != row) return;
            if (request is null) Viewer.ShowUnavailable($"{row.Heading} can't be opened right now.", "Its file can't be reached.");
            else await Viewer.ShowAsync(request);
        }
        if (Current == row) ReadAheadAsync().FireAndForget();
    }

    /// <summary>The online reader at <paramref name="request"/>'s page, in the reader's place.</summary>
    async Task ShowOnlineAsync(OnlineReaderRequest request)
    {
        var reader = _online.Create(request, inBinder: true);
        ShowOnline(reader);
        await reader.LoadAsync();
    }

    void ShowOnline(OnlineReaderViewModel? reader)
    {
        if (ReferenceEquals(reader, Online)) return;
        Online?.Unload();
        Online = reader;
    }

    /// <summary>
    /// Opens the next page's book ahead of time, with the page drawn, when it's another book than the one open, so Next
    /// shows it at once; the book open draws its own next pages. Otherwise any book opened ahead before is closed.
    /// </summary>
    async Task ReadAheadAsync()
    {
        var items = Items;
        var at = Current;
        var index = at is { } current ? items.ToList().FindIndex(i => i.Id == current.Id) : -1;
        var ahead = items.Select(i => new ReadAheadItem(i.Target.DocumentId, i.CanOpen, OpensAsImage(i))).ToList();
        if (ReadAhead.Next(ahead, index, Viewer.Here()?.DocumentId) is not { } next)
        {
            Viewer.DropStandby();
            return;
        }
        try
        {
            if (await SessionPackRows.RequestAsync(items[next], Pack?.Title ?? "", _queries) is { } request && at is not null && Current == at) await Viewer.PrepareAsync(request);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Reading ahead in binder {PackId} failed", PackId);
        }
    }

    /// <summary>
    /// An image, or a whole pack whose first file is one, opens in the image viewer, which is quick enough not to need
    /// opening ahead. A pack's card shows its first file, so its format is that file's.
    /// </summary>
    static bool OpensAsImage(SessionItemRow row) =>
        row.Item.Range is null && row.Cover is { } cover && SourceFormats.IsImage(cover.Entry.Format);

    [RelayCommand]
    void ToggleNotes() => ShowNotes = !ShowNotes;

    /// <summary>Rename: F2, a double-click or the pencil puts the item's name in a box to type over.</summary>
    [RelayCommand]
    void RenameItem(SessionItemRow row) => Rename(row, row.Label);

    void Rename(SessionItemRow row, string draft)
    {
        foreach (var other in Items.Where(i => i.IsRenaming && i != row)) other.IsRenaming = false;
        row.Draft = draft;
        row.IsRenaming = true;
        RenameStarted?.Invoke(this, row);
    }

    /// <summary>Enter, or leaving the box: the name is saved; a blank one shows the book's title again.</summary>
    public static void CommitRename(SessionItemRow row)
    {
        if (!row.IsRenaming) return;
        row.IsRenaming = false;
        row.Label = row.Draft.Trim();
    }

    /// <summary>Esc: the name stays as it was.</summary>
    public static void CancelRename(SessionItemRow row) => row.IsRenaming = false;

    async void OnItemEdited(object? sender, EventArgs e)
    {
        if (sender is not SessionItemRow row) return;
        await RunAsync(() => _sessions.UpdateItemAsync(row.Id, row.Label, row.Note), "Saving the item failed");
        OnPropertyChanged(nameof(Counter));
    }

    /// <summary>
    /// Add this page: the page in view, or the whole image, right after the item in hand, called after the bookmark it
    /// sits under, with its name ready to type over.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddPage))]
    async Task AddPage()
    {
        if (Viewer.Here() is not { } here) return;
        var (pack, document, page, image) = (PackId, here.DocumentId, Viewer.CurrentPageIndex, Viewer.Mode == ViewerMode.Image);
        var after = Current?.Item;
        long? added = null;
        await RunAsync(async () =>
        {
            var section = after is null ? SessionStore.NoSection : after.SectionId ?? SessionStore.NoSection;
            added = image ? await _sessions.AddDocumentAsync(pack, document, section) : await _sessions.AddPagesAsync(pack, document, page, page, sectionId: section);
            if (added is { } id && after is not null) await _sessions.MoveItemToAsync(id, section, after.Position + 1);
        }, "Adding the page failed");
        if (added is not { } item) return;
        await RefreshRowsAsync(open: item);
        if (Items.FirstOrDefault(i => i.Id == item) is { } row) Rename(row, row.Label);
    }

    bool CanAddPage() => Pack is not null && Viewer.Mode is ViewerMode.Pdf or ViewerMode.Image;

    /// <summary>Edit binder: the list page, for sections, order, notes and the rest, in the main window.</summary>
    [RelayCommand]
    void Edit() => _sessionPages.Edit(PackId);

    /// <summary>Pop out: the whole binder moves to a window of its own, at the same item and page.</summary>
    [RelayCommand]
    Task PopOut() => _sessionPages.PopOutAsync(this);

    /// <summary>Return to main window: a popped-out binder comes back, at the same item and page, and its window closes.</summary>
    [RelayCommand]
    Task ReturnToMainWindow() => _sessionPages.ReturnToMainWindowAsync(this);

    void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewerViewModel.CurrentPageIndex) or nameof(ViewerViewModel.Mode)) OnPropertyChanged(nameof(IsOutsideItem));
        if (e.PropertyName is nameof(ViewerViewModel.Mode)) AddPageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Saves notes still waiting for typing to pause, as before the binder moves to another window.</summary>
    public async Task SaveNotesAsync()
    {
        _notesTimer.Stop();
        if (!_notesDirty) return;
        _notesDirty = false;
        var (pack, notes) = (PackId, Notes);
        await RunAsync(() => _sessions.SetNotesAsync(pack, notes), "Saving the binder's notes failed");
    }

    async Task RunAsync(Func<Task> action, string failure)
    {
        Interlocked.Increment(ref _ownWrites);
        try
        {
            await Task.Run(action);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Failure}", failure);
        }
        finally
        {
            Interlocked.Decrement(ref _ownWrites);
        }
    }
}
