using Bibliotaph.App.Services;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Settings > Web library: connects this PC to the user's library at bibliotaph.online. Connect opens the approval
/// page in the browser and shows the code the page shows, then waits for the user's answer there; leaving the section
/// stops waiting. Once connected, it says whose library, and Disconnect forgets the PC's key here and on the web.
/// </summary>
public sealed partial class WebLibrarySectionViewModel(WebLibrary web, IDesktopShell shell, ILogger<WebLibrarySectionViewModel> log)
    : SettingsSectionViewModel
{
    // Stops the wait; the wait itself owns its token source.
    Action? _stopWaiting;
    string? _approveUrl;

    public override SettingsSection Section => SettingsSection.WebLibrary;
    public override string Label => "Web library";

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

    /// <summary>How the last Connect or Disconnect went, when it needs saying.</summary>
    [ObservableProperty]
    public partial string? Note { get; private set; }

    public bool CanConnect => !IsConnected && !IsWaiting;

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    partial void OnIsWaitingChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    public override async Task LoadAsync()
    {
        if (IsWaiting) return;
        await ShowAsync();
    }

    public override void Unload()
    {
        _stopWaiting?.Invoke();
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
        Note = "This PC is disconnected. Your books and work on it are untouched.";
        await ShowAsync();
    }
}
