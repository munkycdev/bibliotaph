namespace Bibliotaph.Storage;

/// <summary>Whose a newly connected account is at its provider, as connecting it found.</summary>
public sealed record StorageIdentity(string AccountId, string DisplayName, string? AccountKind);

/// <summary>
/// What a provider's client is made with: the HTTP client its calls go through, the connected account, its credential
/// (an access token, or what the user typed for a provider without OAuth) read when a request is made, and what to do
/// when the provider refuses that credential.
/// </summary>
public sealed record DriveContext(HttpClient Http, string AccountId, string? AccountKind, Func<CancellationToken, Task<string>> Credential,
    Func<CancellationToken, Task> Refused, TimeProvider Clock);

/// <summary>
/// One kind of storage a user can connect, as both the web server and the Windows app see it: its names, what a
/// connection is granted in Settings' words, and its client behind <see cref="IStorageDrive"/>. Where its OAuth app
/// comes from and where connections are kept is each app's own.
/// </summary>
public abstract class StorageKind
{
    /// <summary>The ID in library paths, routes and the connection lists: "onedrive".</summary>
    public abstract string Id { get; }

    /// <summary>"OneDrive".</summary>
    public abstract string Name { get; }

    /// <summary>Who asks the user for access, in "allow Bibliotaph to read your files when Microsoft asks".</summary>
    public abstract string Owner { get; }

    /// <summary>What a connection does, in Settings' words, when what it was granted doesn't say it all.</summary>
    public virtual string? About => null;

    /// <summary>Whether it's connected by starting in the provider's own app rather than from Settings.</summary>
    public virtual bool StartsInItsOwnApp => false;

    /// <summary>What a connection was granted, as Settings lists it.</summary>
    public abstract IReadOnlyList<string> Access(string? scopes);

    public abstract IStorageDrive Drive(DriveContext context);

    /// <summary>
    /// How to ask a drive whether anything changed, for an app that can't be sent the provider's notifications, as a PC
    /// can't. Most drives can be asked themselves; a provider whose drive leaves it to webhooks says how here.
    /// </summary>
    public virtual IStorageChangeLog? ChangeLog(IStorageDrive drive, DriveContext context) => drive as IStorageChangeLog;

    protected const string KeepAccess = "keep access while you're signed out";
}

/// <summary>
/// Storage connected through OAuth: its endpoints and the access asked for, and who an account is. Everything else, from
/// tokens to the folder picker, is shared.
/// </summary>
public abstract class OAuthStorageKind : StorageKind
{
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

    /// <summary>The account an access token from connecting belongs to.</summary>
    public abstract Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct);
}

/// <summary>The kinds of storage both apps can connect. Nornis is left out: where it is depends on the server's configuration.</summary>
public static class StorageKinds
{
    public static readonly IReadOnlyList<StorageKind> All =
        [new OneDrive.OneDriveKind(), new Dropbox.DropboxKind(), new GoogleDrive.GoogleDriveKind(), new Box.BoxKind(), new WebDav.WebDavKind()];

    public static StorageKind? Find(string id) => All.FirstOrDefault(k => k.Id == id);
}
