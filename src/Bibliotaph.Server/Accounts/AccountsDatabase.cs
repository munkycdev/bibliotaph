using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Bibliotaph.Server.Accounts;

/// <summary>The one accounts.db, migrated with EF Core at startup like catalog.db.</summary>
public sealed class AccountsDatabase(string path)
{
    public string FilePath => path;

    public string ConnectionString { get; } =
        new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();

    public void Configure(DbContextOptionsBuilder builder) => builder.UseSqlite(ConnectionString);

    public AccountsDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<AccountsDbContext>();
        Configure(builder);
        return new AccountsDbContext(builder.Options);
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var context = CreateContext();
        await context.Database.MigrateAsync(ct);
        // Litestream replicates the write-ahead log, so it needs WAL mode.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL", ct);
    }

    public void ClearPool()
    {
        using var connection = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(connection);
    }
}

/// <summary>For dotnet ef migrations add, run from the repo root.</summary>
sealed class AccountsDesignTimeFactory : IDesignTimeDbContextFactory<AccountsDbContext>
{
    public AccountsDbContext CreateDbContext(string[] args) => new AccountsDatabase("design-time-accounts.db").CreateContext();
}
