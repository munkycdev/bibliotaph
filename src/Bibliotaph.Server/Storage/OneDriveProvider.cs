using Bibliotaph.Storage;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Storage;

/// <summary>OneDrive, personal or work and school, through the Microsoft identity platform and Microsoft Graph.</summary>
public sealed class OneDriveProvider : OAuthStorageProvider
{
    public const string Key = "onedrive";

    public override string Id => Key;
    public override string Name => "OneDrive";
    public override string Owner => "Microsoft";
    protected override string ConfigurationName => "OneDrive";

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
        new OneDriveDrive(new OneDriveClient(context.Http, context.Credential, context.Clock), context.Account.AccountId);
}
