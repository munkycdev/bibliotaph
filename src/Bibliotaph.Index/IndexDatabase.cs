using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Index;

public static class IndexSchema
{
    /// <summary>Must match the <c>PRAGMA user_version</c> at the end of Schema/index.sql.</summary>
    public const int Version = 8;

    public static string Script { get; } = LoadScript("index") ?? throw new InvalidOperationException("index.sql is not embedded.");

    /// <summary>The script that brings a version <paramref name="version"/> - 1 file up to <paramref name="version"/>, if there is one.</summary>
    public static string? Upgrade(int version) => LoadScript($"upgrade-{version}");

    static string? LoadScript(string name)
    {
        using var stream = typeof(IndexSchema).Assembly.GetManifestResourceStream($"Bibliotaph.Index.Schema.{name}.sql");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The upgrade scripts from <paramref name="from"/> to the current version, or null when any step has none.</summary>
    public static IReadOnlyList<string>? UpgradePath(int from)
    {
        if (from is < 1 or >= Version) return null;
        var scripts = new List<string>();
        for (var v = from + 1; v <= Version; v++)
        {
            if (Upgrade(v) is not { } script) return null;
            scripts.Add(script);
        }
        return scripts;
    }
}

public enum IndexOpenResult
{
    Created,
    Opened,
    /// <summary>The file had another schema version, or could not be read, and was deleted and recreated.</summary>
    Rebuilt,

    /// <summary>The file had an older schema version with an upgrade path, and was brought up to date in place.</summary>
    Upgraded,
}

/// <summary>
/// index.db: pages, FTS5 tables, metadata projections, stage status and the job queue. Derived data only, so instead
/// of migrations a schema mismatch deletes the file and starts again, unless every step from the file's version has an
/// additive upgrade script (Schema/upgrade-N.sql). Reads use read-only connections; all writes go through
/// <see cref="IndexWriter"/>.
/// </summary>
public sealed class IndexDatabase
{
    readonly ILogger _log;

    IndexDatabase(string? filePath, string connectionString, ILogger? log)
    {
        FilePath = filePath;
        ConnectionString = connectionString;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>Null for an in-memory database.</summary>
    public string? FilePath { get; }

    public string ConnectionString { get; }

    public static IndexDatabase ForFile(string path, ILogger<IndexDatabase>? log = null) =>
        new(path, new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString(), log);

    /// <summary>A named, shared-cache in-memory database that lives while at least one connection is open.</summary>
    public static IndexDatabase InMemory(string name) =>
        new(null, new SqliteConnectionStringBuilder { DataSource = name, Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared }.ToString(), null);

    public async Task<IndexOpenResult> InitializeAsync(CancellationToken ct = default)
    {
        int? version;
        try
        {
            version = await ReadVersionAsync(ct);
        }
        catch (SqliteException ex) when (FilePath is not null)
        {
            _log.LogWarning(ex, "index.db could not be read; rebuilding it");
            version = -1;
        }

        if (version == IndexSchema.Version) return IndexOpenResult.Opened;
        if (version is null)
        {
            await CreateAsync(ct);
            return IndexOpenResult.Created;
        }

        if (IndexSchema.UpgradePath(version.Value) is { } upgrades)
        {
            try
            {
                await RunScriptsAsync(upgrades, ct);
                _log.LogInformation("Upgraded index.db from schema version {Found} to {Expected}", version, IndexSchema.Version);
                return IndexOpenResult.Upgraded;
            }
            catch (SqliteException ex)
            {
                _log.LogWarning(ex, "index.db could not be upgraded from schema version {Found}; rebuilding it", version);
            }
        }
        else _log.LogInformation("index.db schema version is {Found}, expected {Expected}; rebuilding it", version, IndexSchema.Version);
        DeleteFiles();
        await CreateAsync(ct);
        return IndexOpenResult.Rebuilt;
    }

    /// <summary>The schema version, or null for an empty database with nothing in it yet.</summary>
    async Task<int?> ReadVersionAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM sqlite_schema), (SELECT user_version FROM pragma_user_version)";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var objects = reader.GetInt64(0);
        var version = reader.GetInt32(1);
        return objects == 0 && version == 0 ? null : version;
    }

    async Task CreateAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        if (FilePath is not null)
        {
            await using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode = WAL";
            await wal.ExecuteNonQueryAsync(ct);
        }
        await ExecuteInTransactionAsync(connection, [IndexSchema.Script], ct);
    }

    async Task RunScriptsAsync(IReadOnlyList<string> scripts, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await ExecuteInTransactionAsync(connection, scripts, ct);
    }

    /// <summary>All or nothing: a failed upgrade leaves the file at its old version, to be rebuilt instead.</summary>
    static async Task ExecuteInTransactionAsync(SqliteConnection connection, IReadOnlyList<string> scripts, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var script in scripts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = script;
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    void DeleteFiles()
    {
        if (FilePath is null) return;
        // Pooled connections keep the file open, and Windows will not delete an open file.
        SqliteConnection.ClearAllPools();
#pragma warning disable RS0030 // index.db is Bibliotaph's own derived file in %LOCALAPPDATA%, never a source file.
        foreach (var path in new[] { FilePath, FilePath + "-wal", FilePath + "-shm" })
            File.Delete(path);
#pragma warning restore RS0030
    }

    /// <summary>A read-only connection for queries. Callers dispose it.</summary>
    public SqliteConnection OpenRead()
    {
        var builder = new SqliteConnectionStringBuilder(ConnectionString);
        if (FilePath is not null) builder.Mode = SqliteOpenMode.ReadOnly;
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        if (FilePath is null)
        {
            // Shared-cache in-memory databases cannot be opened read-only; enforce it per connection instead.
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = ON";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    internal SqliteConnection OpenWrite()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        // Derived data: losing the last few transactions to a power cut costs a re-index, not user work.
        command.CommandText = "PRAGMA synchronous = NORMAL";
        command.ExecuteNonQuery();
        return connection;
    }
}
