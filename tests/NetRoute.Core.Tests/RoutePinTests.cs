namespace NetRoute.Core.Tests;

public class RoutePinTests
{
    static DetectionResult LanAndWifiOnPzhome(AdapterInfo? wifi = null) => DetectionResult.Of(
        TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, wifi ?? TestAdapters.WifiOnLan(), DetectionIssue.None);

    [Fact]
    public void Wifi_mode_on_the_lan_subnet_pins_the_lan_route() =>
        Assert.Equal((10, "192.168.86.0/24"), RoutePin.Wanted(RoutingMode.Wifi, null, LanAndWifiOnPzhome()));

    [Fact]
    public void Healing_onto_wifi_pins_too() =>
        Assert.Equal((10, "192.168.86.0/24"), RoutePin.Wanted(RoutingMode.Lan, ConnectionId.Wifi, LanAndWifiOnPzhome()));

    [Theory, InlineData(RoutingMode.Lan), InlineData(RoutingMode.Phone), InlineData(RoutingMode.Auto)]
    public void No_pin_when_lan_already_preferred_over_wifi(RoutingMode mode) =>
        Assert.Null(RoutePin.Wanted(mode, null, LanAndWifiOnPzhome()));

    [Fact]
    public void No_pin_when_wifi_is_on_another_subnet() =>
        Assert.Null(RoutePin.Wanted(RoutingMode.Wifi, null, LanAndWifiOnPzhome(TestAdapters.Wifi())));

    [Fact]
    public void Sync_applies_once_and_reapplies_after_drift()
    {
        var routes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var pin = new RoutePin(routes, _ => { });
        pin.Sync((10, "192.168.86.0/24"));
        pin.Sync((10, "192.168.86.0/24"));
        Assert.Single(routes.SetCalls);
        routes[(10, "192.168.86.0/24")] = 256;          // DHCP renewal put it back
        pin.Sync((10, "192.168.86.0/24"));
        Assert.Equal(2, routes.SetCalls.Count);
        Assert.Equal(0u, routes[(10, "192.168.86.0/24")]);
    }

    [Fact]
    public void Pin_is_removed_when_wifi_disappears()
    {
        var routes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var pin = new RoutePin(routes, _ => { });
        pin.Sync((10, "192.168.86.0/24"));
        pin.Sync(null);
        Assert.Equal(256u, routes[(10, "192.168.86.0/24")]);
    }

    [Fact]
    public void Revert_of_a_vanished_route_clears_state_without_writing_or_logging()
    {
        var routes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var logs = new List<string>();
        var pin = new RoutePin(routes, logs.Add);
        pin.Sync((10, "192.168.86.0/24"));
        routes.State.Remove((10, "192.168.86.0/24")); // the LAN cable was unplugged; Windows removed the route
        var setCallsBefore = routes.SetCalls.Count;
        logs.Clear();

        pin.Sync(null);

        Assert.Equal(setCallsBefore, routes.SetCalls.Count);
        Assert.Empty(logs);

        // A later pin request (e.g. after the LAN comes back on a different interface/subnet) still applies cleanly.
        routes[(18, "192.168.1.0/24")] = 256;
        pin.Sync((18, "192.168.1.0/24"));
        Assert.Equal(0u, routes[(18, "192.168.1.0/24")]);
    }

    [Fact]
    public void Release_reverts_and_a_failing_set_is_logged_not_thrown()
    {
        var routes = new FakeSubnetRoutes { [(10, "192.168.86.0/24")] = 256 };
        var logs = new List<string>();
        var pin = new RoutePin(routes, logs.Add);
        pin.Sync((10, "192.168.86.0/24"));
        Assert.Equal(0u, routes[(10, "192.168.86.0/24")]);

        pin.Release();
        Assert.Equal(256u, routes[(10, "192.168.86.0/24")]);

        logs.Clear();
        routes.FailOnSet = true;
        var ex = Record.Exception(() => pin.Sync((10, "192.168.86.0/24")));

        Assert.Null(ex);
        Assert.Single(logs);
    }
}
