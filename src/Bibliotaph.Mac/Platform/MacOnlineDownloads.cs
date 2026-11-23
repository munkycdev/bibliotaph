using System.Collections.ObjectModel;
using Bibliotaph.Catalog;
using Bibliotaph.Catalog.Entities;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Where a store's own Download buttons in Bibliotaph's browser save, as on Windows: the downloads folder, a library
/// folder or one inside it so what is downloaded joins the library, or else the Mac's Downloads folder.
/// </summary>
public sealed class MacOnlineDownloads(SettingsStore settings, SourceRootStore roots, IJobControl indexing, ILogger<MacOnlineDownloads> log)
{
    string? _folder;
    bool _loaded;

    /// <summary>This session's downloads, newest first, which every browser window lists.</summary>
    public ObservableCollection<MacDownload> Items { get; } = [];

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
            return "Choose a library folder or a folder inside one, so Bibliotaph reads what you download. Add the folder in Settings › Library first if it isn't one.";
        await settings.SetAsync(SettingKeys.DownloadsFolder, folder);
        (_folder, _loaded) = (folder, true);
        return null;
    }

    public async Task ClearFolderAsync()
    {
        await settings.SetAsync(SettingKeys.DownloadsFolder, "");
        (_folder, _loaded) = (null, true);
    }

    /// <summary>Where a download goes: a free name in the downloads folder when it can be reached, else in ~/Downloads.</summary>
    public async Task<MacDownload> StartAsync(string suggested, string serviceName)
    {
        var folder = await FolderAsync();
        SourceRoot? root = null;
        if (folder is not null && Directory.Exists(folder))
            root = DownloadPlaces.RootOf(folder, await roots.ListAsync(), r => r.Path);
        var target = root is not null ? folder! : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var path = DownloadPlaces.FreePath(target, Path.GetFileName(suggested), File.Exists);
        var item = new MacDownload(path, serviceName, root?.Id, folder is not null && root is null);
        Items.Insert(0, item);
        return item;
    }

    /// <summary>A download saved into the library is read now, rather than when the folder is next looked at.</summary>
    public void Finished(MacDownload item)
    {
        if (item.RootId is { } id && item.Saved) indexing.RequestScan(id);
    }
}

/// <summary>One download in Bibliotaph's browser: its name, how it went, and where it went.</summary>
public sealed partial class MacDownload(string path, string serviceName, long? rootId, bool folderAway) : ObservableObject
{
    public string Name { get; } = System.IO.Path.GetFileName(path);

    public string ServiceName { get; } = serviceName;

    public string Path { get; } = path;

    public long? RootId { get; } = rootId;

    [ObservableProperty]
    public partial string Status { get; private set; } = "Downloading…";

    [ObservableProperty]
    public partial bool Saved { get; private set; }

    public void Done()
    {
        Saved = true;
        Status = RootId is not null
            ? "Saved in your downloads folder. Bibliotaph is reading it."
            : folderAway
                ? "Your downloads folder can't be reached, so it went to your Downloads folder."
                : "Saved in your Downloads folder. Choose a downloads folder in Online libraries to have downloads join your library.";
    }

    public void Failed(string reason) => Status = $"Stopped: {reason}.";
}
