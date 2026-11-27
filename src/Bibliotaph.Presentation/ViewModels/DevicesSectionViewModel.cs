using Bibliotaph.Presentation.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Presentation.ViewModels;

/// <summary>
/// Settings > Devices: connects this PC to the user's library at bibliotaph.online, which keeps the user's work the
/// same on every PC connected to it and on the web. Connect opens the approval
/// page in the browser and shows the code the page shows, then waits for the user's answer there; leaving the section
/// stops waiting. Once connected, it says whose library and how library sync with it is going, and Disconnect
/// forgets the PC's key here and on the web. It also says how much of the web library is downloaded here, and sets
/// the room for the downloads not kept offline, and whether favorites' and binders' books are kept here (<see cref="OfflineKeeper"/>).
/// </summary>
public sealed partial class DevicesSectionViewModel(WebLibrary web, LibrarySync sync, WebShelfSync shelf, WebLibraryCopies copies, IDesktopShell shell,
    IUiThread ui, TimeProvider clock, Bibliotaph.Storage.Desktop.PcConnections storage, ILogger<DevicesSectionViewModel> log, OfflineKeeper keeper)
    : SettingsSectionViewModel
{
    static string Device => OperatingSystem.IsMacOS() ? "Mac" : "PC";

    // Stops the wait; the wait itself owns its token source.
    Action? _stopWaiting;
    string? _approveUrl;

    public override SettingsSection Section => SettingsSection.Devices;
    public override string Label => "Devices";

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    /// <summary>Asked to connect, and waiting for the user to approve this PC in the browser.</summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; private set; }

    /// <summary>The code the approval page shows, so the user can tell it's this PC they're approving.</summary>
    [ObservableProperty]
    public partial string? Code { get; private set; }

    /// <summary>Whose library this PC is connected to, or why it isn't.</summary>
    [ObservableProperty]
    public partial string? StatusLine { get; private set; }

    /// <summary>How sync with the web library is going, while connected.</summary>
    [ObservableProperty]
    public partial string? SyncLine { get; private set; }

    /// <summary>How the last Connect or Disconnect went, when it needs saying.</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    public bool CanConnect => !IsConnected && !IsWaiting;

    /// <summary>The rooms offered for the downloads not kept offline, in GB.</summary>
    public IReadOnlyList<int> RoomChoices { get; } = WebLibraryCopies.Rooms;

    /// <summary>The room for the web library's books downloaded to open them, in GB; past it the least recently read go.</summary>
    [ObservableProperty]
    public partial int RoomGb { get; set; } = WebLibraryCopies.DefaultRoomGb;

    /// <summary>How many books from the web library and connected storage are downloaded here, their size, and how many are kept offline.</summary>
    [ObservableProperty]
    public partial string? CopiesLine { get; private set; }

    /// <summary>Whether there are downloads to speak of: connected to the web library, storage connected here, or copies left from either.</summary>
    [ObservableProperty]
    public partial bool ShowsCopies { get; private set; }

    bool _showingRoom;

    /// <summary>"Keep favorites and binders offline": their books only storage or the web library has are downloaded and kept here.</summary>
    [ObservableProperty]
    public partial bool KeepsPrepared { get; set; } = true;

    /// <summary>How keeping favorites and binders offline is going.</summary>
    [ObservableProperty]
    public partial string? PreparedLine { get; private set; }

    bool _showingPrepared;

    partial void OnKeepsPreparedChanged(bool value)
    {
        if (!_showingPrepared) Task.Run(() => keeper.SetOnAsync(value)).FireAndForget();
    }

    void OnKeeperChanged(object? sender, EventArgs e) => ui.Post(() => ShowPrepared(keeper.Status));

    void ShowPrepared(OfflineKeepStatus status)
    {
        _showingPrepared = true;
        KeepsPrepared = status.On;
        _showingPrepared = false;
        PreparedLine = Describe(status, Device);
        if (status.Books > 0) ShowsCopies = true;
        DownloadNowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What Settings says of keeping favorites and binders offline on this <paramref name="device"/> ("PC", "Mac").</summary>
    public static string Describe(OfflineKeepStatus status, string device)
    {
        if (!status.On) return "Off. Favorites and binders are downloaded when you open them, like any other book.";
        if (status.Working && status.Books == 0) return "Looking for favorites and binders to keep…";
        if (status.Books == 0) return $"Nothing to download: every favorite and binder book is on this {device} already.";
        var line = status.Working && status.Ready < status.Books
            ? $"Downloading favorites and binders: {status.Ready} of {status.Books} {LibraryActivity.Plural(status.Books, "book", "books")} on this {device}"
            : $"{status.Ready} {LibraryActivity.Plural(status.Ready, "book", "books")} from favorites and binders kept on this {device}";
        line += status.Bytes > 0 ? $", {LibraryActivity.Size(status.Bytes)}." : ".";
        if (!status.Working && status.NoRoom > 0) line += $" {status.NoRoom} {LibraryActivity.Plural(status.NoRoom, "waits", "wait")} for free disk space.";
        if (!status.Working && status.Failed > 0) line += $" {status.Failed} couldn't be downloaded; Download now tries again.";
        return line;
    }

    bool CanDownloadNow => keeper.Status is { On: true, Working: false };

    /// <summary>Download now: looks again at favorites and binders, and tries again the books that couldn't be downloaded.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadNow))]
    void DownloadNow() => keeper.Refresh();

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    partial void OnIsWaitingChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    public override async Task LoadAsync()
    {
        sync.StatusChanged -= OnSyncStatusChanged;
        sync.StatusChanged += OnSyncStatusChanged;
        keeper.StatusChanged -= OnKeeperChanged;
        keeper.StatusChanged += OnKeeperChanged;
        if (IsWaiting) return;
        await ShowAsync();
    }

    public override void Unload()
    {
        sync.StatusChanged -= OnSyncStatusChanged;
        keeper.StatusChanged -= OnKeeperChanged;
        _stopWaiting?.Invoke();
    }

    void OnSyncStatusChanged(object? sender, EventArgs e) => ui.Post(ShowSync);

    void ShowSync() => SyncLine = IsConnected ? LibrarySync.DescribeWeb(sync.Status, clock.GetUtcNow(), clock.LocalTimeZone) : null;

    partial void OnRoomGbChanged(int value)
    {
        if (!_showingRoom) SetRoomAsync(value).FireAndForget();
    }

    async Task SetRoomAsync(int gb)
    {
        await Task.Run(() => copies.SetRoomGbAsync(gb));
        await ShowCopiesAsync();
    }

    async Task ShowCopiesAsync()
    {
        var summary = await Task.Run(() => copies.SummarizeAsync());
        var prepared = await Task.Run(() => keeper.StatusAsync());
        ShowsCopies = IsConnected || summary.Books > 0 || prepared.Books > 0 || (await Task.Run(() => storage.ListAsync())).Count > 0;
        _showingRoom = true;
        RoomGb = await copies.GetRoomGbAsync();
        _showingRoom = false;
        CopiesLine = summary.Books == 0 ? "No books are downloaded to this PC to read."
            : $"{summary.Books} {LibraryActivity.Plural(summary.Books, "book", "books")} downloaded to this PC to read, {LibraryActivity.Size(summary.Bytes)}"
                + (summary.KeptOffline == 0 ? "." : $", {summary.KeptOffline} kept offline.");
        ShowPrepared(prepared);
    }

    /// <summary>Deletes the downloads not kept offline and not open; they're downloaded again when they're opened.</summary>
    [RelayCommand]
    async Task FreeUpSpaceAsync()
    {
        await Task.Run(() => copies.ClearAsync(keepKept: true));
        await ShowCopiesAsync();
    }

    async Task ShowAsync()
    {
        var status = await web.CheckAsync();
        var address = (await web.AddressAsync()).Host;
        IsConnected = status.State is WebLibraryState.Connected or WebLibraryState.Unreachable;
        StatusLine = status.State switch
        {
            WebLibraryState.Connected => $"Connected to {status.UserName}'s library at {address} as {status.DeviceName}.",
            WebLibraryState.Unreachable => $"Connected, but {address} can't be reached just now.",
            WebLibraryState.Disconnected => "This PC was disconnected on the web. Connect again to use your web library.",
            _ => "Not connected.",
        };
        ShowSync();
        await ShowCopiesAsync();
    }

    [RelayCommand]
    async Task ConnectAsync()
    {
        Note = null;
        var started = await web.StartAsync(Environment.MachineName);
        if (started is null)
        {
            Note = $"Bibliotaph couldn't reach {(await web.AddressAsync()).Host} just now. Check you're online, then try again.";
            return;
        }
        _approveUrl = started.ApproveUrl;
        Code = started.Code;
        IsWaiting = true;
        OpenPage();
        using var waiting = new CancellationTokenSource();
        _stopWaiting = waiting.Cancel;
        try
        {
            Note = await web.WaitAsync(started, waiting.Token) switch
            {
                WebConnectOutcome.Connected => null,
                WebConnectOutcome.Declined => "You chose not to connect this PC.",
                WebConnectOutcome.Expired => "The request expired before it was approved. Choose Connect to ask again.",
                _ => "Connecting didn't finish. Try again in a moment.",
            };
        }
        catch (OperationCanceledException)
        {
            // Settings was left, or Cancel was chosen; asking again starts afresh.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Connecting to the web library failed");
            Note = "Connecting didn't finish. Try again in a moment.";
        }
        finally
        {
            _stopWaiting = null;
            IsWaiting = false;
            Code = null;
        }
        await ShowAsync();
        // Merged now rather than at the next minute's look, so the web library's work shows here straight away.
        if (IsConnected)
        {
            _ = Task.Run(() => sync.SyncNowAsync());
            shelf.Nudge();
        }
    }

    /// <summary>Opens the approval page again, as when its tab was closed.</summary>
    [RelayCommand]
    void OpenPage()
    {
        if (_approveUrl is { } url && shell.Open(url) is { } problem)
        {
            log.LogWarning(problem, "Opening the approval page failed");
            Note = $"Bibliotaph couldn't open your browser. Go to {url} to approve this PC.";
        }
    }

    [RelayCommand]
    void Cancel() => _stopWaiting?.Invoke();

    [RelayCommand]
    async Task DisconnectAsync()
    {
        await web.DisconnectAsync();
        shelf.Nudge();
        Note = "This PC is disconnected. The web library's folders go from your library here; your own folders and your work are untouched.";
        await ShowAsync();
    }
}
