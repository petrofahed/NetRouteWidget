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
}
