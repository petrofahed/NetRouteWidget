using System.Net.NetworkInformation;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class WindowsAdapterCountersTests
{
    [Fact]
    public void An_unknown_adapter_has_no_counters()
    {
        Assert.Null(new WindowsAdapterCounters().TotalBytes("No such adapter " + Guid.NewGuid()));
    }

    [Fact]
    public void A_real_adapter_reports_a_non_negative_total()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        if (nic is null) return; // a machine with no live adapter at all: nothing to read

        var total = new WindowsAdapterCounters().TotalBytes(nic.Name);

        Assert.NotNull(total);
        Assert.True(total >= 0);
    }

    [Fact]
    public void Read_by_index_matches_the_adapters_own_statistics()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
            && IndexOf(n) is not null);
        if (nic is null) return; // a machine with no live adapter at all: nothing to read
        var index = IndexOf(nic)!.Value;

        var before = nic.GetIPStatistics();
        var read = new WindowsAdapterCounters().Read(index);

        Assert.NotNull(read);
        var traffic = read.Value;
        // The counters only grow, and the two reads are moments apart.
        const long margin = 100L * 1024 * 1024;
        Assert.InRange(traffic.Up, before.BytesSent, before.BytesSent + margin);
        Assert.InRange(traffic.Down, before.BytesReceived, before.BytesReceived + margin);
    }

    [Fact]
    public void Read_of_an_unknown_index_is_null()
    {
        Assert.Null(new WindowsAdapterCounters().Read(int.MaxValue));
    }

    /// The ifIndex, as WindowsAdapterSource gets it: shared by both IP families.
    static int? IndexOf(NetworkInterface nic)
    {
        var properties = nic.GetIPProperties();
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
}
