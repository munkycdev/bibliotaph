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
