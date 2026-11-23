using Bibliotaph.Processing;
using Foundation;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// The Mac's cloud files, as far as Bibliotaph uses them, for what Windows does with OneDrive's: whether a library file
/// in iCloud Drive or a File Provider folder (OneDrive, Dropbox, Google Drive) is only in the cloud, and giving one that
/// reading downloaded back to the cloud, as Finder's Remove Download does. Nothing else of the file changes, and the
/// Mac downloads it again when it's opened.
/// </summary>
public sealed class MacCloudFiles : ICloudFiles
{
    public CloudFileState StateOf(string path)
    {
        if (!File.Exists(path)) return CloudFileState.Unknown;
        using var url = NSUrl.CreateFileUrl(path, false, null);
        if (!url.TryGetResource(NSUrl.IsUbiquitousItemKey, out var ubiquitous) || ubiquitous is not NSNumber { BoolValue: true }) return CloudFileState.Local;
        return url.TryGetResource(NSUrl.UbiquitousItemDownloadingStatusKey, out var status) && status is NSString text
            && text.ToString() == NSUrl.UbiquitousItemDownloadingStatusNotDownloaded.ToString()
            ? CloudFileState.OnlineOnly
            : CloudFileState.Local;
    }

    public void ReturnToOnlineOnly(string path)
    {
        using var url = NSUrl.CreateFileUrl(path, false, null);
        if (!NSFileManager.DefaultManager.EvictUbiquitous(url, out var error))
            throw new IOException(error?.LocalizedDescription ?? "The Mac kept its copy of the file.");
    }

    /// <summary>For the library's listing: a file only in the cloud is read from there, and waits for disk space.</summary>
    public bool IsOnlineOnly(string path) => StateOf(path) == CloudFileState.OnlineOnly;
}
