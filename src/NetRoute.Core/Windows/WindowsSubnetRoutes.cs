using System.ComponentModel;
using System.Net;

namespace NetRoute.Core.Windows;

/// Reads and writes the RouteMetric of a specific on-link IPv4 route with GetIpForwardEntry2 /
/// SetIpForwardEntry2 (needs admin to write). MIB_IPFORWARD_ROW2's layout is pinned by explicit
/// offsets in IpHelperNative and guarded by IpHelperLayoutTests; this class always Gets the full
/// row before Set so every field but Metric round-trips unchanged.
public sealed class WindowsSubnetRoutes : ISubnetRoutes
{
    public uint? GetOnLinkMetric(int ifIndex, string prefix)
    {
        var row = Read(ifIndex, prefix, out var rc);
        if (rc == IpHelperNative.ERROR_NOT_FOUND) return null;
        if (rc != IpHelperNative.NO_ERROR)
            throw new Win32Exception(rc, $"GetIpForwardEntry2 failed for {prefix} on interface {ifIndex} (Win32 error {rc})");
        return row.Metric;
    }

    public void SetOnLinkMetric(int ifIndex, string prefix, uint metric)
    {
        var row = Read(ifIndex, prefix, out var rc);
        if (rc != IpHelperNative.NO_ERROR)
            throw new Win32Exception(rc, $"Route {prefix} on interface {ifIndex} not found (GetIpForwardEntry2 returned Win32 error {rc})");

        row.Metric = metric;
        rc = IpHelperNative.SetIpForwardEntry2(ref row);
        if (rc != IpHelperNative.NO_ERROR)
            throw new Win32Exception(rc, $"SetIpForwardEntry2 failed for {prefix} on interface {ifIndex} (Win32 error {rc})");
    }

    /// Full native row, read-only. Lets tests compare every field (not just Metric) before and after a Set.
    internal static IpHelperNative.MIB_IPFORWARD_ROW2 ReadRow(int ifIndex, string prefix)
    {
        var row = Read(ifIndex, prefix, out var rc);
        if (rc != IpHelperNative.NO_ERROR)
            throw new Win32Exception(rc, $"GetIpForwardEntry2 failed for {prefix} on interface {ifIndex} (Win32 error {rc})");
        return row;
    }

    static IpHelperNative.MIB_IPFORWARD_ROW2 Read(int ifIndex, string prefix, out int rc)
    {
        var row = BuildLookupRow(ifIndex, prefix);
        rc = IpHelperNative.GetIpForwardEntry2(ref row);
        return row;
    }

    /// A row with just enough filled in (DestinationPrefix, NextHop, InterfaceIndex) for
    /// GetIpForwardEntry2 to find the matching row; it does not enumerate, it looks one up.
    static IpHelperNative.MIB_IPFORWARD_ROW2 BuildLookupRow(int ifIndex, string prefix)
    {
        var parts = prefix.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !byte.TryParse(parts[1], out var prefixLength))
            throw new ArgumentException($"Not an IPv4 CIDR prefix: '{prefix}'", nameof(prefix));

        return new IpHelperNative.MIB_IPFORWARD_ROW2
        {
            InterfaceIndex = (uint)ifIndex,
            DestinationPrefix = new IpHelperNative.IP_ADDRESS_PREFIX
            {
                Prefix = new IpHelperNative.SOCKADDR_INET_V4
                {
                    Family = IpHelperNative.AF_INET,
                    Ipv4Addr = BitConverter.ToUInt32(address.GetAddressBytes(), 0),
                },
                PrefixLength = prefixLength,
            },
            // All-zero, AF_UNSPEC (0): "the next hop is unspecified" for a route to a locally-attached subnet.
            NextHop = new IpHelperNative.SOCKADDR_INET_V4 { Family = 0 },
        };
    }
}
