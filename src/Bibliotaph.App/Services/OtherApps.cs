using System.ComponentModel;
using System.Diagnostics;
using Bibliotaph.Catalog;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>Opens a file in the app Windows has for its type. An interface so the smoke test never starts another app.</summary>
public interface IShellLauncher
{
    /// <summary>Returns why it couldn't, or null once Windows has it.</summary>
    string? Open(string path);
}

public sealed class ShellLauncher(ILogger<ShellLauncher> log) : IShellLauncher
{
    public string? Open(string path)
    {
        try
        {
            // Windows opens the file in place: nothing is copied, and the app it starts is the user's own default.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
        {
            log.LogWarning("Opening a file in another app failed: {Message}", ex.Message);
            return ex.Message;
        }
    }
}

/// <summary>Records what would have been opened, for the smoke test.</summary>
public sealed class SmokeShellLauncher : IShellLauncher
{
    readonly Lock _lock = new();
    readonly List<string> _opened = [];

    public IReadOnlyList<string> Opened
    {
        get { lock (_lock) return [.. _opened]; }
    }

    public string? Open(string path)
    {
        lock (_lock) _opened.Add(path);
        return null;
    }
}

/// <summary>
/// Open in another app (slice 4i plan, choice 4), for a PDF whose protection Bibliotaph can't open: the file goes to
/// the user's default PDF app, read where it is. A file inside a ZIP can't be opened by itself without being copied
/// out, so its ZIP is opened instead, in whatever Windows opens ZIPs with.
/// </summary>
public sealed class OtherApps(LibraryStore library, IShellLauncher launcher)
{
    /// <summary>Returns what to tell the user when it couldn't, or null once the file is open elsewhere.</summary>
    public async Task<string?> OpenAsync(long documentId, CancellationToken ct = default)
    {
        if (await library.GetSourceAsync(documentId, ct) is not { } source)
            return "No copy of this file can be opened right now: its folder is offline or the file has gone.";
        var path = source.ArchivePath ?? source.FullPath;
        return launcher.Open(path) is { } error ? $"Windows couldn't open it: {error}" : null;
    }
}
