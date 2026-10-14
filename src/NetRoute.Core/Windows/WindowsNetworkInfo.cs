using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Windows;

public sealed class WindowsNetworkInfo : INetworkInfo
{
    public NicStats? ReadNic(string adapterName)
    {
        try
        {
            var nic = Find(adapterName);
            if (nic is null) return null;
            var stats = nic.GetIPStatistics();
            return new NicStats(
                nic.Name, nic.Speed > 0 ? nic.Speed : null, nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet,
                stats.IncomingPacketsWithErrors, stats.IncomingPacketsDiscarded, stats.OutgoingPacketsWithErrors, stats.OutgoingPacketsDiscarded);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public IPAddress? GatewayV4(string adapterName)
    {
        try
        {
            return Find(adapterName)?.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public int? PrefixLengthV4(string adapterName, IPAddress address)
    {
        try
        {
            return Find(adapterName)?.GetIPProperties().UnicastAddresses.FirstOrDefault(u => u.Address.Equals(address))?.PrefixLength;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    static NetworkInterface? Find(string adapterName) =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Name, adapterName, StringComparison.OrdinalIgnoreCase));
}
