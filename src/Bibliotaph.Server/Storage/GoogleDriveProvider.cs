using Bibliotaph.Storage;
using Bibliotaph.Storage.GoogleDrive;

namespace Bibliotaph.Server.Storage;

/// <summary>Google Drive, through Google's OAuth 2 endpoints and Drive API v3.</summary>
public sealed class GoogleDriveProvider : OAuthStorageProvider
{
    public const string Key = "googledrive";

    public override string Id => Key;
    public override string Name => "Google Drive";
    public override string Owner => "Google";
    protected override string ConfigurationName => "GoogleDrive";

    public override string AuthorizationEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";
    public override string TokenEndpoint => "https://oauth2.googleapis.com/token";

    /// <summary>
    /// Read every file, and nothing that writes. Library folders can be anywhere in a drive, and Drive's narrower
    /// scope covers only files a user picks one by one.
    /// </summary>
    public override IReadOnlyList<string> Scopes { get; } = ["https://www.googleapis.com/auth/drive.readonly"];

    protected override IReadOnlyDictionary<string, string> ScopeText { get; } = new Dictionary<string, string>
    {
        ["https://www.googleapis.com/auth/drive.readonly"] = "see and read your files",
    };

    /// <summary>
    /// Google hands out a refresh token only for offline access, and only the first time unless consent is asked again,
    /// which a reconnect needs.
    /// </summary>
    public override IReadOnlyDictionary<string, string> AuthorizationParameters { get; } =
        new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" };

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var user = await new GoogleDriveClient(http, _ => Task.FromResult(accessToken)).AboutAsync(ct);
        var who = user.Name is { Length: > 0 } name ? name : user.Email;
        return new StorageIdentity(user.PermissionId, who is { Length: > 0 } ? $"Google Drive · {who}" : "Google Drive", null);
    }

    public override IStorageDrive Drive(DriveContext context) =>
        new GoogleDriveDrive(new GoogleDriveClient(context.Http, context.Credential, context.Clock), context.Clock);
}
