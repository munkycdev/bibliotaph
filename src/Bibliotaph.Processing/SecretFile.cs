using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Secrets by name in one file that only its owner can read or write (0600), for a host whose own secret store its
/// builds can't use without asking: the Mac, whose login keychain asks for the keychain password whenever a build
/// signed differently from the one that saved an item reads it, as every local build is. Any program running as the
/// same user can read the file, which is what Windows Credential Manager allows too. The whole file is written beside
/// itself and moved into place, so a crash leaves either the old file or the new one. A secret is never logged, and a
/// failure is logged by its type alone, since a JSON error can quote the file.
/// </summary>
public sealed class SecretFile(string path, ILogger? log = null, Action<string>? written = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    readonly ILogger _log = log ?? NullLogger.Instance;
    readonly Lock _lock = new();
    Dictionary<string, string>? _secrets;

    /// <summary>What's kept in the file: the secrets by name, in a field of their own so more can be added beside them.</summary>
    sealed record Contents(Dictionary<string, string>? Secrets);

    /// <summary>Whether the file has been written, even with nothing in it.</summary>
    public bool Exists
    {
        get
        {
            lock (_lock) return File.Exists(path);
        }
    }

    public string? Find(string name)
    {
        lock (_lock) return Load().GetValueOrDefault(name);
    }

    /// <summary>The names starting with <paramref name="prefix"/>, in order.</summary>
    public IReadOnlyList<string> Names(string prefix)
    {
        lock (_lock) return [.. Load().Keys.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Saves a secret in place of any it replaces. Returns false, keeping what was there, when the file can't be written.</summary>
    public bool Remember(string name, string secret)
    {
        lock (_lock)
        {
            var secrets = new Dictionary<string, string>(Load(), StringComparer.Ordinal) { [name] = secret };
            return Write(secrets);
        }
    }

    public void Forget(string name)
    {
        lock (_lock)
        {
            if (!Load().ContainsKey(name)) return;
            var secrets = new Dictionary<string, string>(Load(), StringComparer.Ordinal);
            secrets.Remove(name);
            Write(secrets);
        }
    }

    /// <summary>Forgets every secret whose name starts with <paramref name="prefix"/> ("" for all), and returns how many.</summary>
    public int ForgetAll(string prefix)
    {
        lock (_lock)
        {
            var secrets = new Dictionary<string, string>(Load(), StringComparer.Ordinal);
            var gone = secrets.Keys.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (gone.Count == 0) return 0;
            foreach (var name in gone) secrets.Remove(name);
            return Write(secrets) ? gone.Count : 0;
        }
    }

    /// <summary>
    /// Writes the file for the first time, with <paramref name="first"/> in it. Returns false when it already exists or
    /// can't be written, so a host moving secrets in from elsewhere does that once and keeps the originals on a failure.
    /// </summary>
    public bool Create(IReadOnlyDictionary<string, string> first)
    {
        lock (_lock)
        {
            if (File.Exists(path)) return false;
            return Write(new Dictionary<string, string>(first, StringComparer.Ordinal));
        }
    }

    Dictionary<string, string> Load()
    {
        if (_secrets is not null) return _secrets;
        _secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return _secrets;
            // A file copied in, or made before this class, is made owner-only again before it's read.
            if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~OwnerOnly) != 0) File.SetUnixFileMode(path, OwnerOnly);
            if (JsonSerializer.Deserialize<Contents>(File.ReadAllBytes(path), Json)?.Secrets is { } secrets)
                foreach (var (name, secret) in secrets) _secrets[name] = secret;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Read as empty: what was saved is asked for again, and the next save replaces the file.
            _log.LogWarning("Reading the saved secrets failed: {Error}", ex.GetType().Name);
        }
        return _secrets;
    }

    bool Write(Dictionary<string, string> secrets)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, new Contents(secrets), Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
            _secrets = secrets;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Saving the secrets failed: {Error}", ex.GetType().Name);
            try
            {
                File.Delete(temp);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                // Left beside the file, owner-only like it; it's never read.
            }
            return false;
        }
        written?.Invoke(path);
        return true;
    }
}
