using Bibliotaph.Storage;

namespace Bibliotaph.Server.Storage;

/// <summary>The "Storage:&lt;provider&gt;" configuration section: the OAuth app a provider is connected through.</summary>
public sealed class StorageApp
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>Whose a newly connected account is at its provider, as connecting it found.</summary>
public sealed record StorageIdentity(string AccountId, string DisplayName, string? AccountKind);

/// <summary>
/// One kind of storage a user can connect: its OAuth endpoints and the access asked for, who an account is, and its
/// client behind <see cref="IStorageDrive"/>. Everything else, from tokens to the folder picker, is shared.
/// </summary>
public abstract class StorageProvider
{
    /// <summary>The ID in library paths, routes and accounts.db: "onedrive".</summary>
    public abstract string Id { get; }

    /// <summary>"OneDrive".</summary>
    public abstract string Name { get; }

    /// <summary>Who asks the user for access, in "allow Bibliotaph to read your files when Microsoft asks".</summary>
    public abstract string Owner { get; }

    /// <summary>The configuration section's last part: "OneDrive" for "Storage:OneDrive".</summary>
    protected abstract string ConfigurationName { get; }

    public abstract string AuthorizationEndpoint { get; }
    public abstract string TokenEndpoint { get; }
    public abstract IReadOnlyList<string> Scopes { get; }

    /// <summary>Asked for besides the scopes, as some providers hand out a refresh token only when asked.</summary>
    public virtual IReadOnlyDictionary<string, string> AuthorizationParameters { get; } = new Dictionary<string, string>();

    /// <summary>Whether redeeming a refresh token names the scopes again, as Microsoft's endpoint wants.</summary>
    public virtual bool ScopesOnRefresh => false;

    public string Section => $"Storage:{ConfigurationName}";

    /// <summary>The OAuth app, or null until it's configured, which leaves the provider out of Settings.</summary>
    public StorageApp? App(IConfiguration configuration) =>
        configuration.GetSection(Section).Get<StorageApp>() is { ClientId.Length: > 0, ClientSecret.Length: > 0 } app ? app : null;

    /// <summary>The account an access token from connecting belongs to.</summary>
    public abstract Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct);

    public abstract IStorageDrive Drive(HttpClient http, ConnectedAccount account, Func<CancellationToken, Task<string>> accessToken, TimeProvider clock);
}

/// <summary>Every storage provider the server knows, whether or not its OAuth app is configured here.</summary>
public static class StorageProviders
{
    public static readonly IReadOnlyList<StorageProvider> All = [new OneDriveProvider(), new DropboxProvider(), new GoogleDriveProvider()];

    public static StorageProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static IEnumerable<StorageProvider> Configured(IConfiguration configuration) => All.Where(p => p.App(configuration) is not null);
}
