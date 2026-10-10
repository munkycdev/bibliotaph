using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Run mode (slice 3 plan, choice 16): a session pack at the table, filling the main window. Its items list down the
/// left, numbered under their sections; the reader shows the item in hand at its first page, with every other page
/// still reachable; the pack's notes sit in a panel on the right that folds away. Next and Previous item (Ctrl+Right
/// and Ctrl+Left) step through them, F11 is full screen and Esc leaves. A personal preparation surface, not a display
/// for players (spec §4.5).
/// </summary>
public sealed partial class RunSessionViewModel : PageViewModel
{
    readonly SessionsService _sessions;
    readonly SessionPages _sessionPages;
    readonly LibraryQueries _queries;
    readonly CoverImages _covers;
    readonly INavigationService _navigation;
    readonly ILogger<RunSessionViewModel> _log;
    readonly DispatcherTimer _notesTimer;
    int _version;
    bool _notesDirty;
    bool _loadingNotes;
    long? _currentId;

    public RunSessionViewModel(SessionsService sessions, SessionPages sessionPages, LibraryQueries queries, CoverImages covers, ReaderWindows readers,
        INavigationService navigation, ILogger<RunSessionViewModel> log)
    {
        _sessions = sessions;
        _sessionPages = sessionPages;
        _queries = queries;
        _covers = covers;
        _navigation = navigation;
        _log = log;
        Viewer = readers.Create(null, inRunMode: true);
        Viewer.PropertyChanged += OnViewerChanged;
        _notesTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, async (_, _) => await SaveNotesAsync(), Dispatcher.CurrentDispatcher);
        _notesTimer.Stop();
    }

    public long PackId { get; set; }

    public override Route Route => Route.RunSession;

    public override Route NavRoute => Route.Sessions;

    public override string Section => "Sessions";

    public override Route? SectionRoute => Route.Sessions;

    public override string Title => Pack?.Title ?? "Session";

    public override bool ScrollsItself => true;

    public override bool IsImmersive => true;

    /// <summary>The reader for the item in hand.</summary>
    public ViewerViewModel Viewer { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Eyebrow))]
    public partial SessionPackInfo? Pack { get; private set; }

    /// <summary>"RUNNING · SATURDAY, 17 OCTOBER 2026".</summary>
    public string Eyebrow => SessionDirectory.DateLabel(Pack?.Date) is { } date ? $"RUNNING · {date.ToUpperInvariant()}" : "RUNNING";

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
        _notesTimer.Stop();
        _notesTimer.Start();
    }

    /// <summary>The notes panel is open; it folds away to give the reader the room.</summary>
    [ObservableProperty]
    public partial bool ShowNotes { get; set; } = true;

    /// <summary>F11: the main window fills the screen, without its frame. The view does it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullScreenLabel))]
    public partial bool IsFullScreen { get; set; }

    public string FullScreenLabel => IsFullScreen ? "Leave full screen" : "Full screen";

    public override async Task LoadAsync()
    {
        var version = ++_version;
        try
        {
            var contents = await Task.Run(() => _sessions.OpenAsync(PackId));
            if (version != _version) return;
            if (contents is null)
            {
                Pack = null;
                Rows.Clear();
                HasItems = false;
                IsLoaded = true;
                Viewer.ShowUnavailable("This session isn't there any more.", "It was deleted. Go back to Sessions to pick another.");
                return;
            }
            var rows = await SessionPackRows.BuildAsync(_sessions, _queries, _covers, contents);
            if (version != _version) return;
            Pack = contents.Pack;
            if (!_notesDirty)
            {
                _loadingNotes = true;
                Notes = contents.Pack.Notes ?? "";
                _loadingNotes = false;
            }
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
            HasItems = Items.Count > 0;
            IsLoaded = true;
            // Coming Back to run mode reopens the item in hand where the reader left it.
            var items = Items;
            if (_currentId is { } id && items.FirstOrDefault(i => i.Id == id) is { } again)
            {
                Current = again;
                if (again.CanOpen && Viewer.Here() is not null) await Viewer.LoadAsync();
                else await OpenAsync(again);
            }
            else if (items.Count > 0) await OpenAsync(items[0]);
            else Viewer.ShowUnavailable("This session has nothing in it yet.", "Add books and pages to it from your library, then begin again.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Beginning session pack {PackId} failed", PackId);
        }
    }

    public override void Unload()
    {
        _version++;
        IsFullScreen = false;
        Viewer.Unload();
        if (_notesDirty) _ = SaveNotesAsync();
    }

    /// <summary>A click or Enter on an item in the list.</summary>
    [RelayCommand]
    Task OpenItem(SessionItemRow row) => OpenAsync(row);

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
        if (!row.CanOpen)
        {
            Viewer.ShowUnavailable($"{row.Heading} can't be opened right now.", row.Reason ?? "Its file can't be reached.");
            return;
        }
        var request = await SessionPackRows.RequestAsync(row, Pack?.Title ?? "", _queries);
        if (Current != row) return;
        if (request is null) Viewer.ShowUnavailable($"{row.Heading} can't be opened right now.", "Its file can't be reached.");
        else await Viewer.ShowAsync(request);
    }

    [RelayCommand]
    void ToggleNotes() => ShowNotes = !ShowNotes;

    [RelayCommand]
    void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    /// <summary>Esc: out of full screen first, then out of run mode.</summary>
    [RelayCommand]
    void Escape()
    {
        if (IsFullScreen) IsFullScreen = false;
        else End();
    }

    /// <summary>End session: back to the pack's page.</summary>
    [RelayCommand]
    void End()
    {
        if (!_navigation.GoBack()) _sessionPages.Open(PackId);
    }

    void OnViewerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewerViewModel.CurrentPageIndex) or nameof(ViewerViewModel.Mode)) OnPropertyChanged(nameof(IsOutsideItem));
    }

    async Task SaveNotesAsync()
    {
        _notesTimer.Stop();
        if (!_notesDirty) return;
        _notesDirty = false;
        var (pack, notes) = (PackId, Notes);
        try
        {
            await Task.Run(() => _sessions.SetNotesAsync(pack, notes));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving the session's notes failed");
        }
    }
}
