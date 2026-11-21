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
    public void ReadAll_reports_every_live_adapter_with_non_negative_directions()
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        if (nic is null) return; // a machine with no live adapter at all: nothing to read

        var all = new WindowsAdapterCounters().ReadAll();

        Assert.True(all.TryGetValue(nic.Name.ToUpperInvariant(), out var traffic)); // key is case-insensitive
        Assert.True(traffic.Up >= 0 && traffic.Down >= 0);
    }
}
