using System.Text;
using Bibliotaph.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<SourceRoot> SourceRoots => Set<SourceRoot>();
    public DbSet<FileLocation> FileLocations => Set<FileLocation>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Assertion> Assertions => Set<Assertion>();
    public DbSet<PageRef> PageRefs => Set<PageRef>();
    public DbSet<Setting> Settings => Set<Setting>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored by name so the database reads sensibly in any SQLite tool.
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceRoot>(e =>
        {
            e.HasIndex(r => r.Path).IsUnique();
            e.HasMany(r => r.Files).WithOne(f => f.SourceRoot).HasForeignKey(f => f.SourceRootId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FileLocation>(e =>
        {
            e.HasIndex(f => new { f.SourceRootId, f.RelativePath }).IsUnique();
            e.HasIndex(f => f.ContentHash);
            e.HasOne(f => f.Document).WithMany(d => d.Locations).HasForeignKey(f => f.DocumentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Document>(e =>
        {
            e.HasIndex(d => d.ContentHash).IsUnique();
            e.HasOne(d => d.PreviousVersion).WithMany().HasForeignKey(d => d.PreviousVersionId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Assertion>(e =>
        {
            e.HasIndex(a => new { a.DocumentId, a.Field, a.State });
            e.HasOne(a => a.Document).WithMany(d => d.Assertions).HasForeignKey(a => a.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PageRef>(e =>
        {
            e.HasIndex(p => p.DocumentId);
            // User work points at page refs, so a document is never deleted out from under them.
            e.HasOne(p => p.Document).WithMany(d => d.PageRefs).HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Setting>(e => e.HasKey(s => s.Key));

        UseSnakeCaseNames(modelBuilder);
    }

    static void UseSnakeCaseNames(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            entity.SetTableName(SnakeCase(entity.ClrType.Name));
            foreach (var property in entity.GetProperties()) property.SetColumnName(SnakeCase(property.Name));
        }
    }

    internal static string SnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
