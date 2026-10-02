using System.Diagnostics;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

/// Read-only checks against the real machine. They never change any setting.
[Trait("Category", "Integration")]
public class WindowsIntegrationTests
{
    [Fact]
    public void Metrics_read_through_ip_helper_match_Get_NetIPInterface()
    {
        var expected = PowerShell(
                "Get-NetIPInterface -ConnectionState Connected | ForEach-Object { '{0},{1},{2},{3}' -f $_.ifIndex, $_.AddressFamily, $_.InterfaceMetric, $_.AutomaticMetric }")
            .Select(line => line.Split(','))
            .Select(f => (Index: int.Parse(f[0]), Family: f[1] == "IPv4" ? IpFamily.IPv4 : IpFamily.IPv6,
                          Metric: uint.Parse(f[2]), Automatic: f[3] == "Enabled"))
            .ToList();
        Assert.NotEmpty(expected);

        var metrics = new WindowsInterfaceMetrics();
        foreach (var e in expected)
        {
            var actual = metrics.Get(e.Index, e.Family);
            Assert.NotNull(actual);
            Assert.Equal((e.Index, e.Family, e.Automatic, e.Metric), (e.Index, e.Family, actual.UseAutomatic, actual.Metric));
        }
    }

    [Fact]
    public void Best_interface_for_1_1_1_1_matches_Find_NetRoute()
    {
        var expected = int.Parse(PowerShell("(Find-NetRoute -RemoteIPAddress 1.1.1.1 | Select-Object -First 1).InterfaceIndex").Single());

        Assert.Equal(expected, new WindowsRouteQuery().GetBestInterfaceIndex(RouteController.ProbeTargetV4));
    }

    [Fact]
    public void Ipv6_route_query_does_not_throw()
    {
        var index = new WindowsRouteQuery().GetBestInterfaceIndex(RouteController.ProbeTargetV6);

        Assert.True(index is null or > 0);
    }

    [Fact]
    public void Adapter_source_reports_the_internet_adapter_as_up_with_an_address()
    {
        var best = new WindowsRouteQuery().GetBestInterfaceIndex(RouteController.ProbeTargetV4);

        var adapter = new WindowsAdapterSource().GetAdapters().Single(a => a.Index == best);

        Assert.True(adapter.IsUp);
        Assert.True(adapter.HasGateway);
        Assert.NotNull(adapter.IPv4);
    }

    static string[] PowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
