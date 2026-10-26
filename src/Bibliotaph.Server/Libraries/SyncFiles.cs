using Bibliotaph.Core.Sync;
using Bibliotaph.Processing;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Server.Libraries;

/// <summary>A device's sync file as the web library keeps it.</summary>
public sealed record StoredSyncFile(string Device, string Machine, long Version);

/// <summary>
/// The sync files of one user's devices, and the web library's own, in sync.db beside the catalog: the folder a
/// PC's library sync would otherwise use, kept where Litestream streams it like any other database. The web
/// library's own file is under <see cref="WebDevice"/>; each PC's, under its device ID.
/// </summary>
public sealed class SyncFiles(string path)
{
    public const string WebDevice = "web";

    /// <summary>What the web library calls itself in other devices' sync status, as a PC is called by its name.</summary>
    public const string WebMachine = "bibliotaph.online";

    // Not pooled, so closing the library leaves nothing holding the file.
    readonly string _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        // WAL, as Litestream needs.
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS files (
                device TEXT PRIMARY KEY,
                machine TEXT NOT NULL,
                version INTEGER NOT NULL,
                content BLOB NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<StoredSyncFile>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT device, machine, version FROM files ORDER BY device";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var files = new List<StoredSyncFile>();
        while (await reader.ReadAsync(ct)) files.Add(new StoredSyncFile(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        return files;
    }

    public async Task<byte[]?> ReadAsync(string device, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT content FROM files WHERE device = $device";
        command.Parameters.AddWithValue("$device", device);
        return await command.ExecuteScalarAsync(ct) as byte[];
    }

    /// <summary>Keeps a device's file in place of the one before, with a new version.</summary>
    public async Task WriteAsync(string device, string machine, byte[] content, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO files (device, machine, version, content) VALUES ($device, $machine, 1, $content)
            ON CONFLICT (device) DO UPDATE SET machine = excluded.machine, version = files.version + 1, content = excluded.content
            """;
        command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$machine", machine);
        command.Parameters.AddWithValue("$content", content);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string device, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM files WHERE device = $device";
        command.Parameters.AddWithValue("$device", device);
        await command.ExecuteNonQueryAsync(ct);
    }

    async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(_connection);
        await db.OpenAsync(ct);
        return db;
    }
}

/// <summary>
/// The web library's side of library sync: it is one more device, which reads its PCs' files from sync.db and writes
/// its own there. A PC sending its file says so (<see cref="Received"/>), so it's merged at once.
/// </summary>
public sealed class ServerSyncChannel(SyncFiles files, ILogger<ServerSyncChannel> log) : ISyncChannel
{
    readonly Dictionary<string, (long Version, SyncFileContent Content)> _read = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    volatile bool _hasDevices;

    /// <summary>Only once a PC has sent its file: a library no PC syncs with has no file of its own to keep.</summary>
    public bool IsOn => _hasDevices;

    public event EventHandler? Changed;

    /// <summary>Whether any PC has sent its file, as the library opens.</summary>
    public async Task LoadAsync(CancellationToken ct = default) =>
        _hasDevices = (await files.ListAsync(ct)).Any(f => f.Device != SyncFiles.WebDevice);

    /// <summary>A PC's file arrived.</summary>
    public void Received()
    {
        _hasDevices = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SyncChannelRead> ReadPeersAsync(CancellationToken ct)
    {
        var peers = new List<SyncFileContent>();
        string? problem = null;
        foreach (var file in (await files.ListAsync(ct)).Where(f => f.Device != SyncFiles.WebDevice))
        {
            lock (_lock)
                if (_read.TryGetValue(file.Device, out var known) && known.Version == file.Version)
                {
                    peers.Add(known.Content);
                    continue;
                }
            if (await files.ReadAsync(file.Device, ct) is not { } bytes) continue;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                var content = await SyncFile.ReadAsync(stream, ct);
                lock (_lock) _read[file.Device] = (file.Version, content);
                peers.Add(content);
            }
            catch (InvalidDataException ex)
            {
                log.LogWarning(ex, "A device's sync file can't be read");
                problem ??= $"{file.Machine}'s sync file can't be read. {ex.Message}";
            }
        }
        return new SyncChannelRead(peers, problem);
    }

    public async Task<string?> WriteOwnAsync(Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await write(buffer, ct);
        await files.WriteAsync(SyncFiles.WebDevice, SyncFiles.WebMachine, buffer.ToArray(), ct);
        return null;
    }

    // A PC's file arriving says so at once.
    public Task<bool> HasChangesAsync(CancellationToken ct) => Task.FromResult(false);
}
