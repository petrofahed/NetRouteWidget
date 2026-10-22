namespace NetRoute.Core.Tests;

public class RoutingEngineTests
{
    static DetectionResult Both(AdapterInfo? phone = null) =>
        DetectionResult.Of(phone ?? TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None);

    static DetectionResult All3(AdapterInfo? wifi = null) => DetectionResult.Of(
        TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, wifi ?? TestAdapters.Wifi(), DetectionIssue.None);

    static FakeInterfaceMetrics MetricsFor(params int[] indexes)
    {
        var metrics = new FakeInterfaceMetrics();
        foreach (var i in indexes) metrics.Add(i);
        return metrics;
    }

    [Fact]
    public void Phone_mode_prefers_phone_on_both_ip_families()
    {
        var metrics = MetricsFor(31, 10);

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.True(result.Success, result.Error);
        Assert.Equal(new InterfaceMetricState(false, 5), metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 5), metrics.Get(31, IpFamily.IPv6));
        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(10, IpFamily.IPv6));
    }

    [Fact]
    public void Lan_mode_prefers_lan()
    {
        var metrics = MetricsFor(31, 10);

        new RoutingEngine(metrics).Apply(RoutingMode.Lan, Both());

        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 5), metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public void Auto_mode_restores_automatic_metrics()
    {
        var metrics = MetricsFor(31, 10);
        var engine = new RoutingEngine(metrics);
        engine.Apply(RoutingMode.Phone, Both());

        var result = engine.Apply(RoutingMode.Auto, Both());

        Assert.True(result.Success, result.Error);
        Assert.All(metrics.State.Values, s => Assert.True(s.UseAutomatic));
    }

    [Fact]
    public void Interface_without_ipv6_is_skipped()
    {
        var metrics = new FakeInterfaceMetrics();
        metrics.Add(31, ipv6: false);
        metrics.Add(10);

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(metrics.SetCalls, c => c.Index == 31 && c.Family == IpFamily.IPv6);
    }

    [Fact]
    public void Missing_phone_only_touches_lan()
    {
        var metrics = MetricsFor(10);
        var lanOnly = DetectionResult.Of(null, DetectionIssue.NotFound, TestAdapters.Lan(), DetectionIssue.None);

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, lanOnly);

        Assert.True(result.Success, result.Error);
        Assert.All(metrics.SetCalls, c => Assert.Equal(10, c.Index));
    }

    [Fact]
    public void Failure_on_one_interface_is_reported_and_the_rest_still_applied()
    {
        var metrics = MetricsFor(31, 10);
        metrics.FailOnSet.Add((31, IpFamily.IPv4));

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.False(result.Success);
        Assert.Contains("Ethernet 31 IPv4", result.Error);
        Assert.Contains("Access is denied.", result.Error);
        Assert.Equal(new InterfaceMetricState(false, 50), metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public void Metric_that_does_not_stick_is_reported()
    {
        var metrics = MetricsFor(31, 10);
        metrics.IgnoreSet.Add((10, IpFamily.IPv4));

        var result = new RoutingEngine(metrics).Apply(RoutingMode.Phone, Both());

        Assert.False(result.Success);
        Assert.Contains("did not stick", result.Error);
    }

    [Fact]
    public void IsInSync_detects_drift_after_phone_replug()
    {
        var metrics = MetricsFor(31, 10);
        var engine = new RoutingEngine(metrics);
        engine.Apply(RoutingMode.Phone, Both());
        Assert.True(engine.IsInSync(RoutingMode.Phone, Both()));

        metrics.Add(36); // re-plugged phone comes back with an automatic metric
        var replugged = Both(TestAdapters.Phone(index: 36));
        Assert.False(engine.IsInSync(RoutingMode.Phone, replugged));

        engine.Apply(RoutingMode.Phone, replugged);
        Assert.True(engine.IsInSync(RoutingMode.Phone, replugged));
    }

    [Fact]
    public void Reset_restores_automatic_metric_on_both_families()
    {
        var metrics = MetricsFor(31, 10);
        var engine = new RoutingEngine(metrics);
        engine.Apply(RoutingMode.Phone, Both());

        var result = engine.Reset(TestAdapters.Lan());

        Assert.True(result.Success, result.Error);
        Assert.True(metrics.Get(10, IpFamily.IPv4)!.UseAutomatic);
        Assert.True(metrics.Get(10, IpFamily.IPv6)!.UseAutomatic);
    }

    [Theory]
    [InlineData(RoutingMode.Phone, 5u, 50u, 60u)]
    [InlineData(RoutingMode.Lan, 60u, 5u, 50u)]
    [InlineData(RoutingMode.Wifi, 60u, 50u, 5u)]
    public void Every_mode_grades_the_backups_in_fallback_order(RoutingMode mode, uint phone, uint lan, uint wifi)
    {
        var metrics = MetricsFor(31, 10, 18);
        new RoutingEngine(metrics).Apply(mode, All3());
        Assert.Equal(new InterfaceMetricState(false, phone), metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, lan), metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, wifi), metrics.Get(18, IpFamily.IPv4));
    }

    [Fact]
    public void Carrier_overrides_the_mode_while_healing()
    {
        var metrics = MetricsFor(31, 10, 18);
        new RoutingEngine(metrics).Apply(RoutingMode.Lan, All3(), carrier: ConnectionId.Wifi);
        Assert.Equal(5u, metrics.Get(18, IpFamily.IPv4)!.Metric);
        Assert.Equal(50u, metrics.Get(10, IpFamily.IPv4)!.Metric);
        Assert.Equal(60u, metrics.Get(31, IpFamily.IPv4)!.Metric);
    }

    [Fact]
    public void Auto_returns_all_three_to_automatic()
    {
        var metrics = MetricsFor(31, 10, 18);
        new RoutingEngine(metrics).Apply(RoutingMode.Auto, All3());
        Assert.All(new[] { 31, 10, 18 }, i => Assert.True(metrics.Get(i, IpFamily.IPv4)!.UseAutomatic));
    }

    [Fact]
    public void Unused_wifi_is_never_touched()
    {
        var metrics = MetricsFor(31, 10, 18);
        new RoutingEngine(metrics).Apply(RoutingMode.Phone, DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None));
        Assert.DoesNotContain(metrics.SetCalls, c => c.Index == 18);
    }
}
