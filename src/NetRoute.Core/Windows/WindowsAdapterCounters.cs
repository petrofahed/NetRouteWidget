using System.Net.NetworkInformation;

namespace NetRoute.Core.Windows;

public sealed class WindowsAdapterCounters : IAdapterCounters
{
    public long? TotalBytes(string adapterName)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!string.Equals(nic.Name, adapterName, StringComparison.OrdinalIgnoreCase)) continue;
                var stats = nic.GetIPStatistics();
                return stats.BytesReceived + stats.BytesSent;
            }
            return null;
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// One GetIfEntry2 call: the same counters GetIPStatistics reports, without enumerating every interface.
    public Traffic? Read(int interfaceIndex)
    {
        var row = new IpHelperNative.MIB_IF_ROW2 { InterfaceIndex = unchecked((uint)interfaceIndex) };
        if (IpHelperNative.GetIfEntry2(ref row) != IpHelperNative.NO_ERROR) return null;
        return new Traffic(ToLong(row.OutOctets), ToLong(row.InOctets));
    }

    static long ToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;
}
