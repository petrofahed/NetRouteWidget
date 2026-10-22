using System.Management;

namespace NetRoute.Core.Windows;

/// Reads the Windows connection-profile name (for Wi-Fi, the SSID) via WMI's MSFT_NetConnectionProfile
/// class, matched by InterfaceIndex. This is the same data source PowerShell's Get-NetConnectionProfile
/// uses, so results match what Windows' own Settings/Network page shows.
///
/// Deliberately NOT implemented via raw COM interop against INetworkListManager: that interface is
/// IDispatch-derived and Microsoft's own docs list its (and related interfaces') methods alphabetically,
/// not in true IDL/vtable order, so a hand-authored [ComImport] declaration risks silently binding to the
/// wrong vtable slot. ManagementObjectSearcher gives the same data through typed, named-property access
/// with no vtable risk.
public sealed class WindowsNetworkNames : INetworkNameSource
{
    public string? NameOf(AdapterInfo adapter)
    {
        try
        {
            var scope = new ManagementScope(@"root\StandardCimv2");
            var query = new ObjectQuery($"SELECT Name FROM MSFT_NetConnectionProfile WHERE InterfaceIndex = {adapter.Index}");
            using var searcher = new ManagementObjectSearcher(scope, query);
            using var results = searcher.Get();
            using var enumerator = results.GetEnumerator();
            if (!enumerator.MoveNext()) return null;
            using var first = enumerator.Current;
            return first["Name"] as string;
        }
        catch
        {
            return null;
        }
    }
}
