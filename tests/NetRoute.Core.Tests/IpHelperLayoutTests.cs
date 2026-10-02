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
}
