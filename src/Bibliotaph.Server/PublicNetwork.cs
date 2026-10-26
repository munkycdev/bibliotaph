using System.Net;
using System.Net.Sockets;

namespace Bibliotaph.Server;

/// <summary>
/// Reaching an address a user typed, such as a model service or a WebDAV server: only on the internet, so an address in
/// Settings can't reach the server's own network or the cloud's metadata and host endpoints.
/// </summary>
public static class PublicNetwork
{
    /// <summary>Azure's host endpoint (DNS, health probes, the VM agent), a public address that answers only inside Azure.</summary>
    static readonly IPAddress AzureHost = IPAddress.Parse("168.63.129.16");

    /// <summary>
    /// A handler whose every connection goes to a public address. No proxy, since a proxy would hide the address the
    /// callback must check, and no redirects, which callers follow themselves where they're safe.
    /// </summary>
    public static SocketsHttpHandler Handler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = ConnectPubliclyAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    /// <summary>Why the server can't reach <paramref name="uri"/>, or null when it can try.</summary>
    public static string? Problem(Uri uri, string what)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return $"Bibliotaph on the web reaches {what} on the internet, over https. Use its https address.";
        if (uri.IsLoopback || (IPAddress.TryParse(uri.IdnHost, out var address) && !IsPublic(address)))
            return $"This address is on a private network, which Bibliotaph on the web can't reach. Use its address on the internet.";
        return null;
    }

    /// <summary>
    /// Connects only to a public address: the name is resolved here, so one that resolves to a private address is
    /// refused however it was typed, and the connection goes to the address that was checked.
    /// </summary>
    public static async ValueTask<Stream> ConnectPubliclyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
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

    /// <summary>Not loopback, private, link-local, shared, multicast, reserved or Azure's host endpoint.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(AzureHost)) return false;
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
        // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address, which is checked as itself.
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6[4..12].All(b => b == 0))
            return IsPublic(new IPAddress(v6[12..16]));
        if (v6[0] == 0x20 && v6[1] == 0x02) return IsPublic(new IPAddress(v6[2..6]));
        return !(address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
            || (v6[0] & 0xFE) == 0xFC);
    }
}
