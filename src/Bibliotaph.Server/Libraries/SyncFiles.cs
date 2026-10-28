using Bibliotaph.Api.Contracts;
using Bibliotaph.Core.Sync;
using Bibliotaph.Processing;
using Microsoft.Data.Sqlite;

namespace Bibliotaph.Server.Libraries;

/// <summary>A device's sync file as the web library keeps it.</summary>
public sealed record StoredSyncFile(string Device, string Machine, long Version);

/// <summary>
/// The sync files of one user's devices, and the web library's own, in sync.db beside the catalog: the folder a
/// PC's library sync would otherwise use, kept where Litestream streams it like any other database. The web
/// library's own file is under <see cref="WebDevice"/>; each PC's, under its device ID. Beside them are the results
/// the devices share (<see cref="SharedResults"/>), the newest for each file's content and kind, numbered in the order
/// they arrived so each device asks only for what is new to it.
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
            CREATE TABLE IF NOT EXISTS results (
                content_hash TEXT NOT NULL,
                kind TEXT NOT NULL,
                prompt_version INTEGER NOT NULL,
                id TEXT NOT NULL,
                finished_utc TEXT NOT NULL,
                content TEXT NOT NULL,
                device TEXT NOT NULL,
                seq INTEGER NOT NULL,
                PRIMARY KEY (content_hash, kind)
            );
            CREATE INDEX IF NOT EXISTS results_seq ON results (seq);
            CREATE TABLE IF NOT EXISTS shelf (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                version TEXT NOT NULL,
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

    /// <summary>How many results a device is given at once.</summary>
    public const int ResultPage = 200;

    /// <summary>
    /// Keeps the results <paramref name="device"/> sent, each in place of the one held for the same content and kind
    /// when it was made with a newer prompt, or the same prompt later. Returns how many were kept.
    /// </summary>
    public async Task<int> WriteResultsAsync(string device, IReadOnlyList<SharedResult> results, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        var kept = 0;
        foreach (var result in results)
        {
            await using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO results (content_hash, kind, prompt_version, id, finished_utc, content, device, seq)
                VALUES ($hash, $kind, $version, $id, $finished, $content, $device, (SELECT coalesce(max(seq), 0) + 1 FROM results))
                ON CONFLICT (content_hash, kind) DO UPDATE SET prompt_version = excluded.prompt_version, id = excluded.id,
                    finished_utc = excluded.finished_utc, content = excluded.content, device = excluded.device, seq = excluded.seq
                WHERE excluded.id <> results.id AND (excluded.prompt_version > results.prompt_version
                    OR (excluded.prompt_version = results.prompt_version AND excluded.finished_utc > results.finished_utc))
                """;
            command.Parameters.AddWithValue("$hash", result.ContentHash);
            command.Parameters.AddWithValue("$kind", result.Kind);
            command.Parameters.AddWithValue("$version", result.PromptVersion);
            command.Parameters.AddWithValue("$id", result.Id);
            command.Parameters.AddWithValue("$finished", Stamp(result.FinishedUtc));
            command.Parameters.AddWithValue("$content", result.Content);
            command.Parameters.AddWithValue("$device", device);
            kept += await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return kept;
    }

    /// <summary>The results after <paramref name="after"/> that devices other than <paramref name="device"/> sent, in the order they arrived.</summary>
    public async Task<SharedResultPage> ReadResultsAsync(long after, string device, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        // The device's own are skipped but counted, so it isn't asked about them again, and so are those asked for one
        // file at a time, without reading their content.
        command.CommandText = """
            SELECT content_hash, kind, prompt_version, id, finished_utc, CASE WHEN kind = $onRequest THEN '' ELSE content END, device, seq
            FROM results WHERE seq > $after ORDER BY seq LIMIT $take
            """;
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$onRequest", SharedResultKinds.PageText);
        command.Parameters.AddWithValue("$take", ResultPage + 1);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var results = new List<SharedResult>();
        var last = after;
        var read = 0;
        while (await reader.ReadAsync(ct))
        {
            if (++read > ResultPage) return new SharedResultPage(results, last, More: true);
            last = reader.GetInt64(7);
            if (reader.GetString(6) == device || SharedResultKinds.OnRequest(reader.GetString(1))) continue;
            results.Add(Result(reader));
        }
        return new SharedResultPage(results, last, More: false);
    }

    /// <summary>The result of <paramref name="kind"/> held for a file, whichever device sent it, or null.</summary>
    public async Task<SharedResult?> ReadResultAsync(string kind, string contentHash, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT content_hash, kind, prompt_version, id, finished_utc, content FROM results WHERE content_hash = $hash AND kind = $kind";
        command.Parameters.AddWithValue("$hash", contentHash);
        command.Parameters.AddWithValue("$kind", kind);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Result(reader) : null;
    }

    /// <summary>The version of the web library's shelf as last kept (<see cref="WebShelf"/>), or null before the first.</summary>
    public async Task<string?> ReadShelfVersionAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT version FROM shelf WHERE id = 1";
        return await command.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>The web library's shelf as last kept: its version and its JSON, or null before the first.</summary>
    public async Task<(string Version, byte[] Content)?> ReadShelfAsync(CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT version, content FROM shelf WHERE id = 1";
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), (byte[])reader.GetValue(1)) : null;
    }

    /// <summary>Keeps the shelf in place of the one before; one the same as it is left alone, so Litestream has nothing to send.</summary>
    public async Task WriteShelfAsync(string version, byte[] content, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO shelf (id, version, content) VALUES (1, $version, $content)
            ON CONFLICT (id) DO UPDATE SET version = excluded.version, content = excluded.content WHERE shelf.version <> excluded.version
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$content", content);
        await command.ExecuteNonQueryAsync(ct);
    }

    static SharedResult Result(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            DateTime.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
            reader.GetString(5));

    static string Stamp(DateTime when) =>
        DateTime.SpecifyKind(when, DateTimeKind.Utc).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

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

/// <summary>
/// The web library's side of sharing results: it is one more device, which sends what it worked out itself (with the
/// user's own AI key) to sync.db and takes its PCs' from there. A PC sending results nudges it (<see cref="SharedResults.Nudge"/>).
/// </summary>
public sealed class ServerResultChannel(SyncFiles files) : IResultChannel
{
    // Always: the web library's own results wait in sync.db for a PC that connects later.
    public bool IsOn => true;

    public async Task<SharedResultPage?> ReadResultsAsync(long after, CancellationToken ct) => await files.ReadResultsAsync(after, SyncFiles.WebDevice, ct);

    public async Task<bool> WriteResultsAsync(IReadOnlyList<SharedResult> results, CancellationToken ct)
    {
        await files.WriteResultsAsync(SyncFiles.WebDevice, results, ct);
        return true;
    }

    public async Task<SharedResult?> ReadResultAsync(string kind, string contentHash, CancellationToken ct) =>
        await files.ReadResultAsync(kind, contentHash, ct);
}
