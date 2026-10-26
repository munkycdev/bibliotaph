using System.Net;
using System.Net.Sockets;
using Bibliotaph.Classification;

namespace Bibliotaph.Server.Ai;

/// <summary>
/// How a library reaches a model service: by address, with the key read when a request is made. <see cref="QuietPeriod"/>
/// is how long reading whole books waits after the last request a person or the catalogue made; half a minute unless
/// tests give a shorter one.
/// </summary>
public sealed record ModelClientFactory(Func<string, Func<string?>, IModelClient> Create, TimeSpan? QuietPeriod = null);

/// <summary>
/// Where the server may send a user's book excerpts: a model service on the internet, over https. Anything else would
/// let an address typed in Settings reach the server's own network or the cloud's metadata endpoints.
/// </summary>
public static class ModelServices
{
    /// <summary>A model can take minutes to read a long excerpt.</summary>
    static readonly Lazy<HttpClient> Http = new(() => new HttpClient(new SocketsHttpHandler
    {
        // Every connection goes where the callback decides, so a proxy would hide the address it must check.
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = ConnectPubliclyAsync,
    })
    { Timeout = TimeSpan.FromMinutes(10) });

    /// <summary>The client for a model service, reading its key from <paramref name="key"/> when a request is made.</summary>
    public static IModelClient ClientFor(string endpoint, Func<string?> key) => new LocalModelClient(Http.Value, endpoint, key);

    /// <summary>The address as it is kept, https:// when no scheme was typed, or null when it isn't an address.</summary>
    public static string? Tidy(string endpoint) =>
        LocalModelClient.Tidy(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "https://" + endpoint.Trim());

    /// <summary>Why the server can't use <paramref name="endpoint"/>, or null when it can try.</summary>
    public static string? Problem(string endpoint)
    {
        if (Tidy(endpoint) is not { } tidy || !Uri.TryCreate(tidy, UriKind.Absolute, out var uri))
            return "An address is like https://api.openai.com/v1.";
        if (uri.Scheme != Uri.UriSchemeHttps)
            return "Bibliotaph on the web reaches a model service on the internet, over https. Use its https address.";
        if (uri.IsLoopback || (IPAddress.TryParse(uri.IdnHost, out var address) && !IsPublic(address)))
            return "This address is on a private network, which Bibliotaph on the web can't reach. Use the service's address on the internet.";
        return null;
    }

    /// <summary>
    /// Connects only to a public address: the name is resolved here, so one that resolves to a private address is
    /// refused however it was typed, and the connection goes to the address that was checked.
    /// </summary>
    static async ValueTask<Stream> ConnectPubliclyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0) throw new HttpRequestException($"{context.DnsEndPoint.Host} isn't on the internet.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Not loopback, private, link-local, shared, multicast or reserved.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] < 128)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] < 32)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var v6 = address.GetAddressBytes();
        return !(address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (v6[0] & 0xFE) == 0xFC);
    }
}
