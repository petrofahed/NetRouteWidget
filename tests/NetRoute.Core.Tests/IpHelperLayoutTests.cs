using System.Runtime.InteropServices;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class IpHelperLayoutTests
{
    [Fact]
    public void Interface_row_matches_native_size_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        Assert.Equal(168, Marshal.SizeOf<IpHelperNative.MIB_IPINTERFACE_ROW>());
    }

    [Fact]
    public void Interface_row_fields_sit_at_the_native_offsets_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        var offsets = new[]
        {
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.UseAutomaticMetric),
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.RouterDiscoveryBehavior),
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.ZoneIndices),
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.SitePrefixLength),
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.Metric),
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.NlMtu),
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.ReachableTime),
            nameof(IpHelperNative.MIB_IPINTERFACE_ROW.DisableDefaultRoutes),
        }.Select(f => $"{f}={(int)Marshal.OffsetOf<IpHelperNative.MIB_IPINTERFACE_ROW>(f)}");

        Assert.Equal(
            new[]
            {
                "UseAutomaticMetric=44", "RouterDiscoveryBehavior=52", "ZoneIndices=80", "SitePrefixLength=144",
                "Metric=148", "NlMtu=152", "ReachableTime=160", "DisableDefaultRoutes=166",
            },
            offsets);
    }

    [Fact]
    public void Sockaddr_inet_v4_matches_native_size_and_offsets_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        Assert.Equal(28, Marshal.SizeOf<IpHelperNative.SOCKADDR_INET_V4>());
        Assert.Equal(0, (int)Marshal.OffsetOf<IpHelperNative.SOCKADDR_INET_V4>(nameof(IpHelperNative.SOCKADDR_INET_V4.Family)));
        Assert.Equal(2, (int)Marshal.OffsetOf<IpHelperNative.SOCKADDR_INET_V4>(nameof(IpHelperNative.SOCKADDR_INET_V4.Port)));
        Assert.Equal(4, (int)Marshal.OffsetOf<IpHelperNative.SOCKADDR_INET_V4>(nameof(IpHelperNative.SOCKADDR_INET_V4.Ipv4Addr)));
    }

    [Fact]
    public void Ip_address_prefix_matches_native_size_and_offsets_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        Assert.Equal(32, Marshal.SizeOf<IpHelperNative.IP_ADDRESS_PREFIX>());
        Assert.Equal(0, (int)Marshal.OffsetOf<IpHelperNative.IP_ADDRESS_PREFIX>(nameof(IpHelperNative.IP_ADDRESS_PREFIX.Prefix)));
        Assert.Equal(28, (int)Marshal.OffsetOf<IpHelperNative.IP_ADDRESS_PREFIX>(nameof(IpHelperNative.IP_ADDRESS_PREFIX.PrefixLength)));
    }

    [Fact]
    public void Forward_row2_matches_native_size_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        Assert.Equal(104, Marshal.SizeOf<IpHelperNative.MIB_IPFORWARD_ROW2>());
    }

    [Fact]
    public void Forward_row2_fields_sit_at_the_native_offsets_on_x64()
    {
        Assert.True(Environment.Is64BitProcess, "Layout check assumes x64");
        var offsets = new[]
        {
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.InterfaceLuid),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.InterfaceIndex),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.DestinationPrefix),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.NextHop),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.SitePrefixLength),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.ValidLifetime),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.PreferredLifetime),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.Metric),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.Protocol),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.Loopback),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.AutoconfigureAddress),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.Publish),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.Immortal),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.Age),
            nameof(IpHelperNative.MIB_IPFORWARD_ROW2.Origin),
        }.Select(f => $"{f}={(int)Marshal.OffsetOf<IpHelperNative.MIB_IPFORWARD_ROW2>(f)}");

        Assert.Equal(
            new[]
            {
                "InterfaceLuid=0", "InterfaceIndex=8", "DestinationPrefix=12", "NextHop=44", "SitePrefixLength=72",
                "ValidLifetime=76", "PreferredLifetime=80", "Metric=84", "Protocol=88", "Loopback=92",
                "AutoconfigureAddress=93", "Publish=94", "Immortal=95", "Age=96", "Origin=100",
            },
            offsets);
    }
}
