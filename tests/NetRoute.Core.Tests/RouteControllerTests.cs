namespace NetRoute.Core.Tests;

public class RouteControllerTests
{
    readonly FakeAdapterSource _adapters = new();
    readonly FakeInterfaceMetrics _metrics = new();
    readonly FakeRouteQuery _routes = new();
    readonly FakeLatencyProbe _probe = new();
    readonly List<AppSettings> _saved = new();
    readonly List<string> _toasts = new();

    public RouteControllerTests()
    {
        _adapters.Adapters.AddRange([TestAdapters.Phone(), TestAdapters.Lan()]);
        _metrics.Add(31);
        _metrics.Add(10);
        _routes.BestV4 = 31;
        _probe.BySource["192.168.42.11"] = 38;
        _probe.BySource["192.168.86.42"] = 12;
    }

    RouteController Create(AppSettings? settings = null, bool canModify = true)
    {
        var controller = new RouteController(_adapters, _metrics, _routes, _probe,
            settings ?? new AppSettings(), canModify, _saved.Add, _ => { });
        controller.AutoSwitched += _toasts.Add;
        return controller;
    }

    [Fact]
    public async Task First_refresh_applies_mode_and_reports_status_without_toast()
    {
        var controller = Create();
        NetworkStatus? published = null;
        controller.StatusChanged += s => published = s;

        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Equal(38, controller.Status.PhoneLatencyMs);
        Assert.Equal(12, controller.Status.LanLatencyMs);
        Assert.Null(controller.Status.Error);
        Assert.Same(controller.Status, published);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Refresh_when_in_sync_writes_nothing()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        var writes = _metrics.SetCalls.Count;

        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(writes, _metrics.SetCalls.Count);
    }

    [Fact]
    public async Task Phone_unplug_and_replug_switches_and_toasts()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);

        _adapters.Adapters.RemoveAll(a => a.Index == 31);
        _routes.BestV4 = 10;
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.True(controller.Status.IsFallback);
        Assert.Equal(new[] { "Phone disconnected — internet via LAN" }, _toasts);

        _adapters.Adapters.Add(TestAdapters.Phone(index: 36, description: "SAMSUNG Mobile USB Remote NDIS Network Device #2"));
        _metrics.Add(36);
        _routes.BestV4 = 36;
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(36, IpFamily.IPv4));
        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Equal("Phone back — internet via Phone", _toasts.Last());
    }

    [Fact]
    public async Task SetMode_applies_persists_and_does_not_toast()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _routes.BestV4 = 10;

        var result = await controller.SetModeAsync(RoutingMode.Lan);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(RoutingMode.Lan, controller.Status.Mode);
        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.Equal(RoutingMode.Lan, _saved.Last().Mode);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Path_settling_after_user_mode_change_does_not_toast()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await controller.SetModeAsync(RoutingMode.Lan); // route table still reports the phone

        _routes.BestV4 = 10; // ...and settles on the LAN a moment later
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Not_elevated_never_writes_metrics()
    {
        var controller = Create(canModify: false);

        await controller.RefreshAsync(measureLatency: true);
        var result = await controller.SetModeAsync(RoutingMode.Lan);

        Assert.False(result.Success);
        Assert.Contains("Administrator", result.Error);
        Assert.Empty(_metrics.SetCalls);
        Assert.Empty(_saved);
        Assert.Equal(RoutingMode.Phone, controller.Status.Mode);
        Assert.False(controller.Status.CanModify);
    }

    [Fact]
    public async Task Failed_SetMode_keeps_previous_mode_and_shows_error()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _metrics.FailOnSet.Add((10, IpFamily.IPv4));

        var result = await controller.SetModeAsync(RoutingMode.Lan);

        Assert.False(result.Success);
        Assert.Equal(RoutingMode.Phone, controller.Status.Mode);
        Assert.Equal(RoutingMode.Phone, controller.Settings.Mode);
        Assert.Contains("Access is denied.", controller.Status.Error);
        Assert.DoesNotContain(_saved, s => s.Mode == RoutingMode.Lan);
    }

    [Fact]
    public async Task Refresh_failure_is_reported_not_thrown()
    {
        var controller = Create();
        _adapters.Throw = new InvalidOperationException("boom");

        await controller.RefreshAsync(measureLatency: true);

        Assert.Contains("boom", controller.Status.Error);
    }

    [Fact]
    public async Task Ipv6_path_is_resolved_separately()
    {
        var controller = Create();
        _routes.BestV6 = 10;

        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Equal(InternetPath.Lan, controller.Status.Ipv6Path);
    }

    [Fact]
    public async Task Changing_lan_override_resets_previous_adapter_to_automatic()
    {
        _adapters.Adapters.Add(TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12"));
        _metrics.Add(12);
        var controller = Create(new AppSettings { LanOverrideMac = "AA-BB-CC-00-00-10" });
        await controller.RefreshAsync(measureLatency: true);
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));

        await controller.SetOverridesAsync(new AdapterOverrides(null, "AA-BB-CC-00-00-12"));

        Assert.True(_metrics.Get(10, IpFamily.IPv4)!.UseAutomatic);
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(12, IpFamily.IPv4));
        Assert.Equal(12, controller.Status.Adapters.Lan?.Index);
        Assert.Equal("AA-BB-CC-00-00-12", _saved.Last().LanOverrideMac);
    }

    [Fact]
    public async Task Latency_is_kept_between_probes_and_cleared_when_adapter_disappears()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);

        await controller.RefreshAsync(measureLatency: false);
        Assert.Equal(38, controller.Status.PhoneLatencyMs);

        _adapters.Adapters.RemoveAll(a => a.Index == 31);
        await controller.RefreshAsync(measureLatency: false);
        Assert.Null(controller.Status.PhoneLatencyMs);
    }

    [Fact]
    public async Task SetOverrides_serializes_reset_against_concurrent_refreshes()
    {
        // Setup: two LAN adapters with initial override pointing to index 10
        _adapters.Adapters.Add(TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12"));
        _metrics.Add(12);
        var blockingProbe = new BlockingLatencyProbe();
        blockingProbe.SetLatency("192.168.42.11", 38);
        blockingProbe.SetLatency("192.168.86.42", 12);
        var controller = new RouteController(_adapters, _metrics, _routes, blockingProbe,
            new AppSettings { LanOverrideMac = "AA-BB-CC-00-00-10" }, canModify: true, _saved.Add, _ => { });

        // First refresh establishes the baseline with index 10 as LAN with metric 50
        var refresh1Task = controller.RefreshAsync(measureLatency: true);
        await Task.Delay(50); // Let it enter the blocking probe

        // Start SetOverridesAsync to change to index 12 (will queue waiting for gate held by refresh1)
        var overridesTask = controller.SetOverridesAsync(new AdapterOverrides(null, "AA-BB-CC-00-00-12"));
        await Task.Delay(50); // Let it queue on the gate

        // Start a second refresh that will queue behind SetOverridesAsync
        var refresh2Task = controller.RefreshAsync(measureLatency: true);
        await Task.Delay(50);

        // Release the blocking probe, allowing all three operations to proceed in order:
        // refresh1 finishes, SetOverridesAsync resets index 10 and applies index 12, refresh2 completes
        blockingProbe.SetResult();
        await Task.WhenAll(refresh1Task, overridesTask, refresh2Task);

        // Verify the old LAN adapter (index 10) was reset to automatic
        Assert.True(_metrics.Get(10, IpFamily.IPv4)!.UseAutomatic,
            "Old LAN adapter (index 10) should be reset to automatic metric");
        // Verify the new LAN adapter (index 12) has the preferred metric
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(12, IpFamily.IPv4));
        // Verify the controller reports the new adapter
        Assert.Equal(12, controller.Status.Adapters.Lan?.Index);
        // Verify the settings were persisted
        Assert.Equal("AA-BB-CC-00-00-12", _saved.Last().LanOverrideMac);
    }
}
