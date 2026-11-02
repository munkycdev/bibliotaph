using Bibliotaph.Storage.WebDav;

namespace Bibliotaph.Server.Storage;

/// <summary>
/// WebDAV on this server: available unless "Storage:WebDav:Enabled" is false, as there's no OAuth app. The server is
/// wherever the user says, so it must be on the internet, over https.
/// </summary>
public sealed class WebDavProvider() : StorageProvider(new WebDavKind())
{
    public const string Key = WebDavKind.Key;

    public override bool IsAvailable(IConfiguration configuration) => configuration.GetValue("Storage:WebDav:Enabled", true);

    public static Task<WebDavConnection> ConnectAsync(HttpClient http, string address, string user, string password, CancellationToken ct) =>
        WebDavKind.ConnectAsync(http, address, user, password, ct, url => PublicNetwork.Problem(url, "a WebDAV server"));

    public static Uri? Tidy(string address) => WebDavKind.Tidy(address);
}
