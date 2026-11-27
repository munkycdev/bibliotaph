using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bibliotaph.Processing;

/// <summary>Where a file in a cloud-synced folder (OneDrive) keeps its content, as far as its attributes tell.</summary>
public enum CloudFileState
{
    /// <summary>It can't be asked, as when it has gone.</summary>
    Unknown,

    /// <summary>Its content is on this device: an ordinary file, or a cloud file that has been downloaded.</summary>
    Local,

    /// <summary>Only in the cloud, or on its way back there (the user chose Free up space): reading it downloads it.</summary>
    OnlineOnly,

    /// <summary>The user chose "Always keep on this device". It stays as it is.</summary>
    Pinned,
}

/// <summary>
/// The Windows cloud files platform, as far as Bibliotaph uses it: whether a library file is online-only, and giving a
/// downloaded one back to online-only after indexing. Behind an interface so the decisions about which files go back,
/// and when, are tested without Windows.
/// </summary>
public interface ICloudFiles
{
    CloudFileState StateOf(string path);

    /// <summary>
    /// Returns a downloaded cloud file to online-only, as File Explorer's Free up space does. Its content, path and
    /// timestamps don't change; only the copy on this device goes, once the sync app has let it go. Throws
    /// <see cref="IOException"/> when it can't, as for a file that isn't a cloud file.
    /// </summary>
    void ReturnToOnlineOnly(string path);

    /// <summary>
    /// Whether this is a cloud file that <see cref="KeepOnDevice"/> could keep on this device: one the platform may free
    /// up, not one already kept there. False where the platform has no such call to make.
    /// </summary>
    bool CanKeepOnDevice(string path) => false;

    /// <summary>
    /// Keeps a cloud file on this device, as File Explorer's "Always keep on this device" does, and downloads it now.
    /// Throws <see cref="IOException"/> when it can't.
    /// </summary>
    void KeepOnDevice(string path) => throw new IOException("Files can't be kept on this device here.");

    /// <summary>
    /// Undoes <see cref="KeepOnDevice"/>: the file goes back to the sync app's own choice, staying downloaded until it
    /// needs the space, rather than going back to online-only at once. Throws <see cref="IOException"/> when it can't.
    /// </summary>
    void LetGoOnDevice(string path) => throw new IOException("Files can't be kept on this device here.");
}

/// <summary>
/// The one exception to Bibliotaph never changing a library file (architecture: OneDrive, and the build-enforced
/// rules): it sets where the cloud files platform keeps a file's content, and nothing else. It takes back the local
/// copy of a file that reading downloaded, with <c>CfSetPinState</c> and <c>CF_PIN_STATE_UNPINNED</c>, which Microsoft
/// documents as callable by any application, not just the sync app, with a handle that only reads attributes; it is
/// what File Explorer's Free up space does. The sync app then frees the space when it can, which it won't do while
/// another app has the file open. <c>CfDehydratePlaceholder</c> was not used: it needs an exclusive handle, and is
/// meant for the sync app itself. For a favorite or a binder's book it does the opposite, as "Always keep on this
/// device" does: <c>CF_PIN_STATE_PINNED</c>, then <c>CfHydratePlaceholder</c> to download it now, and later
/// <c>CF_PIN_STATE_UNSPECIFIED</c> to give the choice back. Content, path and timestamps never change.
/// </summary>
public sealed class CloudFiles : ICloudFiles
{
    // File attributes .NET has no names for. Reading them never downloads a file.
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    const FileAttributes Pinned = (FileAttributes)0x00080000;
    const FileAttributes Unpinned = (FileAttributes)0x00100000;
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    const uint FileReadAttributes = 0x0080;
    const uint ShareAll = 0x1 | 0x2 | 0x4;
    const uint OpenExisting = 3;
    const uint FileReadData = 0x0001;
    const int PinStateUnspecified = 0; // CF_PIN_STATE_UNSPECIFIED
    const int PinStatePinned = 1; // CF_PIN_STATE_PINNED
    const int PinStateUnpinned = 2; // CF_PIN_STATE_UNPINNED
    const int HydrateFlagNone = 0;
    const int SetPinFlagNone = 0;

    public CloudFileState StateOf(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return CloudFileState.Unknown;
        }
        if ((attributes & Pinned) != 0) return CloudFileState.Pinned;
        if ((attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline | Unpinned)) != 0) return CloudFileState.OnlineOnly;
        return CloudFileState.Local;
    }

    public void ReturnToOnlineOnly(string path) => SetPinState(path, PinStateUnpinned);

    public bool CanKeepOnDevice(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // A cloud file is a reparse point whether or not it's downloaded; an ordinary file on a disk isn't.
            var attributes = File.GetAttributes(path);
            return (attributes & Pinned) == 0 && (attributes & FileAttributes.Directory) == 0
                && (attributes & (FileAttributes.ReparsePoint | RecallOnDataAccess | RecallOnOpen | Unpinned)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void KeepOnDevice(string path)
    {
        SetPinState(path, PinStatePinned);
        // Pinned, the sync app downloads it in its own time; asking now means it's here when the call returns.
        using var handle = Open(path, FileReadAttributes | FileReadData);
        var result = CfHydratePlaceholder(handle, 0, -1, HydrateFlagNone, IntPtr.Zero);
        if (result < 0) throw new IOException(Marshal.GetExceptionForHR(result)?.Message ?? $"Error 0x{result:X8}", result);
    }

    public void LetGoOnDevice(string path) => SetPinState(path, PinStateUnspecified);

    static void SetPinState(string path, int state)
    {
        // An attribute handle is enough, and opening one neither downloads the file nor gets in anyone's way.
        using var handle = Open(path, FileReadAttributes);
        var result = CfSetPinState(handle, state, SetPinFlagNone, IntPtr.Zero);
        if (result < 0) throw new IOException(Marshal.GetExceptionForHR(result)?.Message ?? $"Error 0x{result:X8}", result);
    }

    static SafeFileHandle Open(string path, uint access)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Cloud files are kept or let go only on Windows.");
        var handle = CreateFile(path, access, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new IOException(new Win32Exception(error).Message);
    }

    [DllImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("cldapi")]
    static extern int CfSetPinState(SafeFileHandle fileHandle, int pinState, int pinFlags, IntPtr overlapped);

    [DllImport("cldapi")]
    static extern int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length, int hydrateFlags, IntPtr overlapped);
}
