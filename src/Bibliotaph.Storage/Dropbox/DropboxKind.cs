namespace Bibliotaph.Storage.Dropbox;

/// <summary>Dropbox, personal or a team's, through its OAuth 2 endpoints and API v2.</summary>
public sealed class DropboxKind : OAuthStorageKind
{
    public const string Key = "dropbox";

    // A team account's kind keeps the team space's root, which every call names.
    const string TeamKind = "team:";

    public override string Id => Key;
    public override string Name => "Dropbox";
    public override string Owner => "Dropbox";

    public override string AuthorizationEndpoint => "https://www.dropbox.com/oauth2/authorize";
    public override string TokenEndpoint => "https://api.dropboxapi.com/oauth2/token";

    /// <summary>Who the account is, and read its files and folders. Nothing that writes.</summary>
    public override IReadOnlyList<string> Scopes { get; } = ["account_info.read", "files.metadata.read", "files.content.read"];

    protected override IReadOnlyDictionary<string, string> ScopeText { get; } = new Dictionary<string, string>
    {
        ["account_info.read"] = "see your name",
        ["files.metadata.read"] = "see your file and folder names",
        ["files.content.read"] = "read your files",
    };

    /// <summary>Dropbox hands out a refresh token only when asked for offline access.</summary>
    public override IReadOnlyDictionary<string, string> AuthorizationParameters { get; } = new Dictionary<string, string> { ["token_access_type"] = "offline" };

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var account = await new DropboxClient(http, _ => Task.FromResult(accessToken)).CurrentAccountAsync(ct);
        return new StorageIdentity(account.Id, account.Name is { Length: > 0 } name ? $"Dropbox · {name}" : "Dropbox",
            account.TeamRoot is { } root ? TeamKind + root : "personal");
    }

    public override IStorageDrive Drive(DriveContext context) => new DropboxDrive(Client(context));

    /// <summary>The web server hears of changes through Dropbox's webhook; a PC asks with a cursor over the whole account instead.</summary>
    public override IStorageChangeLog ChangeLog(IStorageDrive drive, DriveContext context) => new DropboxChangeLog(Client(context));

    static DropboxClient Client(DriveContext context)
    {
        var kind = context.AccountKind;
        var teamRoot = kind is not null && kind.StartsWith(TeamKind, StringComparison.Ordinal) ? kind[TeamKind.Length..] : null;
        return new DropboxClient(context.Http, context.Credential, teamRoot, context.Clock);
    }
}

/// <summary>Asks a Dropbox account what changed: a cursor over everything in it from now, then what came after each cursor.</summary>
public sealed class DropboxChangeLog(DropboxClient client) : IStorageChangeLog
{
    public Task<string> StartAsync(CancellationToken ct = default) => client.LatestCursorAsync(ct);

    public Task<(bool Changed, string Next)> ChangesSinceAsync(string cursor, CancellationToken ct = default) => client.ChangesSinceAsync(cursor, ct);
}
