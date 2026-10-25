using Microsoft.EntityFrameworkCore;

namespace Bibliotaph.Server.Accounts;

/// <summary>
/// accounts.db: who may sign in and what they've connected, server-wide. Each user's library is in their own folder;
/// nothing about books is kept here.
/// </summary>
public sealed class AccountsDbContext(DbContextOptions<AccountsDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Login> Logins => Set<Login>();
    public DbSet<Invite> Invites => Set<Invite>();
    public DbSet<StorageConnection> StorageConnections => Set<StorageConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasMaxLength(32);
        });
        modelBuilder.Entity<Login>(e =>
        {
            e.ToTable("logins");
            e.HasKey(l => new { l.Provider, l.Subject });
            e.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => l.UserId);
        });
        modelBuilder.Entity<Invite>(e =>
        {
            e.ToTable("invites");
            e.HasKey(i => i.Id);
            e.HasIndex(i => i.CodeHash).IsUnique();
        });
        modelBuilder.Entity<StorageConnection>(e =>
        {
            e.ToTable("storage_connections");
            e.HasKey(c => c.Id);
            e.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(c => new { c.UserId, c.Provider, c.AccountId }).IsUnique();
        });

        // Column names in snake_case, as catalog.db's.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties()) property.SetColumnName(SnakeCase(property.Name));
    }

    static string SnakeCase(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}

public sealed class User
{
    public required string Id { get; set; }
    public required string Name { get; set; }

    /// <summary>For display only. Never used to decide who someone is: a provider's email claim isn't always verified.</summary>
    public string? Email { get; set; }

    public bool IsAdmin { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastSignIn { get; set; }
}

/// <summary>One way into an account. A user can link several: a Microsoft and a Google sign-in, say.</summary>
public sealed class Login
{
    /// <summary>The provider's key: auth0, or developer on a developer's machine.</summary>
    public required string Provider { get; set; }

    /// <summary>The provider's stable ID for the person: Auth0's sub, which names the connection too ("google-oauth2|1234").</summary>
    public required string Subject { get; set; }

    public required string UserId { get; set; }
    public string? Email { get; set; }
    public DateTime Added { get; set; }
}

/// <summary>A one-time invitation link. Only the code's SHA-256 is kept, so this table can't be used to sign up.</summary>
public sealed class Invite
{
    public required string Id { get; set; }
    public required string CodeHash { get; set; }
    public string? Note { get; set; }
    public bool Admin { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime Created { get; set; }
    public DateTime Expires { get; set; }
    public DateTime? Redeemed { get; set; }
    public string? RedeemedBy { get; set; }
}

/// <summary>A storage service the user connected, such as their OneDrive. Separate from sign-in on purpose.</summary>
public sealed class StorageConnection
{
    public required string Id { get; set; }
    public required string UserId { get; set; }

    /// <summary>The storage provider's key, such as onedrive.</summary>
    public required string Provider { get; set; }

    /// <summary>The provider's ID for the account or drive, so the same drive isn't connected twice.</summary>
    public required string AccountId { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>The refresh token, encrypted with ASP.NET Data Protection; the key ring is wrapped by Key Vault in Azure.</summary>
    public required string ProtectedRefreshToken { get; set; }

    public string? Scopes { get; set; }

    /// <summary>The provider's kind of account, such as personal or business for OneDrive, which changes some calls.</summary>
    public string? AccountKind { get; set; }

    /// <summary>The provider refused the refresh token (revoked, expired, password changed): the user must connect again.</summary>
    public bool NeedsReconnect { get; set; }

    /// <summary>
    /// Sent back with every change notification for this connection, so a notification can't be forged by someone who
    /// only knows the address it is posted to.
    /// </summary>
    public required string NotificationSecret { get; set; }

    public string? SubscriptionId { get; set; }
    public DateTime? SubscriptionExpires { get; set; }

    public DateTime Created { get; set; }
    public DateTime Updated { get; set; }
}
