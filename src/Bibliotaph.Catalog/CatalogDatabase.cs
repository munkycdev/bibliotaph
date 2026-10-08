using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Catalog;

public sealed record CatalogMigrationResult(IReadOnlyList<string> AppliedMigrations, string? BackupPath);

/// <summary>
/// catalog.db: the user's work. Migrated with EF Core; before any migration touches an existing file,
/// the file is copied with VACUUM INTO to a timestamped backup.
/// </summary>
public sealed class CatalogDatabase(string path, string backupsDirectory, TimeProvider? clock = null, ILogger<CatalogDatabase>? log = null)
{
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<CatalogDatabase>.Instance;

    public string FilePath => path;

    public string ConnectionString { get; } =
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();

    public DbContextOptions<CatalogDbContext> Options
    {
        get
        {
            var builder = new DbContextOptionsBuilder<CatalogDbContext>();
            Configure(builder);
            return builder.Options;
        }
    }

    /// <summary>Points EF Core at this file; also used by the app's AddDbContextFactory registration.</summary>
    public void Configure(DbContextOptionsBuilder builder) => builder.UseSqlite(ConnectionString);

    public CatalogDbContext CreateContext() => new(Options);

    public async Task<CatalogMigrationResult> MigrateAsync(CancellationToken ct = default)
    {
        var existed = File.Exists(path);
        await using var context = CreateContext();
        var pending = (await context.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count == 0) return new CatalogMigrationResult([], null);

        string? backup = null;
        if (existed)
        {
            backup = await BackupAsync($"before-{pending[0]}", ct);
            _log.LogInformation("Backed up catalog.db to {Backup} before applying {Count} migration(s)", backup, pending.Count);
        }

        await context.Database.MigrateAsync(ct);
        await ExecuteAsync("PRAGMA journal_mode = WAL", ct);
        _log.LogInformation("Applied catalog migrations: {Migrations}", string.Join(", ", pending));
        return new CatalogMigrationResult(pending, backup);
    }

    /// <summary>Writes a consistent copy of catalog.db into the backups folder and returns its path.</summary>
    public async Task<string> BackupAsync(string reason, CancellationToken ct = default)
    {
        Directory.CreateDirectory(backupsDirectory);
        var stamp = _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(backupsDirectory, $"catalog-{stamp}-{reason}.db");
        for (var n = 2; File.Exists(target); n++)
            target = Path.Combine(backupsDirectory, $"catalog-{stamp}-{reason}-{n}.db");

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $target";
        command.Parameters.AddWithValue("$target", target);
        await command.ExecuteNonQueryAsync(ct);
        return target;
    }

    async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
