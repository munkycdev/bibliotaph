using Bibliotaph.Storage;
using Bibliotaph.Storage.Dropbox;
using Bibliotaph.Storage.GoogleDrive;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Storage;

/// <summary>The "Storage:&lt;provider&gt;" configuration section: the OAuth app a provider is connected through.</summary>
public sealed class StorageApp
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}

/// <summary>
/// One kind of storage a user can connect on this server: the <see cref="StorageKind"/> both apps share, and what this
/// server's configuration says about it.
/// </summary>
public abstract class StorageProvider(StorageKind kind)
{
    public StorageKind Kind => kind;

    /// <summary>The ID in library paths, routes and accounts.db: "onedrive".</summary>
    public string Id => kind.Id;

    public string Name => kind.Name;

    public string Owner => kind.Owner;

    public string? About => kind.About;

    public bool StartsInItsOwnApp => kind.StartsInItsOwnApp;

    /// <summary>Whether this server can connect it, which for an OAuth provider needs its app.</summary>
    public abstract bool IsAvailable(IConfiguration configuration);

    public IReadOnlyList<string> Access(string? scopes) => kind.Access(scopes);

    public IStorageDrive Drive(DriveContext context) => kind.Drive(context);

    /// <summary>
    /// The provider with whatever this server's configuration says about where it is. Most providers are at fixed
    /// addresses and are themselves; Nornis's sign-in and API are wherever the configuration puts them.
    /// </summary>
    public virtual StorageProvider Bind(IConfiguration configuration) => this;
}

/// <summary>Storage connected through OAuth, with this server's app for it, which has a secret.</summary>
public class OAuthStorageProvider(OAuthStorageKind kind, string configurationName) : StorageProvider(kind)
{
    public new OAuthStorageKind Kind => (OAuthStorageKind)base.Kind;

    public string AuthorizationEndpoint => Kind.AuthorizationEndpoint;
    public string TokenEndpoint => Kind.TokenEndpoint;
    public IReadOnlyList<string> Scopes => Kind.Scopes;
    public IReadOnlyDictionary<string, string> AuthorizationParameters => Kind.AuthorizationParameters;
    public bool ScopesOnRefresh => Kind.ScopesOnRefresh;

    public string Section => $"Storage:{configurationName}";

    public override bool IsAvailable(IConfiguration configuration) => App(configuration) is not null;

    /// <summary>The OAuth app, or null until it's configured, which leaves the provider out of Settings.</summary>
    public virtual StorageApp? App(IConfiguration configuration) =>
        configuration.GetSection(Section).Get<StorageApp>() is { ClientId.Length: > 0, ClientSecret.Length: > 0 } app ? app : null;

    /// <summary>The account an access token from connecting belongs to.</summary>
    public Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct) => Kind.IdentifyAsync(http, accessToken, ct);
}

public sealed class OneDriveProvider() : OAuthStorageProvider(new OneDriveKind(), "OneDrive")
{
    public const string Key = OneDriveKind.Key;
}

public sealed class DropboxProvider() : OAuthStorageProvider(new DropboxKind(), "Dropbox")
{
    public const string Key = DropboxKind.Key;
}

public sealed class GoogleDriveProvider() : OAuthStorageProvider(new GoogleDriveKind(), "GoogleDrive")
{
    public const string Key = GoogleDriveKind.Key;
}

/// <summary>Every storage provider the server knows, whether or not it can be connected here.</summary>
public static class StorageProviders
{
    public static readonly IReadOnlyList<StorageProvider> All =
        [new OneDriveProvider(), new DropboxProvider(), new GoogleDriveProvider(), new WebDavProvider(), new NornisProvider()];

    public static StorageProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>A provider bound to this server's configuration, for anything that calls it.</summary>
    public static StorageProvider? Find(string id, IConfiguration configuration) => Find(id)?.Bind(configuration);

    public static IEnumerable<StorageProvider> Configured(IConfiguration configuration) =>
        All.Where(p => p.IsAvailable(configuration)).Select(p => p.Bind(configuration));
}
