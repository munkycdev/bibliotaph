using Bibliotaph.App.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Settings > Devices: connects this PC to the user's library at bibliotaph.online, which keeps the user's work the
/// same on every PC connected to it and on the web. Connect opens the approval
/// page in the browser and shows the code the page shows, then waits for the user's answer there; leaving the section
/// stops waiting. Once connected, it says whose library and how library sync with it is going, and Disconnect
/// forgets the PC's key here and on the web. It also says how much of the web library is downloaded here, and sets
/// the room for the downloads not kept offline.
/// </summary>
public sealed partial class DevicesSectionViewModel(WebLibrary web, LibrarySync sync, WebShelfSync shelf, WebLibraryCopies copies, IDesktopShell shell,
    TimeProvider clock, ILogger<DevicesSectionViewModel> log) : SettingsSectionViewModel
{
    System.Windows.Threading.Dispatcher? _dispatcher;
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

    /// <summary>How many of the web library's books are downloaded here, their size, and how many are kept offline.</summary>
    [ObservableProperty]
    public partial string? CopiesLine { get; private set; }

    bool _showingRoom;

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    partial void OnIsWaitingChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    public override async Task LoadAsync()
    {
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        sync.StatusChanged -= OnSyncStatusChanged;
        sync.StatusChanged += OnSyncStatusChanged;
        if (IsWaiting) return;
        await ShowAsync();
    }

    public override void Unload()
    {
        sync.StatusChanged -= OnSyncStatusChanged;
        _stopWaiting?.Invoke();
    }

    void OnSyncStatusChanged(object? sender, EventArgs e) => _dispatcher?.InvokeAsync(ShowSync);

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
        _showingRoom = true;
        RoomGb = await copies.GetRoomGbAsync();
        _showingRoom = false;
        CopiesLine = summary.Books == 0 ? "None of your web library's books are downloaded to this PC."
            : $"{summary.Books} {LibraryActivity.Plural(summary.Books, "book", "books")} from your web library downloaded to this PC, {LibraryActivity.Size(summary.Bytes)}"
                + (summary.KeptOffline == 0 ? "." : $", {summary.KeptOffline} kept offline.");
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
