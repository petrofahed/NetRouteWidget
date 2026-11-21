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

    public IReadOnlyDictionary<string, Traffic> ReadAll()
    {
        var all = new Dictionary<string, Traffic>(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var stats = nic.GetIPStatistics();
                all[nic.Name] = new Traffic(stats.BytesSent, stats.BytesReceived);
            }
            catch (NetworkInformationException)
            {
                // this adapter vanished or has no statistics: the others are still good
            }
        }
        return all;
    }
}
