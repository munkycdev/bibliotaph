namespace Bibliotaph.Storage.OneDrive;

/// <summary>OneDrive, personal or work and school, through the Microsoft identity platform and Microsoft Graph.</summary>
public sealed class OneDriveKind : OAuthStorageKind
{
    public const string Key = "onedrive";

    public override string Id => Key;
    public override string Name => "OneDrive";
    public override string Owner => "Microsoft";

    public override string AuthorizationEndpoint => "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    public override string TokenEndpoint => "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    /// <summary>Read every file (the library folders), and keep access while the user is away. Nothing that writes.</summary>
    public override IReadOnlyList<string> Scopes { get; } = ["offline_access", "Files.Read"];

    // Microsoft adds the sign-in scopes to every token it grants.
    protected override IReadOnlyDictionary<string, string> ScopeText { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Files.Read"] = "read your files",
        ["offline_access"] = KeepAccess,
        ["openid"] = Unlisted,
        ["profile"] = Unlisted,
        ["email"] = Unlisted,
        ["User.Read"] = "see your name",
    };

    public override bool ScopesOnRefresh => true;

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var drive = await new OneDriveClient(http, _ => Task.FromResult(accessToken)).MyDriveAsync(ct);
        return new StorageIdentity(drive.Id, drive.OwnerName is { Length: > 0 } owner ? $"OneDrive · {owner}" : "OneDrive", drive.DriveType);
    }

    public override IStorageDrive Drive(DriveContext context) =>
        new OneDriveDrive(new OneDriveClient(context.Http, context.Credential, context.Clock), context.AccountId);

    /// <summary>The web server hears of changes through Graph's notifications; a PC asks the drive's delta instead.</summary>
    public override IStorageChangeLog ChangeLog(IStorageDrive drive, DriveContext context) =>
        new OneDriveChangeLog(new OneDriveClient(context.Http, context.Credential, context.Clock), context.AccountId);
}

/// <summary>
/// Asks a OneDrive what changed through Graph's delta: a link that starts from now, then each answer's next link. Any
/// item in an answer is a change somewhere in the drive.
/// </summary>
public sealed class OneDriveChangeLog(OneDriveClient client, string driveId) : IStorageChangeLog
{
    public Task<string> StartAsync(CancellationToken ct = default) => client.LatestDeltaAsync(driveId, ct);

    public Task<(bool Changed, string Next)> ChangesSinceAsync(string cursor, CancellationToken ct = default) => client.DeltaSinceAsync(cursor, ct);
}
