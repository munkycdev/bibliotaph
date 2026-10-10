using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bibliotaph.Processing;

/// <summary>
/// The disk a library folder is on: its volume serial, and whether its files have IDs that last through a move or
/// rename (NTFS and ReFS; FAT and exFAT number a file by where its directory entry sits, which a move changes).
/// </summary>
public sealed record VolumeIdentity(string Serial, bool HasFileIds);

/// <summary>
/// What tells a file or a disk apart without reading it (slice 4g plan, choices 2 and 3): the volume serial a
/// folder is on, so a different disk in its drive reads as offline, and a file's ID, so a moved file is recognised
/// without being hashed again. Both open the item for its attributes only, which never downloads a cloud file.
/// </summary>
public interface IFileIdentity
{
    /// <returns>The folder's disk, or null when it can't be asked (not Windows, or the folder can't be opened).</returns>
    VolumeIdentity? Volume(string folder);

    /// <returns>The file's ID, unique on every disk, or null when it can't be read.</returns>
    string? FileId(string path);
}

/// <summary>The Windows file system's own answers. Elsewhere there are none, so moves are found by their hash instead.</summary>
public sealed class FileIdentity : IFileIdentity
{
    const uint FileReadAttributes = 0x0080;
    const uint ShareAll = 0x1 | 0x2 | 0x4;
    const uint OpenExisting = 3;
    const uint BackupSemantics = 0x02000000; // needed to open a folder
    const uint OpenReparsePoint = 0x00200000; // the placeholder itself, so a cloud file's provider isn't asked for it
    const int FileIdInfoClass = 18;

    public VolumeIdentity? Volume(string folder)
    {
        if (!OperatingSystem.IsWindows()) return null;
        // A library folder may itself be a link to another disk, so this one is followed.
        using var handle = Open(folder, BackupSemantics);
        if (handle is null || !GetFileInformationByHandle(handle, out var info)) return null;
        var name = new char[64];
        var hasIds = GetVolumeInformationByHandle(handle, null, 0, out _, out _, out _, name, name.Length)
            && new string(name).TrimEnd('\0') is "NTFS" or "ReFS";
        return new VolumeIdentity(info.VolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture), hasIds);
    }

    public string? FileId(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = Open(path, BackupSemantics | OpenReparsePoint);
        if (handle is null || !GetFileInformationByHandle(handle, out var info)) return null;
        var serial = info.VolumeSerialNumber.ToString("X8", CultureInfo.InvariantCulture);
        // ReFS needs the 128-bit ID; the 64-bit index is NTFS's file reference number, which is the same thing there.
        if (GetFileInformationByHandleEx(handle, FileIdInfoClass, out var id, Marshal.SizeOf<FileIdInfo>()))
            return $"{serial}:{id.FileIdHigh:X16}{id.FileIdLow:X16}";
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return $"{serial}:{index:X16}";
    }

    static SafeFileHandle? Open(string path, uint flags)
    {
        var handle = CreateFile(path, FileReadAttributes, ShareAll, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileIdInfo
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileIdInfo information, int size);

    [DllImport("kernel32", EntryPoint = "GetVolumeInformationByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeInformationByHandle(SafeFileHandle file, char[]? volumeName, int volumeNameSize, out uint serial, out uint maxComponent,
        out uint flags, [Out] char[] fileSystemName, int fileSystemNameSize);
}
