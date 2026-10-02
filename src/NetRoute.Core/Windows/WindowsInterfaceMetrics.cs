using System.ComponentModel;

namespace NetRoute.Core.Windows;

/// Reads and writes interface metrics with GetIpInterfaceEntry / SetIpInterfaceEntry (needs admin to write).
public sealed class WindowsInterfaceMetrics : IInterfaceMetrics
{
    public InterfaceMetricState? Get(int ifIndex, IpFamily family)
    {
        var row = Read(ifIndex, family, out var rc);
        // Windows reports a vanished interface (e.g. phone unplugged mid-refresh) as FILE_NOT_FOUND.
        if (rc is IpHelperNative.ERROR_NOT_FOUND or IpHelperNative.ERROR_FILE_NOT_FOUND) return null;
        if (rc != IpHelperNative.NO_ERROR) throw new Win32Exception(rc);
        return new InterfaceMetricState(row.UseAutomaticMetric != 0, row.Metric);
    }

    /// Full native row, read-only. Lets tests compare every field against Get-NetIPInterface.
    internal static IpHelperNative.MIB_IPINTERFACE_ROW ReadRow(int ifIndex, IpFamily family)
    {
        var row = Read(ifIndex, family, out var rc);
        if (rc != IpHelperNative.NO_ERROR) throw new Win32Exception(rc);
        return row;
    }

    public void Set(int ifIndex, IpFamily family, uint? metric)
    {
        var row = Read(ifIndex, family, out var rc);
        if (rc != IpHelperNative.NO_ERROR) throw new Win32Exception(rc);

        row.UseAutomaticMetric = metric is null ? (byte)1 : (byte)0;
        if (metric is { } m) row.Metric = m;
        // Documented quirk: for IPv4 SitePrefixLength must be 0, or Set fails with ERROR_INVALID_PARAMETER.
        if (family == IpFamily.IPv4) row.SitePrefixLength = 0;

        rc = IpHelperNative.SetIpInterfaceEntry(ref row);
        if (rc != IpHelperNative.NO_ERROR) throw new Win32Exception(rc);
    }

    static IpHelperNative.MIB_IPINTERFACE_ROW Read(int ifIndex, IpFamily family, out int rc)
    {
        var row = new IpHelperNative.MIB_IPINTERFACE_ROW
        {
            Family = family == IpFamily.IPv4 ? IpHelperNative.AF_INET : IpHelperNative.AF_INET6,
            InterfaceIndex = (uint)ifIndex,
            ZoneIndices = new uint[16],
        };
        rc = IpHelperNative.GetIpInterfaceEntry(ref row);
        return row;
    }
}
