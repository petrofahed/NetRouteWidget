using System.Diagnostics;
using NetRoute.Core.Windows;
using Xunit.Abstractions;

namespace NetRoute.Core.Tests;

/// Unlike WindowsIntegrationTests (read-only), this class intentionally pins a REAL local subnet
/// route on this machine for the duration of one test and always reverts it to its original metric
/// in `finally` - it must never leave the real machine's route table altered after the run. Needs
/// admin rights to write (SetOnLinkMetric); run only in an elevated session.
[Trait("Category", "Integration")]
public class WindowsSubnetRoutesIntegrationTests
{
    readonly ITestOutputHelper _output;

    public WindowsSubnetRoutesIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Pins_a_real_subnet_route_and_reverts_every_field_but_metric_unchanged()
    {
        var adapter = new WindowsAdapterSource().GetAdapters()
            .First(a => a.Name != "NetRoute" && a.IsUp && a.HasGateway && a.IPv4 is not null && a.PrefixLength is not null);
        var prefix = AdapterInfo.SubnetOf(adapter.IPv4!, adapter.PrefixLength!.Value);
        Assert.NotNull(prefix);
        _output.WriteLine($"Using interface {adapter.Index} ({adapter.Name}), prefix {prefix}");

        var routes = new WindowsSubnetRoutes();
        var before = WindowsSubnetRoutes.ReadRow(adapter.Index, prefix!);
        var original = before.Metric;
        _output.WriteLine($"Original metric (native row): {original}");

        var expectedBefore = uint.Parse(PowerShell(
            $"(Get-NetRoute -InterfaceIndex {adapter.Index} -DestinationPrefix '{prefix}' -NextHop 0.0.0.0 -PolicyStore ActiveStore).RouteMetric").Single());
        _output.WriteLine($"Original metric (Get-NetRoute): {expectedBefore}");
        Assert.Equal(expectedBefore, original);
        if (original != RoutePin.DefaultMetric)
            _output.WriteLine($"NOTE: this route's starting metric ({original}) is not RoutePin.DefaultMetric ({RoutePin.DefaultMetric}); " +
                "a real RoutePin.Release on this route would leave it at a different value than it started with.");

        try
        {
            var changed = original == 777 ? 778u : 777u;
            _output.WriteLine($"Setting metric to {changed}");

            routes.SetOnLinkMetric(adapter.Index, prefix!, changed);

            var afterSet = WindowsSubnetRoutes.ReadRow(adapter.Index, prefix!);
            _output.WriteLine($"Metric after Set (native row): {afterSet.Metric}");
            Assert.Equal(changed, afterSet.Metric);
            Assert.Equal(changed, routes.GetOnLinkMetric(adapter.Index, prefix!));

            var expectedAfter = uint.Parse(PowerShell(
                $"(Get-NetRoute -InterfaceIndex {adapter.Index} -DestinationPrefix '{prefix}' -NextHop 0.0.0.0 -PolicyStore ActiveStore).RouteMetric").Single());
            _output.WriteLine($"Metric after Set (Get-NetRoute): {expectedAfter}");
            Assert.Equal(changed, expectedAfter);

            AssertRowUnchangedExceptMetricAndAge(before, afterSet);
        }
        finally
        {
            routes.SetOnLinkMetric(adapter.Index, prefix!, original);
            _output.WriteLine($"Reverted metric to {original}");
        }

        var after = WindowsSubnetRoutes.ReadRow(adapter.Index, prefix!);
        Assert.Equal(original, after.Metric);
        AssertRowUnchangedExceptMetricAndAge(before, after);

        var expectedFinal = uint.Parse(PowerShell(
            $"(Get-NetRoute -InterfaceIndex {adapter.Index} -DestinationPrefix '{prefix}' -NextHop 0.0.0.0 -PolicyStore ActiveStore).RouteMetric").Single());
        _output.WriteLine($"Final metric (Get-NetRoute): {expectedFinal}");
        Assert.Equal(original, expectedFinal);
    }

    [Fact]
    public void Absent_route_on_a_real_interface_returns_null_not_throw()
    {
        var adapter = new WindowsAdapterSource().GetAdapters()
            .First(a => a.Name != "NetRoute" && a.IsUp && a.HasGateway && a.IPv4 is not null);

        // TEST-NET-3 (RFC 5737): never a real on-link subnet, so no such route exists on this interface.
        Assert.Null(new WindowsSubnetRoutes().GetOnLinkMetric(adapter.Index, "203.0.113.0/24"));
    }

    /// Exercises the real "Restoring pinned local subnet routes..." PowerShell snippet from
    /// tools/Restore-Network.cmd (lifted out of the script, same approach as RestoreScriptTests'
    /// SettingsRewriteSnippet) against a route this test pins to 0 itself, then reverts.
    [Fact]
    public void Restore_script_snippet_resets_a_pinned_real_route_to_256()
    {
        var adapter = new WindowsAdapterSource().GetAdapters()
            .First(a => a.Name != "NetRoute" && a.IsUp && a.HasGateway && a.IPv4 is not null && a.PrefixLength is not null);
        var prefix = AdapterInfo.SubnetOf(adapter.IPv4!, adapter.PrefixLength!.Value);
        Assert.NotNull(prefix);

        var routes = new WindowsSubnetRoutes();
        var original = routes.GetOnLinkMetric(adapter.Index, prefix!);
        Assert.NotNull(original);
        _output.WriteLine($"Using interface {adapter.Index} ({adapter.Name}), prefix {prefix}, original metric {original}");

        try
        {
            routes.SetOnLinkMetric(adapter.Index, prefix!, RoutePin.PinnedMetric);
            Assert.Equal(RoutePin.PinnedMetric, routes.GetOnLinkMetric(adapter.Index, prefix!));

            var output = RunPowerShellScript(RouteResetSnippet());
            _output.WriteLine(output);

            Assert.Contains($"restored local route {prefix}", output);
            Assert.Equal(RoutePin.DefaultMetric, routes.GetOnLinkMetric(adapter.Index, prefix!));
        }
        finally
        {
            routes.SetOnLinkMetric(adapter.Index, prefix!, original!.Value);
        }

        Assert.Equal(original, routes.GetOnLinkMetric(adapter.Index, prefix!));
    }

    /// The PowerShell command of the "Restoring pinned local subnet routes..." step, lifted out of
    /// the script so the test exercises the real text (same approach as RestoreScriptTests).
    static string RouteResetSnippet()
    {
        var line = File.ReadAllLines(Path.Combine(RepoRoot(), "tools", "Restore-Network.cmd"))
            .Single(l => l.TrimStart().StartsWith("powershell", StringComparison.OrdinalIgnoreCase) && l.Contains("restored local route"));
        var m = System.Text.RegularExpressions.Regex.Match(line, "-Command \"(?<script>.*)\"\\s*$");
        Assert.True(m.Success, "could not find the -Command script in: " + line);
        return m.Groups["script"].Value;
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root (global.json) not found");
    }

    static string RunPowerShellScript(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(60_000), "powershell timed out");
        Assert.Equal(0, process.ExitCode);
        return stdout.Result + stderr.Result;
    }

    /// Every field Set could have clobbered must round-trip unchanged. Age (and, in principle, the
    /// lifetimes) can tick between the two reads on a real system, so they are excluded.
    static void AssertRowUnchangedExceptMetricAndAge(IpHelperNative.MIB_IPFORWARD_ROW2 before, IpHelperNative.MIB_IPFORWARD_ROW2 after)
    {
        Assert.Equal(before.InterfaceLuid, after.InterfaceLuid);
        Assert.Equal(before.InterfaceIndex, after.InterfaceIndex);
        Assert.Equal(before.DestinationPrefix.PrefixLength, after.DestinationPrefix.PrefixLength);
        Assert.Equal(before.DestinationPrefix.Prefix.Family, after.DestinationPrefix.Prefix.Family);
        Assert.Equal(before.DestinationPrefix.Prefix.Ipv4Addr, after.DestinationPrefix.Prefix.Ipv4Addr);
        Assert.Equal(before.NextHop.Family, after.NextHop.Family);
        Assert.Equal(before.NextHop.Ipv4Addr, after.NextHop.Ipv4Addr);
        Assert.Equal(before.SitePrefixLength, after.SitePrefixLength);
        Assert.Equal(before.ValidLifetime, after.ValidLifetime);
        Assert.Equal(before.PreferredLifetime, after.PreferredLifetime);
        Assert.Equal(before.Protocol, after.Protocol);
        Assert.Equal(before.Loopback, after.Loopback);
        Assert.Equal(before.AutoconfigureAddress, after.AutoconfigureAddress);
        Assert.Equal(before.Publish, after.Publish);
        Assert.Equal(before.Immortal, after.Immortal);
        Assert.Equal(before.Origin, after.Origin);
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"PowerShell did not finish within 30 s. Script: {script}");
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Fail($"PowerShell exited with {process.ExitCode}. Stderr: {stderr.Result}\nScript: {script}");
        return stdout.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
