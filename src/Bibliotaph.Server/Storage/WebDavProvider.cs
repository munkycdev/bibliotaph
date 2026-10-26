using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Bibliotaph.Storage;
using Bibliotaph.Storage.WebDav;

namespace Bibliotaph.Server.Storage;

/// <summary>What connecting a WebDAV folder found: who it is and how to sign in, or why it couldn't be connected.</summary>
public sealed record WebDavConnection(StorageIdentity? Identity, WebDavCredential? Credential, string? Problem);

/// <summary>
/// WebDAV, as Nextcloud, ownCloud, a NAS or any WebDAV server offers it: a folder's address, a user name and a password
/// the user types, which is kept encrypted as other providers' refresh tokens are. There's no OAuth app, so it's
/// available unless "Storage:WebDav:Enabled" is false. The server is wherever the user says, so it must be on the
/// internet, over https.
/// </summary>
public sealed class WebDavProvider : StorageProvider
{
    public const string Key = "webdav";

    public override string Id => Key;
    public override string Name => "WebDAV";
    public override string Owner => "your server";

    public override bool IsAvailable(IConfiguration configuration) => configuration.GetValue("Storage:WebDav:Enabled", true);

    /// <summary>
    /// The password may allow more than reading, as most servers' do, but reading is all Bibliotaph's client does, and
    /// keeping the password is what keeps access.
    /// </summary>
    public override IReadOnlyList<string> Access(string? scopes) => ["read your files with the password you gave", KeepAccess];

    public override IStorageDrive Drive(DriveContext context) =>
        new WebDavDrive(new WebDavClient(context.Http, async ct => WebDavCredential.FromJson(await context.Credential(ct)), context.Refused, context.Clock));

    /// <summary>
    /// Checks that the address is a folder the user name and password open, and names the account: the same address and
    /// user name are the same account, so connecting it again keeps its library folders.
    /// </summary>
    public static async Task<WebDavConnection> ConnectAsync(HttpClient http, string address, string user, string password, CancellationToken ct)
    {
        if (Tidy(address) is not { } url) return Failed("A WebDAV address is like https://cloud.example.com/remote.php/dav/files/you/.");
        if (PublicNetwork.Problem(url, "a WebDAV server") is { } problem) return Failed(problem);
        if (user.Trim().Length == 0 || password.Length == 0) return Failed("Type the user name and password you sign in to that server with.");

        var credential = new WebDavCredential(url.AbsoluteUri, user.Trim(), password);
        try
        {
            var found = await new WebDavClient(http, _ => Task.FromResult(credential)).PropFindAsync("", 0, folder: true, ct);
            if (found is null) return Failed("There's no folder at that address.");
            if (found is not [{ IsFolder: true }, ..]) return Failed("That address is a file. Use the address of the folder it's in.");
        }
        catch (StorageException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            return Failed("The server didn't accept that user name and password. If it uses two-step sign-in, make an app password for Bibliotaph.");
        }
        catch (StorageException ex)
        {
            return Failed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Failed($"Bibliotaph couldn't reach that server: {ex.Message}");
        }
        catch (XmlException)
        {
            return Failed("That address doesn't answer as a WebDAV server. For Nextcloud it's like https://cloud.example.com/remote.php/dav/files/you/.");
        }

        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{credential.Url}\n{credential.User}")))[..20];
        return new WebDavConnection(new StorageIdentity(id, $"WebDAV · {credential.User} on {url.Host}", null), credential, null);
    }

    /// <summary>The address as it's kept: https:// when no scheme was typed, a folder's slash at the end, nothing after a ?.</summary>
    public static Uri? Tidy(string address)
    {
        var typed = address.Trim();
        if (!typed.Contains("://", StringComparison.Ordinal)) typed = "https://" + typed;
        if (!Uri.TryCreate(typed, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length > 0) return null;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return new Uri($"{uri.Scheme}://{uri.Authority}{path}");
    }

    static WebDavConnection Failed(string problem) => new(null, null, problem);
}
