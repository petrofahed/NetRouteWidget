using System.Diagnostics;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

/// Read-only checks against the real machine. They never change any setting.
[Trait("Category", "Integration")]
public class WindowsIntegrationTests
{
    // One line per connected interface/family. Enabled/Disabled become 1/0; RouterDiscovery Disabled/Enabled/other(DHCP) becomes 0/1/2.
    const string GetNetIpInterfaceScript = """
        function Flag($s) { if ("$s" -eq 'Enabled') { 1 } else { 0 } }
        function RdCode($s) { switch ("$s") { 'Disabled' { 0 } 'Enabled' { 1 } default { 2 } } }
        Get-NetIPInterface -ConnectionState Connected | ForEach-Object {
          '{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14}' -f `
            $_.ifIndex, $_.AddressFamily, $_.InterfaceMetric, (Flag $_.AutomaticMetric), (Flag $_.Forwarding),
            (Flag $_.WeakHostSend), (Flag $_.WeakHostReceive), (Flag $_.NeighborUnreachabilityDetection), (RdCode $_.RouterDiscovery),
            $_.DadTransmits, $_.BaseReachableTime, $_.RetransmitTime, $_.NlMtu, $_.ReachableTime, (Flag $_.IgnoreDefaultRoutes)
        }
        """;

    [Fact]
    public void Metrics_read_through_ip_helper_match_Get_NetIPInterface()
    {
        var expected = PowerShell(GetNetIpInterfaceScript)
            .Select(line => line.Split(','))
            .Select(f => (Index: int.Parse(f[0]), Family: f[1] == "IPv4" ? IpFamily.IPv4 : IpFamily.IPv6,
                          Metric: uint.Parse(f[2]), Automatic: f[3] == "1",
                          Fields: string.Join(",", f.Skip(2))))
            .ToList();
        Assert.NotEmpty(expected);

        var metrics = new WindowsInterfaceMetrics();
        foreach (var e in expected)
        {
            var actual = metrics.Get(e.Index, e.Family);
            Assert.NotNull(actual);
            Assert.Equal((e.Index, e.Family, e.Automatic, e.Metric), (e.Index, e.Family, actual.UseAutomatic, actual.Metric));

            // Every other field the Set path writes back must also line up, or Set would corrupt it.
            var row = WindowsInterfaceMetrics.ReadRow(e.Index, e.Family);
            var native = string.Join(",", row.Metric, row.UseAutomaticMetric, row.ForwardingEnabled,
                row.WeakHostSend, row.WeakHostReceive, row.UseNeighborUnreachabilityDetection, row.RouterDiscoveryBehavior,
                row.DadTransmits, row.BaseReachableTime, row.RetransmitTime, row.NlMtu, row.ReachableTime, row.DisableDefaultRoutes);
            Assert.Equal($"if{e.Index} {e.Family}: {e.Fields}", $"if{e.Index} {e.Family}: {native}");
        }
    }

    [Fact]
    public void Vanished_interface_is_reported_as_absent_for_both_families()
    {
        var metrics = new WindowsInterfaceMetrics();

        Assert.Null(metrics.Get(999999, IpFamily.IPv4));
        Assert.Null(metrics.Get(999999, IpFamily.IPv6));
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
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi)!;
        // Read both streams concurrently so a full pipe cannot block the child.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"PowerShell did not finish within 30 s. Script: {script}");
        }
        process.WaitForExit(); // flush the async readers
        if (process.ExitCode != 0)
            Assert.Fail($"PowerShell exited with {process.ExitCode}. Stderr: {stderr.Result}\nScript: {script}");
        return stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
