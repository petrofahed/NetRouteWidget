namespace NetRoute.Core.Tests;

public class AdapterDetectorTests
{
    [Fact]
    public void Detects_phone_and_lan_and_ignores_virtual_adapters()
    {
        var all = new[] { TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.VmNet(29), TestAdapters.HyperV(), TestAdapters.NordLynx() };

        var result = AdapterDetector.Detect(all, AdapterOverrides.None);

        Assert.Equal(31, result.Phone?.Index);
        Assert.Equal(DetectionIssue.None, result.PhoneIssue);
        Assert.Equal(10, result.Lan?.Index);
        Assert.Equal(DetectionIssue.None, result.LanIssue);
    }

    [Fact]
    public void Detects_replugged_phone_with_new_index_name_and_numbered_description()
    {
        var replugged = TestAdapters.Phone(index: 36, description: "SAMSUNG Mobile USB Remote NDIS Network Device #2");

        var result = AdapterDetector.Detect([replugged, TestAdapters.Lan()], AdapterOverrides.None);

        Assert.Equal(36, result.Phone?.Index);
    }

    [Fact]
    public void Phone_override_matches_description_ignoring_number_suffix()
    {
        var replugged = TestAdapters.Phone(index: 36, description: "SAMSUNG Mobile USB Remote NDIS Network Device #2");
        var overrides = new AdapterOverrides("SAMSUNG Mobile USB Remote NDIS Network Device", null);

        var result = AdapterDetector.Detect([replugged, TestAdapters.Lan()], overrides);

        Assert.Equal(36, result.Phone?.Index);
    }

    [Fact]
    public void Missing_phone_is_reported_as_not_found()
    {
        var result = AdapterDetector.Detect([TestAdapters.Lan()], AdapterOverrides.None);

        Assert.Null(result.Phone);
        Assert.Equal(DetectionIssue.NotFound, result.PhoneIssue);
        Assert.Equal(10, result.Lan?.Index);
    }

    [Fact]
    public void Two_lan_candidates_are_reported_as_ambiguous()
    {
        var second = TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12");

        var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), second], AdapterOverrides.None);

        Assert.Null(result.Lan);
        Assert.Equal(DetectionIssue.Ambiguous, result.LanIssue);
    }

    [Fact]
    public void Lan_override_by_mac_resolves_ambiguity_case_insensitively()
    {
        var second = TestAdapters.Lan(index: 12, mac: "AA-BB-CC-00-00-12");

        var result = AdapterDetector.Detect([TestAdapters.Lan(), second], new AdapterOverrides(null, "aa-bb-cc-00-00-12"));

        Assert.Equal(12, result.Lan?.Index);
    }

    [Fact]
    public void Down_adapters_and_lan_without_gateway_are_ignored()
    {
        var downPhone = TestAdapters.Phone() with { IsUp = false };
        var noGateway = TestAdapters.Lan() with { HasGateway = false };

        var result = AdapterDetector.Detect([downPhone, noGateway], AdapterOverrides.None);

        Assert.Equal(DetectionIssue.NotFound, result.PhoneIssue);
        Assert.Equal(DetectionIssue.NotFound, result.LanIssue);
    }

    [Fact]
    public void Wifi_is_never_auto_detected_as_lan()
    {
        var result = AdapterDetector.Detect([TestAdapters.Lan(), TestAdapters.Wifi()], AdapterOverrides.None);

        Assert.Equal(10, result.Lan?.Index);
        Assert.Null(result.Wifi);
    }

    [Fact]
    public void Wifi_is_not_used_until_chosen()
    {
        var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi()], AdapterOverrides.None);
        Assert.Null(result.Wifi);
        Assert.Equal(DetectionIssue.NotUsed, result.WifiIssue);
        Assert.False(result.IsUsed(ConnectionId.Wifi));
    }

    [Fact]
    public void Wifi_override_detects_wifi_as_wifi()
    {
        var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi()],
            new AdapterOverrides(null, null, "AA-BB-CC-00-00-18"));
        Assert.Equal(18, result.Wifi?.Index);
        Assert.Equal(10, result.Lan?.Index);
    }

    [Fact]
    public void Not_used_phone_and_lan()
    {
        var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan()],
            new AdapterOverrides(AdapterOverrides.NotUsed, AdapterOverrides.NotUsed));
        Assert.Equal(DetectionIssue.NotUsed, result.PhoneIssue);
        Assert.Equal(DetectionIssue.NotUsed, result.LanIssue);
        Assert.Empty(result.Present);
    }

    [Fact]
    public void One_adapter_never_fills_two_slots()
    {
        var wifi = TestAdapters.Wifi();
        var result = AdapterDetector.Detect([TestAdapters.Phone(), wifi], new AdapterOverrides(null, wifi.Mac, wifi.Mac));
        Assert.Equal(18, result.Wifi?.Index);
        Assert.Null(result.Lan);
    }

    [Fact]
    public void Suggest_fills_every_slot_it_can()
    {
        var s = AdapterDetector.Suggest([TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.VmNet(29)]);
        Assert.Equal("SAMSUNG Mobile USB Remote NDIS Network Device", s.PhoneDescription);
        Assert.Equal("AA-BB-CC-00-00-10", s.LanMac);
        Assert.Equal("AA-BB-CC-00-00-18", s.WifiMac);
    }

    [Fact]
    public void Suggest_marks_missing_or_ambiguous_slots_not_used()
    {
        var s = AdapterDetector.Suggest([TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.Wifi(index: 19, mac: "AA-BB-CC-00-00-19")]);
        Assert.Equal(AdapterOverrides.NotUsed, s.PhoneDescription);
        Assert.Equal(AdapterOverrides.NotUsed, s.WifiMac);
    }

    [Fact]
    public void Results_with_the_same_picks_are_equal()
    {
        Assert.Equal(
            DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None),
            DetectionResult.Of(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None));
    }

    [Fact]
    public void Candidates_exclude_virtual_and_non_network_adapters()
    {
        var all = new[] { TestAdapters.Phone(), TestAdapters.Lan(), TestAdapters.Wifi(), TestAdapters.VmNet(29), TestAdapters.HyperV(), TestAdapters.NordLynx() };

        var indexes = AdapterDetector.Candidates(all).Select(a => a.Index).Order().ToArray();

        Assert.Equal(new[] { 10, 18, 31 }, indexes);
    }

    [Fact]
    public void Smart_routing_tun_adapter_is_ignored()
    {
        var tun = new AdapterInfo(97, "NetRoute", "sing-tun", AdapterKind.Ethernet, true, true, "172.19.0.1", "");

        var result = AdapterDetector.Detect([TestAdapters.Phone(), TestAdapters.Lan(), tun], AdapterOverrides.None);

        Assert.Equal((31, 10), (result.Phone?.Index, result.Lan?.Index));
        Assert.DoesNotContain(AdapterDetector.Candidates([tun]), _ => true);
    }

    [Fact]
    public void Smart_routing_tun_adapter_never_makes_the_lan_ambiguous()
    {
        var tun = new AdapterInfo(97, "NetRoute", "sing-tun", AdapterKind.Ethernet, true, true, "172.19.0.1", "");

        var result = AdapterDetector.Detect([TestAdapters.Lan(), tun], AdapterOverrides.None);

        Assert.Equal(10, result.Lan?.Index);
        Assert.Equal(DetectionIssue.None, result.LanIssue);
        Assert.Null(result.Phone);
        Assert.Equal(DetectionIssue.NotFound, result.PhoneIssue);
        Assert.Equal([10], AdapterDetector.Candidates([TestAdapters.Lan(), tun]).Select(a => a.Index));
    }

    [Fact]
    public void Detail_line_for_lan() =>
        Assert.Equal("192.168.86.42 · gateway 192.168.86.1 · 1 Gbps",
            AdapterDetector.DetailLine(TestAdapters.Lan() with { SpeedBps = 1_000_000_000 }, ConnectionId.Lan));

    [Fact]
    public void Detail_line_for_phone() =>
        Assert.Equal("192.168.42.11 · gateway 192.168.42.129 · USB tethering",
            AdapterDetector.DetailLine(TestAdapters.Phone() with { Gateway = "192.168.42.129" }, ConnectionId.Phone));

    [Fact]
    public void Detail_line_for_down_adapter() =>
        Assert.EndsWith("not connected",
            AdapterDetector.DetailLine(TestAdapters.Wifi() with { IsUp = false, IPv4 = null }, ConnectionId.Wifi));
}
