namespace NetRoute.Core.Tests;

public class RouteControllerTests
{
    /// Checks needed to heal at the 5 s poll: the first failure starts the 60 s window, the 13th check closes it.
    const int HealChecks = 13;

    sealed class StepClock : TimeProvider
    {
        DateTimeOffset _now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    readonly StepClock _clock = new();
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

    RouteController Create(AppSettings? settings = null, bool canModify = true, INetworkNameSource? names = null)
    {
        var controller = new RouteController(_adapters, _metrics, _routes, _probe,
            settings ?? new AppSettings(), canModify, _saved.Add, _ => { }, _clock, names: names);
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
        // Carrier must agree with the Mode that actually stuck (Phone), not the rejected request (Lan): the
        // LAN adapter has a gateway and IPv4, so computing Carrier from the rejected mode would wrongly say Lan.
        Assert.Equal(ConnectionId.Phone, controller.Status.Carrier);
    }

    [Fact]
    public async Task Wifi_mode_pins_and_auto_releases()
    {
        var adapters = new FakeAdapterSource();
        adapters.Adapters.AddRange([TestAdapters.Lan(), TestAdapters.WifiOnLan()]);
        var metrics = new FakeInterfaceMetrics();
        metrics.Add(10);
        metrics.Add(18);
        var routes = new FakeRouteQuery { BestV4 = 10 };
        var subnetRoutes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var settings = new AppSettings { WifiOverrideMac = TestAdapters.WifiOnLan().Mac };

        var controller = new RouteController(adapters, metrics, routes, new FakeLatencyProbe(),
            settings, canModify: true, _saved.Add, _ => { }, _clock, subnetRoutes);

        await controller.SetModeAsync(RoutingMode.Wifi);
        Assert.Equal(0u, subnetRoutes[(10, "192.168.86.0/24")]);

        await controller.SetModeAsync(RoutingMode.Auto);
        Assert.Equal(256u, subnetRoutes[(10, "192.168.86.0/24")]);
    }

    /// ReleasePin must not touch the same RoutePin instance while a RefreshAsync already holds _gate
    /// and is itself about to call RoutePin.Sync - that would be two unsynchronized writers racing on
    /// RoutePin._pinned. Blocks a refresh mid-flight on the latency probe (which _gate is held across),
    /// fires ReleasePin concurrently, and checks it waits rather than running immediately.
    [Fact]
    public async Task ReleasePin_waits_for_the_gate_instead_of_racing_a_refresh()
    {
        var adapters = new FakeAdapterSource();
        adapters.Adapters.AddRange([TestAdapters.Lan(), TestAdapters.WifiOnLan()]);
        var metrics = new FakeInterfaceMetrics();
        metrics.Add(10);
        metrics.Add(18);
        var routes = new FakeRouteQuery { BestV4 = 10 };
        var probe = new BlockingLatencyProbe();
        probe.SetLatency("192.168.86.42", 10);
        probe.SetLatency("192.168.86.35", 20);
        var subnetRoutes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var settings = new AppSettings { Mode = RoutingMode.Wifi, WifiOverrideMac = TestAdapters.WifiOnLan().Mac };

        var controller = new RouteController(adapters, metrics, routes, probe,
            settings, canModify: true, _ => { }, _ => { }, _clock, subnetRoutes);

        var refreshTask = controller.RefreshAsync(measureLatency: true); // enters _gate, then parks inside the probe
        await Task.Delay(200); // let the refresh actually reach and block on the probe

        var releaseTask = Task.Run(() => controller.ReleasePin());
        await Task.Delay(200); // ReleasePin must be blocked on the same gate, not racing ahead

        Assert.False(releaseTask.IsCompleted, "ReleasePin ran without waiting for the in-flight refresh's gate");
        Assert.Equal(256u, subnetRoutes[(10, "192.168.86.0/24")]); // refresh hasn't pinned yet - still parked on the probe

        probe.Unblock();
        await refreshTask;
        await releaseTask;

        // The refresh (holding the gate first) pinned the route; ReleasePin only ran once the gate freed, and reverted it.
        Assert.Equal(256u, subnetRoutes[(10, "192.168.86.0/24")]);
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
        // Two LAN adapters; the saved override points at index 10.
        _adapters.Adapters.Add(TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12"));
        _metrics.Add(12);
        var probe = new BlockingLatencyProbe();
        probe.SetLatency("192.168.42.11", 38);
        probe.SetLatency("192.168.86.42", 12);
        var controller = new RouteController(_adapters, _metrics, _routes, probe,
            new AppSettings { LanOverrideMac = "AA-BB-CC-00-00-10" }, canModify: true, _saved.Add, _ => { });

        await controller.RefreshAsync(measureLatency: false);
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));

        // r1 takes the gate and parks inside the blocked latency probe, still holding the gate.
        var r1 = controller.RefreshAsync(measureLatency: true);
        // r2 queues on the gate, ahead of SetOverridesAsync.
        var r2 = controller.RefreshAsync(measureLatency: false);
        // SetOverridesAsync queues behind r2. If it saved the new override before taking the gate,
        // r2 would detect with it and publish adapter 12 as LAN, so adapter 10 would never be reset.
        var so = controller.SetOverridesAsync(new AdapterOverrides(null, "AA-BB-CC-00-00-12"));

        probe.Unblock();
        await Task.WhenAll(r1, r2, so).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(_metrics.Get(10, IpFamily.IPv4)!.UseAutomatic,
            "Old LAN adapter (index 10) must be reset to automatic by SetOverridesAsync");
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(12, IpFamily.IPv4));
    }

    async Task RefreshTimes(RouteController controller, int times)
    {
        for (var i = 0; i < times; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(5)); // the visible poll interval
            await controller.RefreshAsync(measureLatency: true);
        }
    }

    [Fact]
    public async Task Phone_without_internet_heals_to_lan_after_sixty_seconds_of_failed_checks()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;

        await RefreshTimes(controller, HealChecks - 1);
        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));

        _routes.BestV4 = 10; // Windows follows the swapped metrics
        await RefreshTimes(controller, 1);

        Assert.True(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(RoutingMode.Phone, controller.Status.Mode);
        Assert.Equal(RoutingMode.Phone, controller.Settings.Mode);
        Assert.Empty(_saved);
        Assert.Equal("Phone lost internet — internet via LAN", _toasts.Last());
    }

    [Fact]
    public async Task Healing_returns_to_phone_after_three_good_checks()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;
        _routes.BestV4 = 10;
        await RefreshTimes(controller, HealChecks);
        Assert.True(controller.Status.IsHealing);

        _probe.BySource["192.168.42.11"] = 38;
        await RefreshTimes(controller, 2);
        Assert.True(controller.Status.IsHealing);

        _routes.BestV4 = 31;
        await controller.RefreshAsync(measureLatency: true);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal("Phone back — internet via Phone", _toasts.Last());
    }

    [Fact]
    public async Task No_heal_when_the_backup_has_no_internet_either()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;
        _probe.BySource["192.168.86.42"] = null;

        await RefreshTimes(controller, 5);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
    }

    [Fact]
    public async Task A_good_check_resets_the_failure_count()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);

        foreach (var phoneMs in new int?[] { null, null, 38, null, null })
        {
            _probe.BySource["192.168.42.11"] = phoneMs;
            await controller.RefreshAsync(measureLatency: true);
        }

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
    }

    [Fact]
    public async Task One_missed_latency_check_keeps_the_last_latency_on_the_card()
    {
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan });
        await controller.RefreshAsync(measureLatency: true);
        Assert.Equal(12, controller.Status.LanLatencyMs);

        _probe.BySource["192.168.86.42"] = null;
        await RefreshTimes(controller, 1);
        Assert.Equal(12, controller.Status.LanLatencyMs); // a blip is not "no reply"

        await RefreshTimes(controller, 1);
        Assert.Null(controller.Status.LanLatencyMs); // two misses in a row are

        _probe.BySource["192.168.86.42"] = 15;
        await RefreshTimes(controller, 1);
        Assert.Equal(15, controller.Status.LanLatencyMs);
    }

    [Fact]
    public async Task A_slow_but_answering_preferred_adapter_is_not_a_failed_check()
    {
        _routes.BestV4 = 10;
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = 1500; // congested LAN: slow handshakes, traffic still flows

        await RefreshTimes(controller, HealChecks * 2);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(1500, controller.Status.LanLatencyMs);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(10, IpFamily.IPv4));
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task A_blip_shorter_than_the_window_never_heals()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);

        for (var round = 0; round < 5; round++)
        {
            _probe.BySource["192.168.42.11"] = null;
            await RefreshTimes(controller, HealChecks - 1); // 55 s of failures, just under the window
            _probe.BySource["192.168.42.11"] = 38;
            await RefreshTimes(controller, 1);
        }

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task A_long_gap_between_two_failed_checks_is_not_enough_to_heal()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;

        await controller.RefreshAsync(measureLatency: true);
        _clock.Advance(TimeSpan.FromMinutes(10)); // e.g. the PC slept
        await controller.RefreshAsync(measureLatency: true);

        Assert.False(controller.Status.IsHealing);
    }

    [Fact]
    public async Task A_long_gap_between_checks_restarts_the_failure_window_so_waking_from_sleep_never_heals()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;
        await RefreshTimes(controller, 2); // a 10 s blip...
        _clock.Advance(TimeSpan.FromMinutes(10)); // ...then the PC sleeps

        await RefreshTimes(controller, 1); // the first check after waking, still a miss
        Assert.False(controller.Status.IsHealing);

        await RefreshTimes(controller, HealChecks - 2); // the window runs from the wake-up
        Assert.False(controller.Status.IsHealing);
        await RefreshTimes(controller, 1);
        Assert.True(controller.Status.IsHealing);
    }

    [Fact]
    public async Task Auto_heal_off_never_heals()
    {
        var controller = Create(new AppSettings { AutoHeal = false });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;

        await RefreshTimes(controller, HealChecks * 3);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public async Task Switching_auto_heal_off_during_a_heal_returns_to_the_saved_mode()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;
        await RefreshTimes(controller, HealChecks);
        Assert.True(controller.Status.IsHealing);

        controller.UpdateSettings(s => s with { AutoHeal = false });
        await RefreshTimes(controller, 1);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public async Task Lan_mode_heals_to_phone()
    {
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null;

        await RefreshTimes(controller, HealChecks);

        Assert.True(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(RoutingMode.Lan, controller.Settings.Mode);
    }

    [Fact]
    public async Task Auto_mode_and_read_only_never_heal()
    {
        var auto = Create(new AppSettings { Mode = RoutingMode.Auto });
        await auto.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;
        await RefreshTimes(auto, 5);
        Assert.False(auto.Status.IsHealing);
        Assert.All(_metrics.State.Values, s => Assert.True(s.UseAutomatic));

        var readOnly = Create(canModify: false);
        await RefreshTimes(readOnly, 5);
        Assert.False(readOnly.Status.IsHealing);
        Assert.DoesNotContain(_metrics.SetCalls, c => c.Metric is not null);
    }

    [Fact]
    public async Task User_mode_change_cancels_healing()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;
        await RefreshTimes(controller, HealChecks);
        Assert.True(controller.Status.IsHealing);

        await controller.SetModeAsync(RoutingMode.Phone);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public async Task Healing_ends_when_the_backup_disappears()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.42.11"] = null;
        await RefreshTimes(controller, HealChecks);
        Assert.True(controller.Status.IsHealing);

        _adapters.Adapters.RemoveAll(a => a.Index == 10);
        await controller.RefreshAsync(measureLatency: true);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
    }

    [Fact]
    public async Task Preferred_adapter_without_an_address_yet_does_not_count_as_failed()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        _adapters.Adapters.RemoveAll(a => a.Index == 31);
        _adapters.Adapters.Add(TestAdapters.Phone(index: 36) with { IPv4 = null }); // replugged, DHCP pending
        _metrics.Add(36);

        await RefreshTimes(controller, 5);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(36, IpFamily.IPv4));
    }

    async Task HealPhone(RouteController controller)
    {
        _probe.BySource["192.168.42.11"] = null;
        await RefreshTimes(controller, HealChecks);
        Assert.True(controller.Status.IsHealing);
    }

    async Task RecoverPhone(RouteController controller, int goodChecks)
    {
        _probe.BySource["192.168.42.11"] = 38;
        await RefreshTimes(controller, goodChecks);
    }

    [Fact]
    public async Task Repeated_heal_doubles_the_wait_before_switching_back()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await HealPhone(controller);
        await RecoverPhone(controller, 3);
        Assert.False(controller.Status.IsHealing);

        await HealPhone(controller); // fails again soon after recovering
        Assert.Equal(6, controller.RecoverThreshold);

        await RecoverPhone(controller, 5);
        Assert.True(controller.Status.IsHealing);
        await RecoverPhone(controller, 1);
        Assert.False(controller.Status.IsHealing);
    }

    [Fact]
    public async Task Backoff_resets_after_a_calm_period()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await HealPhone(controller);
        await RecoverPhone(controller, 3);
        await HealPhone(controller);
        await RecoverPhone(controller, 6);
        Assert.False(controller.Status.IsHealing);

        await RecoverPhone(controller, RouteController.CalmChecksToResetBackoff);
        Assert.Equal(RouteController.RecoverAfterGoodChecks, controller.RecoverThreshold);

        await HealPhone(controller);
        await RecoverPhone(controller, 3);
        Assert.False(controller.Status.IsHealing);
    }

    [Fact]
    public async Task Backoff_is_capped()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        for (var i = 0; i < 8; i++)
        {
            await HealPhone(controller);
            await RecoverPhone(controller, controller.RecoverThreshold);
        }

        Assert.Equal(RouteController.MaxRecoverAfterGoodChecks, controller.RecoverThreshold);
    }

    [Fact]
    public async Task Healing_ends_at_once_when_the_backup_dies_and_the_preferred_answers()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await HealPhone(controller);

        _probe.BySource["192.168.86.42"] = null;
        _probe.BySource["192.168.42.11"] = 38;
        await controller.RefreshAsync(measureLatency: true);

        Assert.False(controller.Status.IsHealing);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
    }

    /// Heal and recover until the wait before switching back is at its cap; ends not healing.
    async Task ReachMaxBackoff(RouteController controller)
    {
        do
        {
            await HealPhone(controller);
            await RecoverPhone(controller, controller.RecoverThreshold);
        } while (controller.RecoverThreshold < RouteController.MaxRecoverAfterGoodChecks);
        Assert.False(controller.Status.IsHealing);
    }

    [Fact]
    public async Task Heal_at_the_backoff_cap_is_sticky()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await ReachMaxBackoff(controller);

        await HealPhone(controller);
        Assert.True(controller.Status.IsHealSticky);

        await RecoverPhone(controller, 100);
        Assert.True(controller.Status.IsHealing);
        Assert.True(controller.Status.IsHealSticky);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public async Task User_mode_change_clears_a_sticky_heal()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await ReachMaxBackoff(controller);
        await HealPhone(controller);
        Assert.True(controller.Status.IsHealSticky);

        await controller.SetModeAsync(RoutingMode.Phone);

        Assert.False(controller.Status.IsHealing);
        Assert.False(controller.Status.IsHealSticky);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
    }

    [Fact]
    public async Task A_dead_backup_ends_a_sticky_heal()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await ReachMaxBackoff(controller);
        await HealPhone(controller);
        Assert.True(controller.Status.IsHealSticky);

        _probe.BySource["192.168.86.42"] = null;
        _probe.BySource["192.168.42.11"] = 38;
        await controller.RefreshAsync(measureLatency: true);

        Assert.False(controller.Status.IsHealing);
        Assert.False(controller.Status.IsHealSticky);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(31, IpFamily.IPv4));
    }

    [Fact]
    public async Task Entering_a_sticky_heal_raises_one_toast()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await ReachMaxBackoff(controller);
        _probe.BySource["192.168.42.11"] = null;
        await RefreshTimes(controller, HealChecks - 1);

        _routes.BestV4 = 10; // Windows follows the swapped metrics, so the path changes in this refresh
        _toasts.Clear();
        await RefreshTimes(controller, 1);

        Assert.True(controller.Status.IsHealSticky);
        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.Equal(new[] { "Phone keeps losing internet — staying on LAN. Click Phone to retry." }, _toasts);
    }

    [Fact]
    public async Task User_mode_change_resets_the_backoff()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        await HealPhone(controller);
        await RecoverPhone(controller, 3);
        await HealPhone(controller);
        Assert.Equal(6, controller.RecoverThreshold);

        await controller.SetModeAsync(RoutingMode.Phone);

        Assert.Equal(RouteController.RecoverAfterGoodChecks, controller.RecoverThreshold);
    }

    [Fact]
    public async Task Tun_interface_is_reported_as_the_smart_routing_exit()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        controller.ExternalPathResolver = index => index == 97 ? InternetPath.Phone : null;

        _routes.BestV4 = 97; // sing-box TUN became the best route
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Tun_interface_is_reported_for_ipv6_too()
    {
        var controller = Create();
        controller.ExternalPathResolver = index => index == 97 ? InternetPath.Phone : null;

        _routes.BestV6 = 97;
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Phone, controller.Status.Ipv6Path);
    }

    [Fact]
    public async Task Set_mode_uses_the_external_path_resolver()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);
        controller.ExternalPathResolver = index => index == 97 ? InternetPath.Phone : null;

        _routes.BestV4 = 97;
        await controller.SetModeAsync(RoutingMode.Phone);

        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
    }

    [Fact]
    public async Task Tun_disappearing_while_the_phone_is_best_raises_no_toast()
    {
        var controller = Create();
        controller.ExternalPathResolver = index => index == 97 ? InternetPath.Phone : null;
        _routes.BestV4 = 97;
        await controller.RefreshAsync(measureLatency: true);

        _routes.BestV4 = 31; // TUN gone, the phone adapter carries traffic directly
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Phone, controller.Status.ActivePath);
        Assert.Empty(_toasts);
    }

    [Fact]
    public async Task Tun_exiting_via_lan_while_phone_mode_is_reported_as_a_fallback()
    {
        var controller = Create(new AppSettings { Mode = RoutingMode.Phone });
        controller.ExternalPathResolver = index => index == 97 ? InternetPath.Lan : null;

        _routes.BestV4 = 97;
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(InternetPath.Lan, controller.Status.ActivePath);
        Assert.True(controller.Status.IsFallback);
    }

    [Fact]
    public async Task Plain_unplug_of_the_only_other_connection_present_does_not_spuriously_heal()
    {
        var controller = Create();
        await controller.RefreshAsync(measureLatency: true);

        _adapters.Adapters.RemoveAll(a => a.Index == 31); // main (Phone) adapter entirely gone, not just a failed probe
        _routes.BestV4 = 10; // Windows already routes via LAN on its own, no heal needed

        await RefreshTimes(controller, HealChecks * 2); // well past the usual 60 s / 3-check window

        Assert.False(controller.Status.IsHealing);
        Assert.Null(controller.Status.HealTarget);
        // LAN already carries via plain metric fallback (graded Backup, never promoted to Preferred by a heal).
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
    }

    /// Adds the third, Wi-Fi connection to the fixture: present, with an IPv4 and a 20 ms probe reply.
    void AddWifi()
    {
        _adapters.Adapters.Add(TestAdapters.Wifi());
        _metrics.Add(18);
        _probe.BySource["192.168.1.20"] = 20;
    }

    [Fact]
    public async Task Lan_failing_heals_to_wifi_not_phone()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null;

        await RefreshTimes(controller, HealChecks);

        Assert.True(controller.Status.IsHealing);
        Assert.Equal(ConnectionId.Wifi, controller.Status.HealTarget);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(18, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 50), _metrics.Get(10, IpFamily.IPv4));
        Assert.Equal(new InterfaceMetricState(false, 60), _metrics.Get(31, IpFamily.IPv4));
        // Note: ToastFor (unchanged in this task) does not yet name Wi-Fi for a non-Phone/Lan path;
        // that generic rewrite is Task 10. Not asserting toast text here.
    }

    [Fact]
    public async Task Wifi_target_dying_steps_down_to_phone_at_once()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null;
        await RefreshTimes(controller, HealChecks);
        Assert.Equal(ConnectionId.Wifi, controller.Status.HealTarget);

        _probe.BySource["192.168.1.20"] = null; // Wi-Fi, the current heal target, dies
        await RefreshTimes(controller, 1);

        Assert.True(controller.Status.IsHealing);
        Assert.Equal(ConnectionId.Phone, controller.Status.HealTarget);
    }

    [Fact]
    public async Task Wifi_back_steps_up_after_three_good_checks()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null; // LAN (main) fails
        _probe.BySource["192.168.1.20"] = null;  // Wi-Fi is dead at the moment healing starts
        await RefreshTimes(controller, HealChecks);
        Assert.Equal(ConnectionId.Phone, controller.Status.HealTarget);

        _probe.BySource["192.168.1.20"] = 20; // Wi-Fi answers again
        await RefreshTimes(controller, 2);
        Assert.Equal(ConnectionId.Phone, controller.Status.HealTarget);

        await RefreshTimes(controller, 1);
        Assert.Equal(ConnectionId.Wifi, controller.Status.HealTarget);
    }

    [Fact]
    public async Task Step_moves_do_not_grow_the_backoff()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null;
        await RefreshTimes(controller, HealChecks);
        Assert.Equal(ConnectionId.Wifi, controller.Status.HealTarget);
        Assert.Equal(RouteController.RecoverAfterGoodChecks, controller.RecoverThreshold);

        _probe.BySource["192.168.1.20"] = null; // step down
        await RefreshTimes(controller, 1);
        Assert.True(controller.Status.IsHealing);
        Assert.Equal(ConnectionId.Phone, controller.Status.HealTarget);
        Assert.Equal(RouteController.RecoverAfterGoodChecks, controller.RecoverThreshold);

        _probe.BySource["192.168.1.20"] = 20; // step up, three good checks later
        await RefreshTimes(controller, 3);
        Assert.True(controller.Status.IsHealing);
        Assert.Equal(ConnectionId.Wifi, controller.Status.HealTarget);
        Assert.Equal(RouteController.RecoverAfterGoodChecks, controller.RecoverThreshold);
    }

    [Fact]
    public async Task No_candidate_answering_means_no_heal()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null;
        _probe.BySource["192.168.1.20"] = null;
        _probe.BySource["192.168.42.11"] = null;

        await RefreshTimes(controller, HealChecks * 2);

        Assert.False(controller.Status.IsHealing);
        Assert.Null(controller.Status.HealTarget);
    }

    [Fact]
    public async Task Heal_skips_wifi_without_ipv4()
    {
        _adapters.Adapters.Add(TestAdapters.Wifi() with { IPv4 = null }); // present, but DHCP never answered
        _metrics.Add(18);
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null;

        await RefreshTimes(controller, HealChecks);

        Assert.True(controller.Status.IsHealing);
        Assert.Equal(ConnectionId.Phone, controller.Status.HealTarget);
    }

    [Fact]
    public async Task Wifi_main_unplugged_heals_to_lan_then_phone()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Wifi, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);

        _adapters.Adapters.RemoveAll(a => a.Index == 18); // Wi-Fi, the main connection, unplugged
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(ConnectionId.Lan, controller.Status.Carrier); // metric fallback order, no heal needed yet
        Assert.Null(controller.Status.HealTarget);

        _probe.BySource["192.168.86.42"] = null; // now LAN fails too, for the full window
        await RefreshTimes(controller, HealChecks);

        Assert.Equal(ConnectionId.Phone, controller.Status.HealTarget);
    }

    [Fact]
    public async Task Lan_recovering_ends_the_heal_from_wifi()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Lan, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);
        _probe.BySource["192.168.86.42"] = null;
        await RefreshTimes(controller, HealChecks);
        Assert.Equal(ConnectionId.Wifi, controller.Status.HealTarget);

        _probe.BySource["192.168.86.42"] = 12; // LAN (main) answers again
        await RefreshTimes(controller, RouteController.RecoverAfterGoodChecks);

        Assert.False(controller.Status.IsHealing);
        Assert.Null(controller.Status.HealTarget);
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public async Task Choosing_not_used_for_wifi_resets_its_metric()
    {
        AddWifi();
        var controller = Create(new AppSettings { WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);

        await controller.SetOverridesAsync(new AdapterOverrides(null, null, AdapterOverrides.NotUsed));

        Assert.Contains((18, IpFamily.IPv4, (uint?)null), _metrics.SetCalls);
    }

    [Fact]
    public async Task Setting_wifi_to_not_used_while_in_wifi_mode_falls_back()
    {
        AddWifi();
        var controller = Create(new AppSettings { Mode = RoutingMode.Wifi, WifiOverrideMac = "AA-BB-CC-00-00-18" });
        await controller.RefreshAsync(measureLatency: true);

        // Wi-Fi was Mode's connection; setting it to Not used (Phone/LAN left as they were) must not strand
        // the user on a mode with nothing behind it. FallbackOrder = [Lan, Wifi, Phone] and LAN is present
        // in this fixture, so it falls back to LAN.
        await controller.SetOverridesAsync(new AdapterOverrides(null, null, AdapterOverrides.NotUsed));

        Assert.Equal(RoutingMode.Lan, controller.Settings.Mode);
        Assert.Equal(RoutingMode.Lan, controller.Status.Mode);
        Assert.Equal(RoutingMode.Lan, _saved.Last().Mode);
        // The new mode's metrics were applied on this same SetOverridesAsync call, not left for a later refresh.
        Assert.Equal(new InterfaceMetricState(false, 5), _metrics.Get(10, IpFamily.IPv4));
    }

    [Fact]
    public async Task Wifi_network_name_is_published()
    {
        AddWifi();
        var names = new FakeNetworkNameSource { Result = "pzhome" };
        var controller = Create(new AppSettings { WifiOverrideMac = "AA-BB-CC-00-00-18" }, names: names);

        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal("pzhome", controller.Status.WifiNetwork);
    }

    [Fact]
    public async Task Name_is_looked_up_again_only_after_a_reconnect()
    {
        AddWifi();
        var names = new FakeNetworkNameSource { Result = "pzhome" };
        var controller = Create(new AppSettings { WifiOverrideMac = "AA-BB-CC-00-00-18" }, names: names);

        await controller.RefreshAsync(measureLatency: true);
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(1, names.CallCount);

        _adapters.Adapters.RemoveAll(a => a.Index == 18);
        _adapters.Adapters.Add(TestAdapters.Wifi() with { IPv4 = "192.168.1.99" }); // simulated reconnect
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(2, names.CallCount);
    }

    /// Windows naming the connection profile can race behind DHCP handing out the IPv4, especially
    /// right after this app auto-starts with Windows. A null lookup must not stick for the rest of
    /// the session (until an actual reconnect changes the IPv4): the next poll has to try again.
    [Fact]
    public async Task Null_lookup_result_is_retried_on_the_next_poll_not_cached()
    {
        AddWifi();
        var names = new FakeNetworkNameSource { Results = new Queue<string?>(new string?[] { null, "pzhome" }) };
        var controller = Create(new AppSettings { WifiOverrideMac = "AA-BB-CC-00-00-18" }, names: names);

        await controller.RefreshAsync(measureLatency: true);
        Assert.Equal(1, names.CallCount);
        Assert.Null(controller.Status.WifiNetwork);

        // Same Wi-Fi adapter, same (Index, IPv4) - no reconnect simulated.
        await controller.RefreshAsync(measureLatency: true);

        Assert.Equal(2, names.CallCount);
        Assert.Equal("pzhome", controller.Status.WifiNetwork);
    }

    /// Regression for a round-trip through a different key: K1 resolves to a real name, K2 (e.g. a DHCP
    /// renewal or reconnect briefly changing the IPv4) resolves to null, then the key goes back to K1
    /// (Wi-Fi present throughout - not a disconnect). Leaving the cache key at K1 across the null lookup
    /// at K2 would make the later return to K1 match the stale cache entry and short-circuit on the
    /// cached null forever, instead of re-querying.
    [Fact]
    public async Task Null_result_does_not_poison_a_later_return_to_a_previously_good_key()
    {
        AddWifi(); // index 18, IPv4 "192.168.1.20" (K1)
        var names = new FakeNetworkNameSource { Results = new Queue<string?>(new string?[] { "pzhome", null, "pzhome" }) };
        var controller = Create(new AppSettings { WifiOverrideMac = "AA-BB-CC-00-00-18" }, names: names);

        await controller.RefreshAsync(measureLatency: true); // K1 -> "pzhome"
        Assert.Equal(1, names.CallCount);
        Assert.Equal("pzhome", controller.Status.WifiNetwork);

        _adapters.Adapters.RemoveAll(a => a.Index == 18);
        _adapters.Adapters.Add(TestAdapters.Wifi() with { IPv4 = "192.168.1.99" }); // K2
        await controller.RefreshAsync(measureLatency: true); // K2 -> null
        Assert.Equal(2, names.CallCount);
        Assert.Null(controller.Status.WifiNetwork);

        _adapters.Adapters.RemoveAll(a => a.Index == 18);
        _adapters.Adapters.Add(TestAdapters.Wifi()); // back to K1 ("192.168.1.20") - Wi-Fi never dropped
        await controller.RefreshAsync(measureLatency: true); // K1 again -> must re-query, not short-circuit

        Assert.Equal(3, names.CallCount);
        Assert.Equal("pzhome", controller.Status.WifiNetwork);
    }
}
