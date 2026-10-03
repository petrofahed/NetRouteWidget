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
}
