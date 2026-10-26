namespace Bibliotaph.Api.Contracts;

/// <summary>
/// A backup the server keeps, as Settings › Backup lists it: a weekly one (<paramref name="Weekly"/>) or the one made
/// just before a restore.
/// </summary>
public sealed record KeptBackupInfo(string Name, DateTimeOffset Created, long Bytes, bool Weekly);

/// <summary>
/// A backup read for restoring, as the Windows app's Restore shows it before anything changes: when and by what it was
/// made, what it holds, and its library folders, each found in this library's storage or staying offline.
/// </summary>
public sealed record RestorePreviewInfo(string Token, DateTimeOffset Created, string App, int Books, int Collections, int Notes,
    IReadOnlyList<RestoreFolderInfo> Folders);

/// <summary>A library folder in a backup: where it is, as the details say it, and whether this library can reach it.</summary>
public sealed record RestoreFolderInfo(string Location, bool Found, int Files);

/// <summary>Reading a backup for restoring: its preview, or why it can't be restored ("made by a newer Bibliotaph").</summary>
public sealed record RestoreRead(RestorePreviewInfo? Preview, string? Problem);
