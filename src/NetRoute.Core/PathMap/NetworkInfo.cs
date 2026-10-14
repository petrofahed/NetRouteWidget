using System.Net;

namespace NetRoute.Core.PathMap;

/// A network card's link speed and error counters at one moment.
public sealed record NicStats(string Name, long? SpeedBps, bool IsEthernet, long InErrors, long InDiscards, long OutErrors, long OutDiscards);

/// What Windows knows about the LAN adapter, behind an interface so tests use fakes.
public interface INetworkInfo
{
    NicStats? ReadNic(string adapterName);

    /// The adapter's IPv4 default gateway (the router), or null.
    IPAddress? GatewayV4(string adapterName);

    /// The prefix length of the adapter's IPv4 address (24 for a /24), or null.
    int? PrefixLengthV4(string adapterName, IPAddress address);
}
