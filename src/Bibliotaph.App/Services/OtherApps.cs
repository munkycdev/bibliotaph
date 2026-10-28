using System.ComponentModel;
using System.Diagnostics;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
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
/// Open in another app, for a PDF whose protection Bibliotaph can't open: the file goes to the user's default PDF app,
/// read where it is. A file inside a ZIP can't be opened by itself without being copied out, so its ZIP is opened
/// instead, in whatever Windows opens ZIPs with. A book only the web library has opens from its copy downloaded here.
/// </summary>
public sealed class OtherApps(LibraryStore library, IShellLauncher launcher, IKeptCopies? kept = null)
{
    /// <summary>Returns what to tell the user when it couldn't, or null once the file is open elsewhere.</summary>
    public async Task<string?> OpenAsync(long documentId, CancellationToken ct = default)
    {
        if (await library.GetSourceAsync(documentId, ct) is not { } source)
            return "No copy of this file can be opened right now: its folder is offline or the file has gone.";
        LocalFile? copy = null;
        try
        {
            if (kept is not null && kept.Holds(source)) copy = await kept.OpenAsync(source, ct);
        }
        catch (System.IO.IOException ex)
        {
            return $"It couldn't be downloaded from your web library. {ex.Message}";
        }
        // A downloaded copy is held only while Windows starts the app; one the other app has open can't be deleted under it.
        await using var held = copy;
        var path = copy?.Path ?? source.ArchivePath ?? source.FullPath;
        return launcher.Open(path) is { } error ? $"Windows couldn't open it: {error}" : null;
    }
}

/// <summary>
/// Opens a book where the user owns it online: its Also own link, in the user's own browser. The link is one the user
/// put there, so it opens without asking, unlike a link inside a PDF. Only http and https addresses open.
/// </summary>
public sealed class OnlineBooks(IShellLauncher launcher)
{
    /// <summary>Returns what to tell the user when it couldn't, or null once the browser has it.</summary>
    public string? Open(string? link)
    {
        if (link is null || Core.Metadata.StoreLink.WebAddress(link) is not { } address) return "This book has no web link to open.";
        return launcher.Open(address) is { } error ? $"Windows couldn't open its page: {error}" : null;
    }
}
