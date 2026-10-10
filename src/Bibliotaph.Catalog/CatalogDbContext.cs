using System.Text;
using Bibliotaph.Catalog.Entities;
using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Catalog;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<SourceRoot> SourceRoots => Set<SourceRoot>();
    public DbSet<FileLocation> FileLocations => Set<FileLocation>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Entry> Entries => Set<Entry>();
    public DbSet<EntrySource> EntrySources => Set<EntrySource>();
    public DbSet<EntryJoin> EntryJoins => Set<EntryJoin>();
    public DbSet<CopyDecision> CopyDecisions => Set<CopyDecision>();
    public DbSet<VersionProposal> VersionProposals => Set<VersionProposal>();
    public DbSet<PackDecision> PackDecisions => Set<PackDecision>();
    public DbSet<ElsewhereMatch> ElsewhereMatches => Set<ElsewhereMatch>();
    public DbSet<Assertion> Assertions => Set<Assertion>();
    public DbSet<PageRef> PageRefs => Set<PageRef>();
    public DbSet<Favorite> Favorites => Set<Favorite>();
    public DbSet<ReadingState> ReadingStates => Set<ReadingState>();
    public DbSet<CollectionNode> Collections => Set<CollectionNode>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();
    public DbSet<SmartView> SmartViews => Set<SmartView>();
    public DbSet<SessionPack> SessionPacks => Set<SessionPack>();
    public DbSet<SessionSection> SessionSections => Set<SessionSection>();
    public DbSet<SessionItem> SessionItems => Set<SessionItem>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Rejection> Rejections => Set<Rejection>();
    public DbSet<VocabularyTerm> VocabularyTerms => Set<VocabularyTerm>();
    public DbSet<VocabularyAlias> VocabularyAliases => Set<VocabularyAlias>();
    public DbSet<ClassificationRun> ClassificationRuns => Set<ClassificationRun>();
    public DbSet<IgnoredFolderLabel> IgnoredFolderLabels => Set<IgnoredFolderLabel>();

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
            e.HasOne<Document>().WithMany().HasForeignKey(f => f.PreviousDocumentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(f => f.Container).WithMany().HasForeignKey(f => f.ContainerId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Document>(e => e.HasIndex(d => d.ContentHash).IsUnique());

        modelBuilder.Entity<Entry>(e =>
        {
            e.HasIndex(x => x.ParentEntryId);
            e.HasOne(x => x.ParentEntry).WithMany().HasForeignKey(x => x.ParentEntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Entry>().WithMany().HasForeignKey(x => x.MergedIntoEntryId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<EntryJoin>(e =>
        {
            e.HasIndex(j => j.DocumentId);
            e.HasIndex(j => j.EntryId);
            e.HasOne<Entry>().WithMany().HasForeignKey(j => j.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Entry>().WithMany().HasForeignKey(j => j.JoinedEntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Document>().WithMany().HasForeignKey(j => j.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Document>().WithMany().HasForeignKey(j => j.MatchedDocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CopyDecision>(e => e.HasIndex(d => new { d.FirstHash, d.SecondHash }).IsUnique());

        modelBuilder.Entity<VersionProposal>(e =>
        {
            e.HasIndex(p => p.DocumentId).IsUnique();
            e.HasIndex(p => p.MatchedDocumentId);
            e.HasOne<Document>().WithMany().HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Document>().WithMany().HasForeignKey(p => p.MatchedDocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ElsewhereMatch>(e =>
        {
            e.HasIndex(m => new { m.EntryId, m.DocumentId }).IsUnique();
            e.HasIndex(m => m.DocumentId);
            e.HasOne<Entry>().WithMany().HasForeignKey(m => m.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Document>().WithMany().HasForeignKey(m => m.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PackDecision>(e =>
        {
            e.HasIndex(p => new { p.SourceRootId, p.FolderPath }).IsUnique();
            e.HasIndex(p => p.EntryId).IsUnique();
            e.HasOne<SourceRoot>().WithMany().HasForeignKey(p => p.SourceRootId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Entry>().WithMany().HasForeignKey(p => p.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EntrySource>(e =>
        {
            e.HasIndex(s => s.EntryId).IsUnique().HasFilter("is_current = 1").HasDatabaseName("ix_entry_source_current");
            // A document backs at most one whole-document entry; parts take page ranges of it as well.
            e.HasIndex(s => s.DocumentId).IsUnique().HasFilter("first_pdf_page IS NULL").HasDatabaseName("ix_entry_source_whole");
            e.HasIndex(s => new { s.DocumentId, s.EntryId });
            e.HasOne(s => s.Entry).WithMany(x => x.Sources).HasForeignKey(s => s.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Document).WithMany(d => d.Sources).HasForeignKey(s => s.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Assertion>(e =>
        {
            e.HasIndex(a => new { a.EntryId, a.Field, a.State });
            e.HasOne(a => a.Entry).WithMany(x => x.Assertions).HasForeignKey(a => a.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Rejection>(e =>
        {
            e.HasIndex(r => new { r.EntryId, r.Field, r.NormalizedValue }).IsUnique();
            e.HasOne(r => r.Entry).WithMany(x => x.Rejections).HasForeignKey(r => r.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabularyTerm>(e =>
        {
            e.HasIndex(t => new { t.Vocabulary, t.Key }).IsUnique();
            e.HasMany(t => t.Aliases).WithOne(a => a.Term).HasForeignKey(a => a.TermId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabularyAlias>(e => e.HasIndex(a => new { a.TermId, a.Normalized }).IsUnique());

        modelBuilder.Entity<ClassificationRun>(e =>
        {
            e.HasIndex(r => new { r.ContentHash, r.Model, r.PromptVersion });
            e.HasOne(r => r.Entry).WithMany().HasForeignKey(r => r.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IgnoredFolderLabel>(e => e.HasIndex(l => new { l.Folder, l.Vocabulary, l.TermKey }).IsUnique());

        modelBuilder.Entity<PageRef>(e =>
        {
            e.HasIndex(p => p.DocumentId);
            // User work points at page refs, so a document is never deleted out from under them.
            e.HasOne(p => p.Document).WithMany(d => d.PageRefs).HasForeignKey(p => p.DocumentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Favorite>(e =>
        {
            e.HasKey(f => f.EntryId);
            e.Property(f => f.EntryId).ValueGeneratedNever();
            e.HasOne<Entry>().WithOne().HasForeignKey<Favorite>(f => f.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReadingState>(e =>
        {
            e.HasKey(r => r.EntryId);
            e.Property(r => r.EntryId).ValueGeneratedNever();
            e.HasIndex(r => r.OpenedUtc);
            e.HasIndex(r => r.DocumentId);
            e.HasOne<Entry>().WithOne().HasForeignKey<ReadingState>(r => r.EntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Document>().WithMany().HasForeignKey(r => r.DocumentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CollectionNode>(e =>
        {
            e.HasIndex(c => c.ParentId);
            // Deleting a collection moves its sub-collections up a level first (choice 8); this only guards the rest.
            e.HasOne<CollectionNode>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CollectionItem>(e =>
        {
            e.HasKey(i => new { i.CollectionId, i.EntryId });
            e.HasIndex(i => i.EntryId);
            e.HasOne<CollectionNode>().WithMany().HasForeignKey(i => i.CollectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Entry>().WithMany().HasForeignKey(i => i.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SmartView>(e => e.HasIndex(v => v.Name));

        modelBuilder.Entity<SessionPack>(e => e.HasIndex(p => p.TouchedUtc));

        modelBuilder.Entity<SessionSection>(e =>
        {
            e.HasIndex(s => s.PackId);
            e.HasOne<SessionPack>().WithMany().HasForeignKey(s => s.PackId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SessionItem>(e =>
        {
            e.HasIndex(i => i.PackId);
            e.HasIndex(i => i.EntryId);
            e.HasOne<SessionPack>().WithMany().HasForeignKey(i => i.PackId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a section moves its items to the one above first (choice 11); this only guards the rest.
            e.HasOne<SessionSection>().WithMany().HasForeignKey(i => i.SectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Entry>().WithMany().HasForeignKey(i => i.EntryId).OnDelete(DeleteBehavior.Cascade);
            // An item's page reference is its own: the store deletes it with the item.
            e.HasOne(i => i.PageRef).WithMany().HasForeignKey(i => i.PageRefId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Setting>(e => e.HasKey(s => s.Key));

        UseSnakeCaseNames(modelBuilder);
        // The class can't be called Collection (CA1711), but the table can.
        modelBuilder.Entity<CollectionNode>().ToTable("collection");
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
