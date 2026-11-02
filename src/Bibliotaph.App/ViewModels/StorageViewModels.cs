using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Bibliotaph.App.Services;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Desktop;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>A storage account connected on this PC, as Settings > Library lists it, with what it was allowed.</summary>
public sealed class StorageAccountItem(PcConnection connection, StorageKind? kind)
{
    public PcConnection Connection { get; } = connection;

    public string Name => Connection.DisplayName.Length > 0 ? Connection.DisplayName : kind?.Name ?? Connection.Provider;

    public bool NeedsReconnect => Connection.NeedsReconnect;

    public string Detail => NeedsReconnect
        ? $"Needs connecting again: {kind?.Owner ?? "your storage"} no longer lets Bibliotaph read it. Its books stay in your library."
        : $"Connected {Connection.Connected.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}";

    /// <summary>"Allowed to read your files and keep access while you're signed out."</summary>
    public string Allowed => kind?.Access(Connection.Scopes.Length > 0 ? Connection.Scopes : null) is { Count: > 0 } access
        ? $"Allowed to {Sentence(access)}."
        : "";

    /// <summary>The provider's own names for what was granted, for the tooltip.</summary>
    public string? Scopes => Connection.Scopes.Length > 0 ? $"As {kind?.Owner ?? Connection.Provider} names it: {Connection.Scopes}" : null;

    static string Sentence(IReadOnlyList<string> parts) =>
        parts.Count == 1 ? parts[0] : $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}";
}

/// <summary>A kind of storage this PC can connect, as its Connect button.</summary>
public sealed record StorageConnectOption(StorageKind Kind, bool IsPrimary)
{
    public string Label => $"Connect {Kind.Name}";

    public bool SignsInWithPassword => Kind is not OAuthStorageKind;
}

/// <summary>
/// Settings > Library's Storage, above the folders, as the web has it: the storage connected on this PC, what each
/// was allowed, connecting another (in the browser, or a WebDAV folder's address and password typed here), connecting
/// one again when its provider stopped letting Bibliotaph in, and disconnecting.
/// </summary>
public sealed partial class StorageAccountsViewModel(PcConnections connections, StorageConnecting connecting, DesktopStorageApps apps,
    ILogger<StorageAccountsViewModel> log) : ObservableObject
{
    public ObservableCollection<StorageAccountItem> Accounts { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<StorageConnectOption> Options { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccounts))]
    public partial int Count { get; set; }

    public bool HasAccounts => Count > 0;

    /// <summary>What connecting is waiting on, or why it didn't finish; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string Status { get; set; } = "";

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(ConnectWebDavCommand), nameof(CancelConnectingCommand))]
    public partial bool IsConnecting { get; set; }

    [ObservableProperty]
    public partial bool IsWebDavOpen { get; set; }

    [ObservableProperty]
    public partial string WebDavAddress { get; set; } = "";

    [ObservableProperty]
    public partial string WebDavUser { get; set; } = "";

    /// <summary>Set from the password box, which can't be bound.</summary>
    public string WebDavPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWebDavProblem))]
    public partial string WebDavProblem { get; set; } = "";

    public bool HasWebDavProblem => WebDavProblem.Length > 0;

    /// <summary>Storage was connected or disconnected, which the folders below offer to add from.</summary>
    public event EventHandler? Changed;

    // The sign-in waiting on the browser, as its Cancel, so this view model owns nothing that needs disposing.
    Action? _cancelSignIn;

    public async Task LoadAsync()
    {
        var all = await Task.Run(() => connections.ListAsync());
        Accounts.Clear();
        foreach (var connection in all) Accounts.Add(new StorageAccountItem(connection, StorageKinds.Find(connection.Provider)));
        Count = Accounts.Count;
        var kinds = StorageKinds.All.Where(apps.CanConnect).ToList();
        Options = [.. kinds.Select((k, i) => new StorageConnectOption(k, Count == 0 && i == 0))];
    }

    bool CanConnect() => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    async Task Connect(StorageConnectOption option)
    {
        if (option.Kind is not OAuthStorageKind oauth)
        {
            IsWebDavOpen = true;
            WebDavProblem = "";
            return;
        }
        await SignInAsync(oauth);
    }

    /// <summary>Connect again, for an account its provider stopped letting Bibliotaph read: the same sign-in, or the WebDAV form.</summary>
    [RelayCommand]
    async Task ConnectAgain(StorageAccountItem account)
    {
        if (StorageKinds.Find(account.Connection.Provider) is OAuthStorageKind oauth) await SignInAsync(oauth);
        else
        {
            IsWebDavOpen = true;
            WebDavProblem = "";
        }
    }

    async Task SignInAsync(OAuthStorageKind kind)
    {
        IsConnecting = true;
        Status = $"Finish connecting {kind.Name} in your browser, and allow Bibliotaph to read your files when {kind.Owner} asks.";
        using var signIn = new CancellationTokenSource();
        _cancelSignIn = signIn.Cancel;
        try
        {
            var outcome = await Task.Run(() => connecting.ConnectAsync(kind, signIn.Token));
            Status = outcome.Problem is { } problem ? $"Connecting {kind.Name} didn't finish. {problem}" : "";
            if (outcome.Connection is not null) await ChangedAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "";
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Connecting {Provider} failed", kind.Name);
            Status = $"Connecting {kind.Name} didn't finish. Try again.";
        }
        finally
        {
            _cancelSignIn = null;
            IsConnecting = false;
        }
    }

    bool CanCancelConnecting() => IsConnecting;

    [RelayCommand(CanExecute = nameof(CanCancelConnecting))]
    void CancelConnecting() => _cancelSignIn?.Invoke();

    /// <summary>Leaving Settings stops waiting on the browser, so the sign-in's port isn't held for minutes.</summary>
    public void Leave() => _cancelSignIn?.Invoke();

    [RelayCommand(CanExecute = nameof(CanConnect))]
    async Task ConnectWebDav()
    {
        if (WebDavAddress.Trim().Length == 0 || WebDavUser.Trim().Length == 0 || WebDavPassword.Length == 0)
        {
            WebDavProblem = "Type the folder's address, and the user name and password you sign in to that server with.";
            return;
        }
        IsConnecting = true;
        WebDavProblem = "";
        try
        {
            var outcome = await Task.Run(() => connecting.ConnectWebDavAsync(WebDavAddress, WebDavUser, WebDavPassword, CancellationToken.None));
            if (outcome.Problem is { } problem)
            {
                WebDavProblem = problem;
                return;
            }
            CloseWebDav();
            await ChangedAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Connecting WebDAV failed");
            WebDavProblem = "That didn't work. Try again.";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    [RelayCommand]
    void CloseWebDav()
    {
        IsWebDavOpen = false;
        WebDavAddress = WebDavUser = WebDavPassword = WebDavProblem = "";
    }

    [RelayCommand]
    async Task Disconnect(StorageAccountItem account)
    {
        try
        {
            await Task.Run(() => connecting.DisconnectAsync(account.Connection, CancellationToken.None));
            await ChangedAsync();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Disconnecting {Provider} failed", account.Connection.Provider);
        }
    }

    async Task ChangedAsync()
    {
        await LoadAsync();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A folder inside the one the picker is in, or a step of the way back up.</summary>
public sealed record StorageFolderStep(string Name, string Path);

/// <summary>
/// Picks a folder in connected storage, as the web's picker does: choose the account, walk down from the top, then
/// use the folder you're in. Only folders are listed; nothing is downloaded.
/// </summary>
public sealed partial class StorageFolderPickerViewModel(IReadOnlyList<StorageAccountItem> accounts, PcDrives drives, Action<string> picked, Action cancelled,
    ILogger log) : ObservableObject
{
    public IReadOnlyList<StorageAccountItem> Accounts { get; } = accounts;

    public bool HasManyAccounts => Accounts.Count > 1;

    [ObservableProperty]
    public partial StorageAccountItem? Account { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<StorageFolderStep> Crumbs { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<StorageFolderStep> Folders { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(UseCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string Problem { get; set; } = "";

    public bool HasProblem => Problem.Length > 0;

    public bool IsEmpty => !IsLoading && Folders.Count == 0 && !HasProblem;

    string _path = "";

    /// <summary>Opens the top of the first account that can be read; choosing another account opens its top.</summary>
    public void Start() => Account = Accounts.FirstOrDefault(a => !a.NeedsReconnect) ?? (Accounts.Count > 0 ? Accounts[0] : null);

    partial void OnAccountChanged(StorageAccountItem? value)
    {
        if (value is not null) _ = OpenAsync("");
    }

    [RelayCommand]
    Task Open(StorageFolderStep step) => OpenAsync(step.Path);

    async Task OpenAsync(string path)
    {
        if (Account is not { } account) return;
        IsLoading = true;
        Problem = "";
        try
        {
            var drive = drives.Find(account.Connection.Provider, account.Connection.AccountId)
                ?? throw new IOException($"{account.Name} isn't connected any more.");
            var children = await Task.Run(() => drive.ChildrenAsync(path));
            if (children is null)
            {
                Problem = "That folder isn't there any more.";
                return;
            }
            _path = path;
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            Crumbs = [new StorageFolderStep(account.Name, ""), .. parts.Select((name, i) => new StorageFolderStep(name, string.Join('/', parts.Take(i + 1))))];
            Folders = [.. children.Where(c => c.IsFolder).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new StorageFolderStep(c.Name, path.Length == 0 ? c.Name : $"{path}/{c.Name}"))];
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            log.LogWarning(ex, "Listing a storage folder failed");
            Problem = $"{account.Name} couldn't be read just now: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    bool CanUse() => !IsLoading && Account is not null;

    [RelayCommand(CanExecute = nameof(CanUse))]
    void Use() => picked(StoragePath.Of(Account!.Connection.Provider, Account.Connection.AccountId, _path));

    [RelayCommand]
    void Cancel() => cancelled();
}
