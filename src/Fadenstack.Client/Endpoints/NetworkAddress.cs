using System.Net;
using System.Net.Sockets;

namespace Fadenstack.Endpoints;

/// <summary>Where an address points, for the plain-http rule (D3): this device, a private network, or anywhere else.</summary>
internal enum AddressScope
{
    Public,
    Private,
    Loopback,
}

/// <summary>Sorts addresses into loopback, private (RFC 1918, link-local, fc00::/7, CGNAT is public) and public.</summary>
internal static class NetworkAddress
{
    public static AddressScope Of(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return AddressScope.Loopback;
        }
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                ? AddressScope.Private : AddressScope.Public;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC ? AddressScope.Private : AddressScope.Public;
        }
        return AddressScope.Public;
    }

    /// <summary>
    /// The scope of a host: an address literal by itself; <c>localhost</c> (and names under it, RFC 6761) is loopback; a name
    /// takes the widest scope of every address it resolves to, so one public address makes it public. Unresolvable: public.
    /// </summary>
    public static async Task<AddressScope> OfHostAsync(string host, CancellationToken cancellationToken)
    {
        var name = host.Trim('[', ']');
        if (IPAddress.TryParse(name, out var literal))
        {
            return Of(literal);
        }
        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return AddressScope.Loopback;
        }
        IPAddress[] addresses;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            addresses = await Dns.GetHostAddressesAsync(name).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return AddressScope.Public;
        }
        if (addresses.Length == 0)
        {
            return AddressScope.Public;
        }
        return addresses.Select(Of).Min(); // Public < Private < Loopback: the widest wins
    }
}
