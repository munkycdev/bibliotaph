using Bibliotaph.Core;

namespace Bibliotaph.Server;

/// <summary>The "Server" configuration section. In the container these come from environment variables (Server__DataRoot).</summary>
public sealed class ServerOptions
{
    public const string Section = "Server";

    /// <summary>
    /// Where accounts.db and every user's library live. In the container this is the replica's own disk, which is lost
    /// on a restart; Litestream keeps the durable copy in Blob storage.
    /// </summary>
    public string DataRoot { get; set; } = "/data";

    /// <summary>
    /// The Litestream replica for <see cref="DataRoot"/>, such as abs://account@container. Unset (local development and
    /// tests), nothing is restored and nothing is replicated.
    /// </summary>
    public string? ReplicaUrl { get; set; }

    public string LitestreamPath { get; set; } = "litestream";

    /// <summary>A library nobody has used for this long is closed, so its connections and writer don't sit idle.</summary>
    public TimeSpan LibraryIdle { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>The origin invitation links point at, such as https://bibliotaph.online. Unset, the request's own.</summary>
    public string? PublicOrigin { get; set; }

    /// <summary>
    /// The Data Protection key ring's blob (https://account.blob.core.windows.net/container/keys.xml) and the Key Vault
    /// key that wraps it. Both set in Azure, so sign-in cookies and stored tokens survive restarts and deploys; unset,
    /// the keys stay in <see cref="DataRoot"/>/keys.
    /// </summary>
    public string? KeyRingBlob { get; set; }

    public string? KeyRingKey { get; set; }

    /// <summary>
    /// The Blob container that keeps every user's covers (https://account.blob.core.windows.net/covers), which the
    /// replica's disk loses on a restart. Unset, covers live only in <see cref="DataRoot"/>.
    /// </summary>
    public string? CoverContainer { get; set; }

    /// <summary>
    /// The Blob container that keeps every user's weekly backups and the backups made before a restore
    /// (https://account.blob.core.windows.net/backups). Unset with <see cref="CoverContainer"/> set, it is the container
    /// named backups in the covers' account, made when first needed; with neither, backups live in <see cref="DataRoot"/>.
    /// </summary>
    public string? BackupContainer { get; set; }

    /// <summary>When the first look for weekly backups that are due comes after the server starts, and how often after.</summary>
    public TimeSpan WeeklyBackupFirst { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan WeeklyBackupEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The user-assigned managed identity that reaches Blob storage and Key Vault.</summary>
    public string? ManagedIdentityClientId { get; set; }
}

/// <summary>Where the server keeps its own files under <see cref="ServerOptions.DataRoot"/>.</summary>
public sealed record ServerPaths(string Root)
{
    public string Accounts => Path.Combine(Root, "accounts.db");

    /// <summary>One folder per user, named by user ID. Litestream watches this whole tree for *.db files.</summary>
    public string Users => Path.Combine(Root, "users");

    public string UserRoot(string userId) => Path.Combine(Users, UserIds.Require(userId));

    /// <summary>
    /// Copies of catalog.db taken before a migration. Outside <see cref="Users"/> on purpose: they're *.db files, and
    /// Litestream would otherwise replicate every one of them.
    /// </summary>
    public string UserBackups(string userId) => Path.Combine(Root, "backups", UserIds.Require(userId));

    /// <summary>Restores land here first and are moved into place when complete, so no half-written file is ever opened.</summary>
    public string Staging => Path.Combine(Root, "staging");

    public string Keys => Path.Combine(Root, "keys");

    /// <summary>
    /// Books being read, each downloaded from its drive and deleted when it has been read. Emptied at every start, so
    /// a crash mid-read leaves nothing behind.
    /// </summary>
    public string Downloads => Path.Combine(Root, "downloads");

    /// <summary>
    /// Files from inside each user's ZIPs while they are read, one folder per user. Outside <see cref="Users"/>, with
    /// <see cref="Downloads"/>, as the only folders the PDF worker may read books from.
    /// </summary>
    public string Extracts => Path.Combine(Root, "extract");

    /// <summary>A user's library files: their folder, with ZIPs extracted to their own folder under <see cref="Extracts"/>.</summary>
    public AppPaths Library(string userId) => new(UserRoot(userId)) { Extract = Path.Combine(Extracts, UserIds.Require(userId)) };
}

/// <summary>
/// User IDs are ours, never a sign-in provider's: 32 lowercase hex digits. They name folders, so anything else is refused
/// before it can reach a path.
/// </summary>
public static class UserIds
{
    public static string New() => Guid.NewGuid().ToString("N");

    public static bool IsValid(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Require(string? id) => IsValid(id) ? id! : throw new ArgumentException("Not a user ID.", nameof(id));
}
