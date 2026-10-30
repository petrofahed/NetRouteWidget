namespace NetRoute.Core.Tests;

public class StatusPresenterTests
{
    static NetworkStatus Status(
        RoutingMode mode = RoutingMode.Phone, InternetPath path = InternetPath.Phone,
        bool phone = true, bool lan = true, int? phoneMs = 38, int? lanMs = 12,
        string? error = null, InternetPath ipv6 = InternetPath.None,
        DetectionIssue missingLanIssue = DetectionIssue.NotFound) =>
        new(mode,
            DetectionResult.Of(
                phone ? TestAdapters.Phone() : null, phone ? DetectionIssue.None : DetectionIssue.NotFound,
                lan ? TestAdapters.Lan() : null, lan ? DetectionIssue.None : missingLanIssue),
            path, ipv6, new Dictionary<ConnectionId, int?> { [ConnectionId.Phone] = phone ? phoneMs : null, [ConnectionId.Lan] = lan ? lanMs : null },
            CanModify: true, error);

    /// Three present adapters (Phone 38ms, LAN 12ms, Wi-Fi 20ms), ActivePath set to the mode's path.
    static NetworkStatus StatusWith3(RoutingMode mode) =>
        new(mode,
            DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, TestAdapters.Wifi(), DetectionIssue.None),
            Connections.Of(mode) is { } id ? Connections.PathOf(id) : InternetPath.None,
            InternetPath.None,
            new Dictionary<ConnectionId, int?> { [ConnectionId.Phone] = 38, [ConnectionId.Lan] = 12, [ConnectionId.Wifi] = 20 },
            CanModify: true, null);

    [Fact]
    public void Normal_phone_mode()
    {
        var view = StatusPresenter.Present(Status());

        Assert.Equal("Internet via PHONE", view.Header);
        Assert.False(view.HeaderWarning);
        Assert.Null(view.Note);
        Assert.Equal(new AdapterRow("Phone", "192.168.42.11", "38 ms", Dot.Green, true), view.Phone);
        Assert.Equal(new AdapterRow("LAN", "192.168.86.42", "12 ms", Dot.Green, false), view.Lan);
        Assert.Equal("Internet: Phone (38 ms) · LAN ready", view.Tooltip);
        Assert.Equal(TrayColor.Green, view.TrayColor);
        Assert.False(view.TrayBadge);
    }

    [Fact]
    public void Phone_unplugged_in_phone_mode_shows_fallback()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.Lan, phone: false));

        Assert.Equal("Internet via LAN (phone offline)", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.True(view.TrayBadge);
        Assert.Equal(new AdapterRow("Phone", "not connected", "", Dot.Gray, false), view.Phone);
        Assert.True(view.Lan.IsActive);
        Assert.Equal("Internet: LAN (12 ms) · Phone offline", view.Tooltip);
    }

    [Fact]
    public void Phone_connected_but_not_routing_shows_reason()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.Lan));

        Assert.Equal("Internet via LAN (phone not routing)", view.Header);
    }

    [Fact]
    public void Lan_mode_fallback_to_phone()
    {
        var view = StatusPresenter.Present(Status(mode: RoutingMode.Lan, path: InternetPath.Phone, lan: false));

        Assert.Equal("Internet via PHONE (LAN offline)", view.Header);
        Assert.Equal(TrayColor.Blue, view.TrayColor);
        Assert.True(view.TrayBadge);
    }

    [Fact]
    public void Adapter_without_probe_reply_is_amber()
    {
        var view = StatusPresenter.Present(Status(phoneMs: null));

        Assert.Equal(new AdapterRow("Phone", "192.168.42.11", "no reply", Dot.Amber, true), view.Phone);
    }

    [Theory]
    [InlineData(499, Dot.Green)]
    [InlineData(500, Dot.Amber)]
    [InlineData(1250, Dot.Amber)]
    public void Slow_reply_shows_its_figure_in_amber(int ms, Dot dot)
    {
        var view = StatusPresenter.Present(Status(lanMs: ms));

        Assert.Equal(new AdapterRow("LAN", "192.168.86.42", $"{ms} ms", dot, false), view.Lan);
    }

    [Fact]
    public void Ambiguous_adapter_asks_to_choose()
    {
        var view = StatusPresenter.Present(Status(lan: false, missingLanIssue: DetectionIssue.Ambiguous));

        Assert.Equal("choose adapter…", view.Lan.Address);
        Assert.Equal(Dot.Gray, view.Lan.Dot);
    }

    [Fact]
    public void No_internet_is_a_warning()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.None));

        Assert.Equal("No internet", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.True(view.TrayBadge);
        Assert.Equal("Internet: none · LAN ready", view.Tooltip);
    }

    [Fact]
    public void Error_replaces_header_and_tooltip()
    {
        var view = StatusPresenter.Present(Status(error: "Ethernet IPv4: Access is denied."));

        Assert.Equal("Ethernet IPv4: Access is denied.", view.Header);
        Assert.True(view.HeaderWarning);
        Assert.Equal("NetRoute: Ethernet IPv4: Access is denied.", view.Tooltip);
    }

    [Fact]
    public void Ipv6_on_a_different_adapter_adds_a_note()
    {
        Assert.Equal("IPv6 traffic goes via LAN", StatusPresenter.Present(Status(ipv6: InternetPath.Lan)).Note);
        Assert.Null(StatusPresenter.Present(Status(ipv6: InternetPath.Phone)).Note);
        Assert.Null(StatusPresenter.Present(Status(ipv6: InternetPath.None)).Note);
    }

    [Fact]
    public void Auto_mode_is_gray_and_other_adapter_is_named()
    {
        var auto = StatusPresenter.Present(Status(mode: RoutingMode.Auto, path: InternetPath.Lan));
        Assert.Equal(TrayColor.Gray, auto.TrayColor);
        Assert.False(auto.TrayBadge);
        Assert.Equal("Internet via LAN", auto.Header);

        Assert.Equal("Internet via other adapter", StatusPresenter.Present(Status(path: InternetPath.Other)).Header);
    }

    [Fact]
    public void Toast_when_phone_disconnects()
    {
        Assert.Equal("Phone disconnected — internet via LAN",
            StatusPresenter.ToastFor(Status(), Status(path: InternetPath.Lan, phone: false)));
    }

    [Fact]
    public void Toast_when_phone_comes_back()
    {
        Assert.Equal("Phone back — internet via Phone",
            StatusPresenter.ToastFor(Status(path: InternetPath.Lan, phone: false), Status()));
    }

    [Fact]
    public void Toast_when_phone_stays_connected_but_loses_the_route()
    {
        Assert.Equal("Phone lost internet — internet via LAN",
            StatusPresenter.ToastFor(Status(), Status(path: InternetPath.Lan)));
    }

    [Fact]
    public void No_toast_when_path_is_unchanged()
    {
        Assert.Null(StatusPresenter.ToastFor(Status(), Status(phoneMs: 99)));
    }

    [Fact]
    public void Toast_when_internet_is_lost()
    {
        Assert.Equal("No internet connection", StatusPresenter.ToastFor(Status(), Status(path: InternetPath.None)));
    }

    [Fact]
    public void ResolvePath_maps_best_interface_to_adapter()
    {
        var adapters = Status().Adapters;

        Assert.Equal(InternetPath.Phone, NetworkStatus.ResolvePath(31, adapters));
        Assert.Equal(InternetPath.Lan, NetworkStatus.ResolvePath(10, adapters));
        Assert.Equal(InternetPath.Other, NetworkStatus.ResolvePath(40, adapters));
        Assert.Equal(InternetPath.None, NetworkStatus.ResolvePath(null, adapters));
    }

    [Fact]
    public void Wifi_path_resolves_to_wifi()
    {
        var adapters = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, TestAdapters.Wifi(), DetectionIssue.None);
        Assert.Equal(InternetPath.Wifi, NetworkStatus.ResolvePath(18, adapters));
    }

    [Theory]
    [InlineData(RoutingMode.Wifi, InternetPath.Lan, true)]
    [InlineData(RoutingMode.Wifi, InternetPath.Wifi, false)]
    [InlineData(RoutingMode.Lan, InternetPath.Wifi, true)]
    [InlineData(RoutingMode.Auto, InternetPath.Wifi, false)]
    public void Fallback_is_any_path_other_than_the_main(RoutingMode mode, InternetPath path, bool expected) =>
        Assert.Equal(expected, (NetworkStatus.Initial(mode, true) with { ActivePath = path }).IsFallback);

    [Fact]
    public void Carrier_is_the_main_when_present()
    {
        var all = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None, TestAdapters.Wifi(), DetectionIssue.None);
        Assert.Equal(ConnectionId.Wifi, NetworkStatus.CarrierFor(RoutingMode.Wifi, null, all));
        Assert.Equal(ConnectionId.Phone, NetworkStatus.CarrierFor(RoutingMode.Lan, ConnectionId.Phone, all));
    }

    [Fact]
    public void Carrier_falls_to_wifi_when_the_lan_cable_is_out()
    {
        var noLan = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, null, DetectionIssue.NotFound, TestAdapters.Wifi(), DetectionIssue.None);
        Assert.Equal(ConnectionId.Wifi, NetworkStatus.CarrierFor(RoutingMode.Lan, null, noLan));
    }

    [Fact]
    public void Carrier_skips_adapter_without_gateway_or_ipv4()
    {
        var pending = TestAdapters.Wifi() with { IPv4 = null };
        var adapters = DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, null, DetectionIssue.NotFound, pending, DetectionIssue.None);
        Assert.Equal(ConnectionId.Phone, NetworkStatus.CarrierFor(RoutingMode.Wifi, null, adapters));
    }

    [Fact]
    public void WithLatency_sets_one_connection_without_mutating_the_original()
    {
        var initial = NetworkStatus.Initial(RoutingMode.Auto, true);
        var updated = initial.WithLatency(ConnectionId.Wifi, 7);

        Assert.Equal(7, updated.WifiLatencyMs);
        Assert.Null(initial.WifiLatencyMs);
    }

    [Fact]
    public void Healing_header_explains_the_phone_has_no_internet()
    {
        var view = StatusPresenter.Present(Status(path: InternetPath.Lan) with { IsHealing = true });

        Assert.Equal("Internet via LAN (phone has no internet)", view.Header);
        Assert.True(view.TrayBadge);
    }

    [Theory]
    [InlineData(RoutingMode.Phone, InternetPath.Lan, "Internet via LAN (phone keeps losing internet — click Phone to retry)")]
    [InlineData(RoutingMode.Lan, InternetPath.Phone, "Internet via PHONE (LAN keeps losing internet — click LAN to retry)")]
    public void Sticky_heal_header_tells_the_user_how_to_retry(RoutingMode mode, InternetPath path, string header)
    {
        var view = StatusPresenter.Present(Status(mode: mode, path: path) with { IsHealing = true, IsHealSticky = true });

        Assert.Equal(header, view.Header);
        Assert.True(view.TrayBadge);
    }

    [Theory]
    [InlineData(RoutingMode.Phone, ConnectionId.Lan, "Phone keeps losing internet — staying on LAN. Click Phone to retry.")]
    [InlineData(RoutingMode.Lan, ConnectionId.Phone, "LAN keeps losing internet — staying on Phone. Click LAN to retry.")]
    public void Sticky_heal_toast_names_the_retry_button(RoutingMode mode, ConnectionId target, string toast)
    {
        Assert.Equal(toast, StatusPresenter.StickyHealToast(mode, target));
    }

    [Theory]
    [InlineData(RoutingMode.Phone, TrayColor.Green)]
    [InlineData(RoutingMode.Lan, TrayColor.Blue)]
    [InlineData(RoutingMode.Wifi, TrayColor.Purple)]
    [InlineData(RoutingMode.Auto, TrayColor.Gray)]
    public void Tray_colour_per_mode(RoutingMode mode, TrayColor color) =>
        Assert.Equal(color, StatusPresenter.Present(StatusWith3(mode)).TrayColor);

    [Fact]
    public void Wifi_mode_header() =>
        Assert.Equal("Internet via Wi-Fi", StatusPresenter.Present(StatusWith3(RoutingMode.Wifi) with { ActivePath = InternetPath.Wifi }).Header);

    [Fact]
    public void Healed_onto_wifi_header()
    {
        var view = StatusPresenter.Present(StatusWith3(RoutingMode.Lan) with { ActivePath = InternetPath.Wifi, IsHealing = true });

        Assert.Equal("Internet via Wi-Fi (LAN has no internet)", view.Header);
    }

    [Fact]
    public void Wifi_row_uses_the_network_name()
    {
        var withName = StatusPresenter.Present(StatusWith3(RoutingMode.Wifi) with { WifiNetwork = "pzhome" });
        var withoutName = StatusPresenter.Present(StatusWith3(RoutingMode.Wifi));

        Assert.Equal("pzhome", withName.Rows.Single(r => r.Id == ConnectionId.Wifi).Row.Label);
        Assert.Equal("Wi-Fi", withoutName.Rows.Single(r => r.Id == ConnectionId.Wifi).Row.Label);
    }

    [Fact]
    public void Unused_wifi_has_no_row()
    {
        var view = StatusPresenter.Present(Status());

        Assert.Equal([ConnectionId.Phone, ConnectionId.Lan], view.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void Toast_when_lan_lost_and_wifi_took_over()
    {
        var before = StatusWith3(RoutingMode.Lan);
        var after = before with { ActivePath = InternetPath.Wifi };

        Assert.Equal("LAN lost internet — internet via Wi-Fi", StatusPresenter.ToastFor(before, after));
    }

    [Fact]
    public void Sticky_toast_names_the_target() =>
        Assert.Equal("LAN keeps losing internet — staying on Wi-Fi. Click LAN to retry.",
            StatusPresenter.StickyHealToast(RoutingMode.Lan, ConnectionId.Wifi));

    [Fact]
    public void Tooltip_lists_every_other_connection()
    {
        var view = StatusPresenter.Present(StatusWith3(RoutingMode.Wifi) with { ActivePath = InternetPath.Wifi });

        Assert.Equal("Internet: Wi-Fi (20 ms) · Phone ready · LAN ready", view.Tooltip);
    }

    [Fact]
    public void Tooltip_has_no_dangling_separator_when_nothing_else_is_used()
    {
        var view = StatusPresenter.Present(Status(lan: false, missingLanIssue: DetectionIssue.NotUsed));

        Assert.Equal("Internet: Phone (38 ms)", view.Tooltip);
    }

    [Fact]
    public void Tooltip_with_no_active_path_names_the_real_backup_not_a_hardcoded_lan()
    {
        // Wi-Fi-primary setup: no LAN cable at all (NotUsed), Wi-Fi is the only used connection.
        var adapters = DetectionResult.Of(null, DetectionIssue.NotUsed, null, DetectionIssue.NotUsed, TestAdapters.Wifi(), DetectionIssue.None);
        var status = new NetworkStatus(RoutingMode.Wifi, adapters, InternetPath.None, InternetPath.None,
            new Dictionary<ConnectionId, int?> { [ConnectionId.Wifi] = 20 }, CanModify: true, null);

        Assert.Equal("Internet: none · Wi-Fi ready", StatusPresenter.Present(status).Tooltip);
    }

    [Theory]
    [InlineData(RoutingMode.Lan, InternetPath.Phone, InternetPath.Lan, "LAN back — internet via LAN")]
    [InlineData(RoutingMode.Auto, InternetPath.Lan, InternetPath.Phone, "Internet now via Phone")]
    [InlineData(RoutingMode.Auto, InternetPath.Phone, InternetPath.Lan, "Internet now via LAN")]
    [InlineData(RoutingMode.Phone, InternetPath.Phone, InternetPath.Other, "Internet now via another adapter (VPN?)")]
    public void Toast_covers_the_remaining_switch_arms(RoutingMode mode, InternetPath before, InternetPath after, string toast) =>
        Assert.Equal(toast, StatusPresenter.ToastFor(Status(mode: mode, path: before), Status(mode: mode, path: after)));

    static NetworkStatus LanTurnedOff(RoutingMode mode = RoutingMode.Phone, InternetPath path = InternetPath.Phone) =>
        Status(mode, path) with
        {
            Adapters = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan()], AdapterOverrides.None, [ConnectionId.Lan]),
        };

    [Fact]
    public void Off_connection_keeps_its_row_reading_off()
    {
        var view = StatusPresenter.Present(LanTurnedOff());

        Assert.Equal([ConnectionId.Phone, ConnectionId.Lan], view.Rows.Select(r => r.Id));
        Assert.Equal(new AdapterRow("LAN", "off", "", Dot.Gray, false, IsOff: true), view.Lan);
    }

    [Fact]
    public void Off_connection_is_not_listed_as_a_backup_in_the_tooltip()
    {
        var view = StatusPresenter.Present(LanTurnedOff());

        Assert.Equal("Internet: Phone (38 ms)", view.Tooltip);
    }

    [Theory]
    [InlineData(ConnectionId.Phone)]
    [InlineData(ConnectionId.Lan)]
    [InlineData(ConnectionId.Wifi)]
    public void Off_connection_is_hidden_from_the_card_and_returns_when_enabled(ConnectionId id)
    {
        var adapters = new[] { TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi() };
        var overrides = new AdapterOverrides(null, null, "AA-BB-CC-00-00-18");
        var status = StatusWith3(RoutingMode.Auto);
        var off = StatusPresenter.Present(status with { Adapters = AdapterDetector.Detect(adapters, overrides, [id]) });

        Assert.DoesNotContain(off.VisibleRows, row => row.Id == id);
        Assert.False(off.IsModeButtonVisible(id));
        // The tray still receives the configured connection and its disabled state.
        Assert.True(off.Rows.Single(row => row.Id == id).Row.IsOff);

        var on = StatusPresenter.Present(status with { Adapters = AdapterDetector.Detect(adapters, overrides, []) });
        Assert.Equal([ConnectionId.Phone, ConnectionId.Lan, ConnectionId.Wifi], on.VisibleRows.Select(row => row.Id));
        Assert.True(on.IsModeButtonVisible(id));
    }

    [Fact]
    public void Chosen_but_disconnected_connections_still_show_rows_and_mode_buttons()
    {
        var status = StatusWith3(RoutingMode.Auto) with
        {
            Adapters = DetectionResult.Of(null, DetectionIssue.NotFound, null, DetectionIssue.NotFound, null, DetectionIssue.NotFound),
        };
        var view = StatusPresenter.Present(status);

        Assert.Equal([ConnectionId.Phone, ConnectionId.Lan, ConnectionId.Wifi], view.VisibleRows.Select(row => row.Id));
        Assert.All(view.VisibleRows, row =>
        {
            Assert.Equal("not connected", row.Row.Address);
            Assert.True(view.IsModeButtonVisible(row.Id));
        });
    }

    [Fact]
    public void All_off_hides_all_card_rows_and_mode_buttons_without_changing_tray_metadata()
    {
        var status = StatusWith3(RoutingMode.Auto) with
        {
            Adapters = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi()],
                new AdapterOverrides(null, null, "AA-BB-CC-00-00-18"), Connections.All),
        };
        var view = StatusPresenter.Present(status);

        Assert.Empty(view.VisibleRows);
        Assert.Equal([ConnectionId.Phone, ConnectionId.Lan, ConnectionId.Wifi], view.Rows.Select(row => row.Id));
        Assert.All(view.Rows, row =>
        {
            Assert.True(row.Row.IsOff);
            Assert.False(view.IsModeButtonVisible(row.Id));
        });
    }

    [Fact]
    public void Not_used_slots_keep_the_existing_mode_button_visibility()
    {
        var status = StatusWith3(RoutingMode.Auto) with
        {
            Adapters = DetectionResult.Of(null, DetectionIssue.NotUsed, null, DetectionIssue.NotUsed, null, DetectionIssue.NotUsed),
        };
        var view = StatusPresenter.Present(status);

        Assert.Empty(view.VisibleRows);
        Assert.True(view.IsModeButtonVisible(ConnectionId.Phone));
        Assert.True(view.IsModeButtonVisible(ConnectionId.Lan));
        Assert.False(view.IsModeButtonVisible(ConnectionId.Wifi));
    }

    [Fact]
    public void Windows_routing_through_an_off_adapter_is_still_named()
    {
        var adapters = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan()], AdapterOverrides.None, [ConnectionId.Lan]);

        Assert.Equal(InternetPath.Lan, NetworkStatus.ResolvePath(TestAdapters.Lan().Index, adapters));
        Assert.Equal("Internet via LAN", StatusPresenter.Present(LanTurnedOff(RoutingMode.Auto, InternetPath.Lan)).Header);
    }
}
