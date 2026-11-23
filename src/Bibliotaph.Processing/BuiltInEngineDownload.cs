using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// The llama.cpp release the built-in AI runs on: build/engine.json, which build/fetch-engine.ps1 reads too. Windows
/// runs its Vulkan build; a Mac its Apple silicon build, which draws on Metal.
/// </summary>
public sealed record EnginePin(string Release, string Asset, string Sha256)
{
    public Uri DownloadUrl => new($"https://github.com/ggml-org/llama.cpp/releases/download/{Release}/{Asset}");

    /// <summary>The release comes as a gzipped tarball, as the Mac's does, rather than a zip.</summary>
    public bool IsTarball => Asset.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    /// <summary>"llama.cpp b11552", as engine.txt says it.</summary>
    public string Version => $"llama.cpp {Release}";

    /// <summary>The pin built into this copy of Bibliotaph, for the system it runs on.</summary>
    public static EnginePin Embedded { get; } = Load(OperatingSystem.IsMacOS() ? "engineMac" : "engine");

    /// <summary>The Mac's pin, whatever this runs on.</summary>
    public static EnginePin Mac { get; } = Load("engineMac");

    internal static EnginePin Load(string name)
    {
        using var stream = typeof(EnginePin).Assembly.GetManifestResourceStream("Bibliotaph.Processing.engine.json")
            ?? throw new InvalidOperationException("engine.json is not embedded.");
        using var json = JsonDocument.Parse(stream);
        var engine = json.RootElement.GetProperty(name);
        return new EnginePin(engine.GetProperty("release").GetString()!, engine.GetProperty("asset").GetString()!, engine.GetProperty("sha256").GetString()!);
    }
}

/// <summary>
/// Fetches the built-in AI's engine at startup when this copy of Bibliotaph doesn't have one beside it, as a build made
/// without build/fetch-engine.ps1 doesn't. It downloads the pinned llama.cpp release from GitHub, checks its SHA-256,
/// and keeps only llama-server and the libraries it loads, in the engine folder. An installed copy has the engine
/// already and downloads nothing; a download that fails is tried again at the next start.
/// </summary>
public sealed class BuiltInEngineDownload(BuiltInEngine engine, HttpClient http, EnginePin? pin = null, ILogger<BuiltInEngineDownload>? log = null)
{
    readonly EnginePin _pin = pin ?? EnginePin.Embedded;
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;

    /// <summary>The download is running.</summary>
    public bool IsDownloading { get; private set; }

    /// <summary>Why the last download didn't finish, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised, on any thread, when the download starts and ends.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised, on any thread, when a downloaded engine is ready to use.</summary>
    public event EventHandler? Downloaded;

    /// <summary>The engine needs downloading: none came with this copy, and none of the pinned release has been downloaded.</summary>
    public bool IsNeeded =>
        !engine.IsBundled && engine.Options.DownloadFolder is { } folder
        && !(BuiltInEngine.ServerIn(folder) is not null && ReadVersion(folder) == _pin.Version);

    /// <summary>Downloads the engine if it's needed. Returns false when it was needed and couldn't be downloaded.</summary>
    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        if (!IsNeeded) return true;
        var folder = engine.Options.DownloadFolder!;
        IsDownloading = true;
        Problem = null;
        Changed?.Invoke(this, EventArgs.Empty);
        var parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        var zip = Path.Combine(parent, _pin.IsTarball ? "engine-download.tar.gz" : "engine-download.zip");
        var fresh = folder + ".new";
        try
        {
            Directory.CreateDirectory(parent);
            _log.LogInformation("Downloading the built-in AI engine, {Version}", _pin.Version);
            await FetchAsync(zip, ct);
            if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
            if (_pin.IsTarball) ExtractTarball(zip, fresh);
            else Extract(zip, fresh);
            File.WriteAllText(Path.Combine(fresh, "engine.txt"), _pin.Version);
            // An engine of an earlier pin goes. If a book has started it meanwhile, it can't be deleted yet, and the next
            // start tries again.
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.Move(fresh, folder);
            _log.LogInformation("Downloaded the built-in AI engine, {Version}, into {Folder}", _pin.Version, folder);
            Finish(null);
            Downloaded?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            Finish(null);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ModelDownloadException)
        {
            _log.LogWarning(ex, "Could not download the built-in AI engine");
            Finish($"The built-in AI engine couldn't be downloaded: {ex.Message} Bibliotaph tries again the next time it starts.");
            return false;
        }
        finally
        {
            if (File.Exists(zip)) File.Delete(zip);
            if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        }
    }

    void Finish(string? problem)
    {
        IsDownloading = false;
        Problem = problem;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    async Task FetchAsync(string zip, CancellationToken ct)
    {
        using (var response = await http.GetAsync(_pin.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new ModelDownloadException($"GitHub wouldn't send it ({(int)response.StatusCode} {response.ReasonPhrase}).");
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await source.CopyToAsync(target, ct);
        }
        string actual;
        await using (var file = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (actual != _pin.Sha256) throw new ModelDownloadException("the file that arrived isn't the one Bibliotaph expects.");
    }

    /// <summary>
    /// llama-server and what sits beside it in the release (its libraries and licences), as build/fetch-engine.ps1 keeps:
    /// the release's other programs, and their -impl libraries, aren't used. Files only, by name, so nothing lands
    /// outside <paramref name="folder"/>.
    /// </summary>
    static void Extract(string zip, string folder)
    {
        using var archive = ZipFile.OpenRead(zip);
        var server = archive.Entries.FirstOrDefault(e => BuiltInEngine.IsServer(e.Name))
            ?? throw new InvalidDataException("the release has no llama-server.");
        var directory = server.FullName[..^server.Name.Length];
        Directory.CreateDirectory(folder);
        foreach (var entry in archive.Entries)
        {
            if (entry.Name.Length == 0 || entry.FullName != directory + entry.Name || !Keep(entry.Name)) continue;
            entry.ExtractToFile(Path.Combine(folder, entry.Name), overwrite: true);
        }
    }

    /// <summary>
    /// The Mac's release, a gzipped tarball with everything in build/bin: the server, its libraries, which link to one
    /// another by symbolic links, and Metal's shaders. The server keeps its executable bit, and a link stays a link to
    /// a file beside it, so nothing lands outside <paramref name="folder"/>.
    /// </summary>
    static void ExtractTarball(string tarball, string folder)
    {
        using var file = File.OpenRead(tarball);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var directory = (string?)null;
        var entries = new List<(string Name, TarEntry Entry)>();
        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            var name = Path.GetFileName(entry.Name.TrimEnd('/'));
            if (BuiltInEngine.IsServer(name) && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile) directory = entry.Name[..^name.Length];
            entries.Add((name, entry));
        }
        if (directory is null) throw new InvalidDataException("the release has no llama-server.");
        Directory.CreateDirectory(folder);
        foreach (var (name, entry) in entries)
        {
            if (name.Length == 0 || entry.Name != directory + name || !Keep(name)) continue;
            var target = Path.Combine(folder, name);
            switch (entry.EntryType)
            {
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    entry.ExtractToFile(target, overwrite: true);
                    break;
                case TarEntryType.SymbolicLink when Path.GetFileName(entry.LinkName) == entry.LinkName && entry.LinkName.Length > 0:
                    if (File.Exists(target) || Path.Exists(target)) File.Delete(target);
                    File.CreateSymbolicLink(target, entry.LinkName);
                    break;
            }
        }
        if (!OperatingSystem.IsWindows() && BuiltInEngine.ServerIn(folder) is { } server)
            File.SetUnixFileMode(server, File.GetUnixFileMode(server) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    /// <summary>
    /// The server, its libraries and Metal's shaders, and the licences: the release's other programs, and their -impl
    /// libraries, aren't used.
    /// </summary>
    internal static bool Keep(string name)
    {
        if (BuiltInEngine.IsServer(name)) return true;
        var extension = Path.GetExtension(name);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        // The Mac's programs have no extension; the licences beside them don't either.
        if (extension.Length == 0) return name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);
        // Each program's own library: only the server's is kept, llama-server-impl.dll or libllama-server-impl.dylib.
        var impl = name.EndsWith("-impl.dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-impl.dylib", StringComparison.Ordinal);
        return !impl || name.Equals("llama-server-impl.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("libllama-server-impl.dylib", StringComparison.Ordinal);
    }

    static string? ReadVersion(string folder)
    {
        var note = Path.Combine(folder, "engine.txt");
        return File.Exists(note) ? File.ReadAllText(note).Trim() : null;
    }
}

public static class BuiltInEngineDownloadWatch
{
    /// <summary>Wakes the Classify lane when the engine arrives, if the built-in AI is what reads books.</summary>
    public static void WakeAiWhenDownloaded(this BuiltInEngineDownload download, AiSettings ai) =>
        download.Downloaded += (_, _) =>
        {
            if (ai.Setup.BuiltIn) ai.Refresh();
        };
}
