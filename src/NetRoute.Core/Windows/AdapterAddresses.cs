using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetRoute.Core.Windows;

/// All addresses (IPv4 and IPv6) of the adapter, so IPv6 traffic is attributed to the right exit too:
/// AdapterInfo only knows the IPv4 address.
public static class AdapterAddresses
{
    /// The detected IPv4 first, then whatever the lookup finds by adapter name; a failing lookup keeps what we have.
    public static IReadOnlyList<IPAddress> Of(AdapterInfo? adapter, Func<string, IReadOnlyList<IPAddress>> lookup)
    {
        if (adapter is null) return [];
        var all = new List<IPAddress>();
        if (IPAddress.TryParse(adapter.IPv4, out var v4)) all.Add(v4);
        try
        {
            foreach (var ip in lookup(adapter.Name))
                if (!all.Contains(ip)) all.Add(ip);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // keep the detected address: the next apply tries again
        }
        return all;
    }

    /// The unicast addresses Windows lists for the adapter with this name.
    public static IReadOnlyList<IPAddress> FromWindows(string adapterName)
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
            return nic.GetIPProperties().UnicastAddresses
                .Select(u => u.Address)
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .ToList();
        }
        return [];
    }
}
