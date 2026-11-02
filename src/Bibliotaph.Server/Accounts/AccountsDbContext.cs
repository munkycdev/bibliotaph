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
    public DbSet<ClosedAccount> ClosedAccounts => Set<ClosedAccount>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<NornisWorldEvent> NornisWorldEvents => Set<NornisWorldEvent>();

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

        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("devices");
            e.HasKey(d => d.Id);
            e.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(d => d.KeyHash).IsUnique();
            e.HasIndex(d => d.UserId);
        });

        modelBuilder.Entity<NornisWorldEvent>(e =>
        {
            e.ToTable("nornis_world_events");
            e.HasKey(w => new { w.UserId, w.EventId });
            e.HasOne<User>().WithMany().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(w => new { w.UserId, w.AppliedUtc });
        });

        modelBuilder.Entity<ClosedAccount>(e =>
        {
            e.ToTable("closed_accounts");
            e.HasKey(c => c.UserId);
            e.Property(c => c.UserId).HasMaxLength(32);
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

/// <summary>
/// An account that was closed: only its ID and when, so its files are deleted until none is left, even across a
/// restart or a storage failure, and never the files of an account that is still open.
/// </summary>
public sealed class ClosedAccount
{
    public required string UserId { get; set; }
    public DateTime Closed { get; set; }

    /// <summary>When nothing of it was left anywhere.</summary>
    public DateTime? Purged { get; set; }
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

/// <summary>
/// A one-time invitation link. The code's SHA-256 is what redeems it; the code itself is kept while the invitation is
/// open, so an admin can copy the link again until it's used.
/// </summary>
public sealed class Invite
{
    public required string Id { get; set; }
    public required string CodeHash { get; set; }

    /// <summary>The code while the invitation is open; null once it's used, and for invitations made before codes were kept.</summary>
    public string? Code { get; set; }
    public string? Note { get; set; }
    public bool Admin { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime Created { get; set; }
    public DateTime Expires { get; set; }
    public DateTime? Redeemed { get; set; }
    public string? RedeemedBy { get; set; }
}

/// <summary>
/// A Windows PC connected to the user's library: it calls the API with its own key, of which only the SHA-256 is kept.
/// Disconnecting it deletes the row, and the key stops working at once.
/// </summary>
public sealed class Device
{
    public required string Id { get; set; }
    public required string UserId { get; set; }

    /// <summary>The PC's name, as it gave it when it asked to connect.</summary>
    public required string Name { get; set; }

    public required string KeyHash { get; set; }
    public DateTime Created { get; set; }
    public DateTime? LastUsed { get; set; }
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

    /// <summary>When the connected app last called in with it (Nornis), written at most every five minutes.</summary>
    public DateTime? LastUsed { get; set; }
}

/// <summary>
/// A world event Nornis delivered, kept until the user's library applies it: Nornis counts it delivered once it's here,
/// whether or not the library is open. Kept a while after it's applied, so Nornis sending it again changes nothing.
/// </summary>
public sealed class NornisWorldEvent
{
    public required string UserId { get; set; }

    /// <summary>Nornis's ID for the event, a GUID in its "D" form.</summary>
    public required string EventId { get; set; }

    /// <summary>created, renamed, deleted or files-changed.</summary>
    public required string Kind { get; set; }

    public required string WorldId { get; set; }
    public required string WorldName { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DateTime ReceivedUtc { get; set; }
    public DateTime? AppliedUtc { get; set; }
}
