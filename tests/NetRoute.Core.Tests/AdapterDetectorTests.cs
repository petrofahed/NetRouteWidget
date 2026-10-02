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
    public void Wifi_is_not_auto_detected_as_lan_but_can_be_chosen_by_override()
    {
        var wifi = TestAdapters.Wifi();

        Assert.Equal(DetectionIssue.NotFound, AdapterDetector.Detect([wifi], AdapterOverrides.None).LanIssue);
        Assert.Equal(18, AdapterDetector.Detect([wifi], new AdapterOverrides(null, wifi.Mac)).Lan?.Index);
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
}
