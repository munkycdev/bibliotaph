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
/// What a provider's client is made with: the HTTP client its calls go through, the connected account, its credential
/// (an access token, or what the user typed for a provider without OAuth) read when a request is made, and what to do
/// when the provider refuses that credential.
/// </summary>
public sealed record DriveContext(HttpClient Http, ConnectedAccount Account, Func<CancellationToken, Task<string>> Credential,
    Func<CancellationToken, Task> Refused, TimeProvider Clock);

/// <summary>One kind of storage a user can connect, and its client behind <see cref="IStorageDrive"/>.</summary>
public abstract class StorageProvider
{
    /// <summary>The ID in library paths, routes and accounts.db: "onedrive".</summary>
    public abstract string Id { get; }

    /// <summary>"OneDrive".</summary>
    public abstract string Name { get; }

    /// <summary>Who asks the user for access, in "allow Bibliotaph to read your files when Microsoft asks".</summary>
    public abstract string Owner { get; }

    /// <summary>Whether this server can connect it, which for an OAuth provider needs its app.</summary>
    public abstract bool IsAvailable(IConfiguration configuration);

    /// <summary>What a connection was granted, as Settings lists it.</summary>
    public abstract IReadOnlyList<string> Access(string? scopes);

    public abstract IStorageDrive Drive(DriveContext context);

    protected const string KeepAccess = "keep access while you're signed out";
}

/// <summary>
/// Storage connected through OAuth: its endpoints and the access asked for, and who an account is. Everything else, from
/// tokens to the folder picker, is shared.
/// </summary>
public abstract class OAuthStorageProvider : StorageProvider
{
    /// <summary>The configuration section's last part: "OneDrive" for "Storage:OneDrive".</summary>
    protected abstract string ConfigurationName { get; }

    public abstract string AuthorizationEndpoint { get; }
    public abstract string TokenEndpoint { get; }
    public abstract IReadOnlyList<string> Scopes { get; }

    /// <summary>Asked for besides the scopes, as some providers hand out a refresh token only when asked.</summary>
    public virtual IReadOnlyDictionary<string, string> AuthorizationParameters { get; } = new Dictionary<string, string>();

    /// <summary>Whether redeeming a refresh token names the scopes again, as Microsoft's endpoint wants.</summary>
    public virtual bool ScopesOnRefresh => false;

    /// <summary>
    /// What each scope lets Bibliotaph do, in Settings' words: "read your files". A scope left out is shown by its
    /// own name, so nothing granted goes unmentioned.
    /// </summary>
    protected abstract IReadOnlyDictionary<string, string> ScopeText { get; }

    public string Section => $"Storage:{ConfigurationName}";

    /// <summary>
    /// Every connection holds a refresh token, so keeping access is always among what it was granted, whether or not
    /// the provider names that as a scope.
    /// </summary>
    public override IReadOnlyList<string> Access(string? scopes)
    {
        var granted = (scopes ?? string.Join(' ', Scopes)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(scope => ScopeText.TryGetValue(scope, out var text) ? text : scope)
            .Append(KeepAccess);
        return [.. granted.Where(text => text.Length > 0).Distinct()];
    }

    /// <summary>Text for a scope that's part of signing in to the provider rather than access worth listing.</summary>
    protected const string Unlisted = "";

    public override bool IsAvailable(IConfiguration configuration) => App(configuration) is not null;

    /// <summary>The OAuth app, or null until it's configured, which leaves the provider out of Settings.</summary>
    public StorageApp? App(IConfiguration configuration) =>
        configuration.GetSection(Section).Get<StorageApp>() is { ClientId.Length: > 0, ClientSecret.Length: > 0 } app ? app : null;

    /// <summary>The account an access token from connecting belongs to.</summary>
    public abstract Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct);
}

/// <summary>Every storage provider the server knows, whether or not it can be connected here.</summary>
public static class StorageProviders
{
    public static readonly IReadOnlyList<StorageProvider> All = [new OneDriveProvider(), new DropboxProvider(), new GoogleDriveProvider(), new WebDavProvider()];

    public static StorageProvider? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static IEnumerable<StorageProvider> Configured(IConfiguration configuration) => All.Where(p => p.IsAvailable(configuration));
}
