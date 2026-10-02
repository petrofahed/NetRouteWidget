using System.Text.Json.Nodes;

namespace NetRoute.Core.Tests;

public class SingBoxConfigBuilderTests
{
    static readonly RuleSet Rules = new(
    [
        new RuleEntry("youtube", "YouTube", [], ["youtube.com", "googlevideo.com"]),
        new RuleEntry("onedrive", "OneDrive", ["OneDrive.exe"], ["onedrive.live.com"]),
    ]);

    static SingBoxConfigInput Input(RouteExit exit = RouteExit.Phone, bool lanRulesOnPhone = false, string? lanDns = "192.168.86.1") =>
        new(Rules, "Ethernet 5", "Ethernet", lanDns, exit, lanRulesOnPhone, 41234, "s3cret");

    static JsonNode Parse(SingBoxConfig c) => JsonNode.Parse(c.Json)!;

    static JsonNode Outbound(JsonNode root, string tag) =>
        root["outbounds"]!.AsArray().Single(o => (string)o!["tag"]! == tag)!;

    [Fact]
    public void Outbounds_bind_adapters_and_selectors_default_per_state()
    {
        var root = Parse(SingBoxConfigBuilder.Build(Input()));

        Assert.Equal("Ethernet 5", (string)Outbound(root, "phone")["bind_interface"]!);
        Assert.Equal("Ethernet", (string)Outbound(root, "lan")["bind_interface"]!);
        Assert.Equal("lan", (string)Outbound(root, "lan-only")["default"]!);
        Assert.Equal("phone", (string)Outbound(root, "default")["default"]!);
        Assert.Equal("default", (string)root["route"]!["final"]!);
        Assert.Equal("remote", (string)root["route"]!["default_domain_resolver"]!); // sing-box check requires one

        var healed = Parse(SingBoxConfigBuilder.Build(Input(RouteExit.Lan, lanRulesOnPhone: true)));
        Assert.Equal("phone", (string)Outbound(healed, "lan-only")["default"]!);
        Assert.Equal("lan", (string)Outbound(healed, "default")["default"]!);
    }

    [Fact]
    public void Each_entry_gets_separate_process_and_domain_rules_mapped_to_its_id()
    {
        var config = SingBoxConfigBuilder.Build(Input());
        var rules = Parse(config)["route"]!["rules"]!.AsArray();

        Assert.Equal("sniff", (string)rules[0]!["action"]!);
        Assert.Equal("hijack-dns", (string)rules[1]!["action"]!);
        Assert.True((bool)rules[2]!["ip_is_private"]!);
        // youtube: domains only -> index 3; onedrive: process -> 4, domains -> 5
        Assert.Equal(new Dictionary<int, string> { [3] = "youtube", [4] = "onedrive", [5] = "onedrive" }, config.RuleIndexToEntryId);
        Assert.Equal("lan-only", (string)rules[4]!["outbound"]!);
        Assert.Equal("OneDrive.exe", (string)rules[4]!["process_name"]![0]!);
        Assert.Null(rules[4]!["domain_suffix"]); // never AND a process with domains
        Assert.Equal("onedrive.live.com", (string)rules[5]!["domain_suffix"]![0]!);
    }

    [Fact]
    public void Tun_excludes_private_ranges_and_api_listens_on_loopback()
    {
        var root = Parse(SingBoxConfigBuilder.Build(Input()));
        var tun = root["inbounds"]![0]!;

        Assert.Equal(("tun", "NetRoute", true, false),
            ((string)tun["type"]!, (string)tun["interface_name"]!, (bool)tun["auto_route"]!, (bool)tun["strict_route"]!));
        Assert.Equal(SingBoxConfigBuilder.ExcludedRanges, tun["route_exclude_address"]!.AsArray().Select(n => (string)n!));
        Assert.Contains("192.168.0.0/16", SingBoxConfigBuilder.ExcludedRanges);
        Assert.Equal("127.0.0.1:41234", (string)root["experimental"]!["clash_api"]!["external_controller"]!);
        Assert.Equal("s3cret", (string)root["experimental"]!["clash_api"]!["secret"]!);
        Assert.Equal("debug", (string)root["log"]!["level"]!);
    }

    [Fact]
    public void Router_local_names_use_the_lan_dns_only_when_known()
    {
        var withLan = Parse(SingBoxConfigBuilder.Build(Input()))["dns"]!;
        Assert.Equal("remote", (string)withLan["final"]!);
        var lanDns = withLan["servers"]!.AsArray().Single(s => (string)s!["tag"]! == "lan-dns")!;
        Assert.Equal(("192.168.86.1", "lan"), ((string)lanDns["server"]!, (string)lanDns["detour"]!));
        Assert.Equal("lan-dns", (string)withLan["rules"]![0]!["server"]!);

        var without = Parse(SingBoxConfigBuilder.Build(Input(lanDns: null)))["dns"]!;
        Assert.Single(without["servers"]!.AsArray());
        Assert.Empty(without["rules"]!.AsArray());
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("not-an-ip")]
    [InlineData("")]
    public void Loopback_or_invalid_lan_dns_is_treated_as_absent(string lanDns)
    {
        var dns = Parse(SingBoxConfigBuilder.Build(Input(lanDns: lanDns)))["dns"]!;

        Assert.Single(dns["servers"]!.AsArray());
        Assert.Empty(dns["rules"]!.AsArray());
    }

    [Fact]
    public void Empty_rule_set_still_produces_a_valid_shape()
    {
        var config = SingBoxConfigBuilder.Build(Input() with { Rules = new RuleSet([]) });

        Assert.Empty(config.RuleIndexToEntryId);
        Assert.Equal(3, Parse(config)["route"]!["rules"]!.AsArray().Count);
    }
}
