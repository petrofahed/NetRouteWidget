using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

public sealed class WindowsAdapterSource : IAdapterSource
{
    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var adapters = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var properties = nic.GetIPProperties();
            if (IndexOf(properties) is not { } index) continue;

            var ipv4 = properties.UnicastAddresses
                .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            var hasGateway = properties.GatewayAddresses
                .Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));

            var dnsServer = properties.DnsAddresses
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();

            adapters.Add(new AdapterInfo(
                index, nic.Name, nic.Description, KindOf(nic.NetworkInterfaceType),
                nic.OperationalStatus == OperationalStatus.Up, hasGateway, ipv4, FormatMac(nic.GetPhysicalAddress()),
                dnsServer));
        }
        return adapters;
    }

    /// The ifIndex is shared by both IP families; an interface may have only one of them.
    static int? IndexOf(IPInterfaceProperties properties)
    {
        try
        {
            if (properties.GetIPv4Properties() is { } v4) return v4.Index;
        }
        catch (NetworkInformationException) { }
        try
        {
            if (properties.GetIPv6Properties() is { } v6) return v6.Index;
        }
        catch (NetworkInformationException) { }
        return null;
    }

    static AdapterKind KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
        NetworkInterfaceType.Wireless80211 => AdapterKind.Wireless,
        _ => AdapterKind.Other,
    };

    static string FormatMac(PhysicalAddress mac) =>
        string.Join("-", mac.GetAddressBytes().Select(b => b.ToString("X2")));
}
