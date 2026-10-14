using System.Collections.ObjectModel;
using System.IO;
using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;

namespace Bibliotaph.App.Services;

/// <summary>
/// Downloads from stores: when the user presses a store's own Download button in Bibliotaph's browser or the online
/// reader, the file goes to the downloads folder they chose, a library folder or one inside it, and the library reads
/// it like any new file. One file per click, started by the user; the browser writes it, under a name no file there
/// has, so Bibliotaph's own code still writes nothing into library folders. With no downloads folder, the browser
/// saves to the user's Downloads folder as any browser does.
/// </summary>
public sealed class OnlineDownloads(SettingsStore settings, SourceRootStore roots, IndexingService indexing, IDesktopShell shell,
    ILogger<OnlineDownloads> log)
{
    string? _folder;
    bool _loaded;

    /// <summary>This session's downloads, newest first, which every browser window lists.</summary>
    public ObservableCollection<OnlineDownload> Items { get; } = [];

    /// <summary>The downloads folder, or null when there is none.</summary>
    public async Task<string?> FolderAsync()
    {
        if (_loaded) return _folder;
        try
        {
            _folder = await settings.GetAsync(SettingKeys.DownloadsFolder) is { Length: > 0 } folder ? folder : null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Reading the downloads folder failed");
        }
        _loaded = true;
        return _folder;
    }

    /// <summary>Makes <paramref name="folder"/> the downloads folder. Returns why it can't be, or null.</summary>
    public async Task<string?> SetFolderAsync(string folder)
    {
        if (DownloadPlaces.RootOf(folder, await roots.ListAsync(), r => r.Path) is null)
            return "Choose a library folder or a folder inside one, so Bibliotaph reads what you download. Add the folder in Settings > Library first if it isn't one.";
        await settings.SetAsync(SettingKeys.DownloadsFolder, folder);
        (_folder, _loaded) = (folder, true);
        return null;
    }

    /// <summary>No downloads folder: downloads go to the browser's own Downloads folder again.</summary>
    public async Task ClearFolderAsync()
    {
        await settings.SetAsync(SettingKeys.DownloadsFolder, "");
        (_folder, _loaded) = (null, true);
    }

    /// <summary>
    /// Takes a download a page started: into the downloads folder when there is one and it can be reached, else where
    /// the browser would put it. Must be called before the event handler returns, so it takes the deferral itself.
    /// </summary>
    public async Task<OnlineDownload> TakeAsync(CoreWebView2DownloadStartingEventArgs e, string serviceName)
    {
        var deferral = e.GetDeferral();
        OnlineDownload item;
        try
        {
            var folder = await FolderAsync();
            SourceRoot? root = null;
            if (folder is not null && Directory.Exists(folder))
                root = DownloadPlaces.RootOf(folder, await roots.ListAsync(), r => r.Path);
            if (folder is not null && root is not null)
            {
                e.ResultFilePath = DownloadPlaces.FreePath(folder, e.ResultFilePath, path => File.Exists(path) || File.Exists(path + ".crdownload"));
                // Bibliotaph lists the download in its own window instead of the browser's download bubble.
                e.Handled = true;
            }
            item = new OnlineDownload(e.DownloadOperation, e.ResultFilePath, serviceName, root?.Id, folder is not null && root is null, shell);
        }
        finally
        {
            deferral.Complete();
        }
        Items.Insert(0, item);
        item.Finished += (_, _) =>
        {
            // The folder watcher sees the file too; asking makes sure, with the watcher off or the drive slow.
            if (item.RootId is { } id && item.Saved) indexing.RequestScan(id);
        };
        return item;
    }
}

/// <summary>One download in Bibliotaph's browser: its name, how far it has got, and where it went.</summary>
public sealed partial class OnlineDownload : ObservableObject
{
    readonly CoreWebView2DownloadOperation _operation;
    readonly bool _folderAway;
    readonly IDesktopShell _shell;

    public OnlineDownload(CoreWebView2DownloadOperation operation, string path, string serviceName, long? rootId, bool folderAway, IDesktopShell shell)
    {
        (_operation, Path, ServiceName, RootId, _folderAway, _shell) = (operation, path, serviceName, rootId, folderAway, shell);
        Name = System.IO.Path.GetFileName(path);
        operation.BytesReceivedChanged += (_, _) => Show();
        operation.StateChanged += (_, _) => Show();
        Show();
    }

    public string Name { get; }

    public string ServiceName { get; }

    /// <summary>Where the browser writes it.</summary>
    public string Path { get; }

    /// <summary>The library folder it lands in; null when it went to the browser's Downloads folder.</summary>
    public long? RootId { get; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShowInFolderCommand))]
    public partial bool Saved { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool InProgress { get; private set; } = true;

    /// <summary>Raised once when it is saved, stopped or cancelled.</summary>
    public event EventHandler? Finished;

    void Show()
    {
        var state = _operation.State;
        switch (state)
        {
            case CoreWebView2DownloadState.InProgress:
                var total = (long?)_operation.TotalBytesToReceive;
                var received = _operation.BytesReceived;
                Status = total is > 0
                    ? $"Downloading… {received * 100 / total.Value}%"
                    : $"Downloading… {received / (1024 * 1024)} MB";
                return;
            case CoreWebView2DownloadState.Completed:
                Saved = true;
                Status = RootId is not null
                    ? "Saved in your downloads folder. Bibliotaph is reading it."
                    : _folderAway
                        ? "Your downloads folder can't be reached, so it went to your Downloads folder."
                        : "Saved in your Downloads folder. Choose a downloads folder in Online libraries to have downloads join your library.";
                break;
            default:
                Status = _operation.InterruptReason is CoreWebView2DownloadInterruptReason.UserCanceled
                    ? "Cancelled."
                    : $"Stopped: {Describe(_operation.InterruptReason)}.";
                break;
        }
        if (!InProgress) return;
        InProgress = false;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    static string Describe(CoreWebView2DownloadInterruptReason reason) => reason switch
    {
        CoreWebView2DownloadInterruptReason.FileNoSpace => "the disk is full",
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "Windows didn't let the browser write there",
        CoreWebView2DownloadInterruptReason.NetworkFailed or CoreWebView2DownloadInterruptReason.NetworkDisconnected
            or CoreWebView2DownloadInterruptReason.NetworkTimeout => "the connection dropped",
        CoreWebView2DownloadInterruptReason.FileBlockedByPolicy or CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed
            or CoreWebView2DownloadInterruptReason.FileMalicious => "Windows blocked the file",
        _ => "the download didn't finish",
    };

    [RelayCommand(CanExecute = nameof(InProgress))]
    void Cancel() => _operation.Cancel();

    [RelayCommand(CanExecute = nameof(Saved))]
    void ShowInFolder()
    {
        if (File.Exists(Path)) _shell.ShowInFolder(Path);
    }
}
