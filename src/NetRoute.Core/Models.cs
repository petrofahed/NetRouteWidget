using System.Net;

namespace NetRoute.Core;

public enum RoutingMode { Phone, Lan, Auto, Wifi }

public enum AdapterKind { Ethernet, Wireless, Other }

/// Which detected adapter Windows would use for internet traffic right now.
public enum InternetPath { None, Phone, Lan, Other, Wifi }

public sealed record AdapterInfo(
    int Index,
    string Name,
    string Description,
    AdapterKind Kind,
    bool IsUp,
    bool HasGateway,
    string? IPv4,
    string Mac,
    string? DnsServer = null,
    string? Id = null,
    int? PrefixLength = null,
    string? Gateway = null,
    long? SpeedBps = null)
{
    /// The CIDR text for an IPv4 address and prefix length, e.g. "192.168.86.0/24".
    public static string? SubnetOf(string ipv4, int prefixLength)
    {
        if (!IPAddress.TryParse(ipv4, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;

        var bytes = address.GetAddressBytes();
        var mask = prefixLength == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLength);
        var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]) & mask;
        var network = new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        return $"{new IPAddress(network)}/{prefixLength}";
    }
}
