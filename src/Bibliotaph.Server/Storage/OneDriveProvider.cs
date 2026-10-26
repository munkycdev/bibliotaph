using Bibliotaph.Storage;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Storage;

/// <summary>OneDrive, personal or work and school, through the Microsoft identity platform and Microsoft Graph.</summary>
public sealed class OneDriveProvider : StorageProvider
{
    public const string Key = "onedrive";

    public override string Id => Key;
    public override string Name => "OneDrive";
    public override string Owner => "Microsoft";
    protected override string ConfigurationName => "OneDrive";

    public override string AuthorizationEndpoint => "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
    public override string TokenEndpoint => "https://login.microsoftonline.com/common/oauth2/v2.0/token";

    /// <summary>
    /// Read every file (the library folders), read and write only Bibliotaph's own app folder (where imported books
    /// go), and keep access while the user is away. Nothing broader: no write access to the user's own folders.
    /// </summary>
    public override IReadOnlyList<string> Scopes { get; } = ["offline_access", "Files.Read", "Files.ReadWrite.AppFolder"];

    public override bool ScopesOnRefresh => true;

    public override async Task<StorageIdentity> IdentifyAsync(HttpClient http, string accessToken, CancellationToken ct)
    {
        var drive = await new OneDriveClient(http, _ => Task.FromResult(accessToken)).MyDriveAsync(ct);
        return new StorageIdentity(drive.Id, drive.OwnerName is { Length: > 0 } owner ? $"OneDrive · {owner}" : "OneDrive", drive.DriveType);
    }

    public override IStorageDrive Drive(HttpClient http, string accountId, Func<CancellationToken, Task<string>> accessToken, TimeProvider clock) =>
        new OneDriveDrive(new OneDriveClient(http, accessToken, clock), accountId);
}
