using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

    static SingBoxConfig BuildWith(params RuleEntry[] entries) =>
        SingBoxConfigBuilder.Build(new SingBoxConfigInput(new RuleSet(entries), "Ethernet 5", "Ethernet", null, RouteExit.Lan, false, 41234, "s3cret"));

    static JsonArray RouteRules(SingBoxConfig c) => Parse(c)["route"]!["rules"]!.AsArray();

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
        // A selector switch must move connections already open, or they would stay on the old exit.
        Assert.True((bool)Outbound(root, "lan-only")["interrupt_exist_connections"]!);
        Assert.True((bool)Outbound(root, "default")["interrupt_exist_connections"]!);

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
        Assert.Equal(@"(?i)\\OneDrive\.exe$", (string)rules[4]!["process_path_regex"]![0]!);
        Assert.Null(rules[4]!["process_name"]); // process_name is an exact, case-sensitive match
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

    [Fact]
    public void Process_rules_use_one_escaped_case_insensitive_path_regex_per_process()
    {
        var rules = new RuleSet([new RuleEntry("app", "My App", ["My App.exe", "qbittorrent.exe"], [])]);

        var config = SingBoxConfigBuilder.Build(Input() with { Rules = rules });

        var rule = Parse(config)["route"]!["rules"]![3]!;
        var regexes = rule["process_path_regex"]!.AsArray().Select(n => (string)n!).ToList();
        Assert.Equal(2, regexes.Count);
        Assert.Equal(@"(?i)\\My\ App\.exe$", regexes[0]); // dot and space are escaped
        Assert.Equal(new Dictionary<int, string> { [3] = "app" }, config.RuleIndexToEntryId);

        // The regex must behave as intended (sing-box uses Go RE2; this subset means the same there).
        Assert.Matches(regexes[0], @"C:\Tools\MY APP.EXE");
        Assert.Matches(regexes[0], @"C:\Tools\my app.exe");
        Assert.DoesNotMatch(regexes[0], @"C:\Tools\xMy App.exe");   // must start at a path separator
        Assert.DoesNotMatch(regexes[0], @"C:\Tools\My AppXexe");    // the dot is literal
        Assert.DoesNotMatch(regexes[0], @"C:\Tools\My App.exe.bak"); // must end the path
        Assert.Matches(regexes[1], @"D:\torrents\QBitTorrent.EXE");
    }

    [Fact]
    public void Process_path_regex_survives_json_round_trip()
    {
        const string name = "weird+name (1).exe";

        var config = SingBoxConfigBuilder.Build(Input() with { Rules = new RuleSet([new RuleEntry("w", "W", [name], [])]) });

        var parsed = (string)Parse(config)["route"]!["rules"]![3]!["process_path_regex"]![0]!;
        Assert.Equal(SingBoxConfigBuilder.ProcessPathRegex(name), parsed);
        Assert.Matches(parsed, @"C:\x\WEIRD+NAME (1).EXE");
    }

    [Fact]
    public void Dns_resolves_ipv4_only_so_nothing_goes_to_ipv6_addresses_that_would_bypass_the_ipv4_tun()
    {
        var root = Parse(SingBoxConfigBuilder.Build(Input()));

        Assert.Equal("ipv4_only", (string)root["dns"]!["strategy"]!);
    }

    [Fact]
    public void A_phone_exit_entry_routes_to_the_phone_outbound_and_a_lan_entry_to_lan_only()
    {
        var config = BuildWith(
            new RuleEntry("claude", "Claude", ["claude.exe"], ["claude.ai"], RouteExit.Phone),
            new RuleEntry("youtube", "YouTube", [], ["youtube.com"], RouteExit.Lan));

        var rules = RouteRules(config).Where(r => r!["outbound"] is not null && (string)r["outbound"]! is "phone" or "lan-only").ToList();

        // claude: one process rule + one domain rule (both phone); youtube: one domain rule (lan-only)
        Assert.Equal(new[] { "phone", "phone", "lan-only" }, rules.Select(r => (string)r!["outbound"]!));
    }

    [Fact]
    public void A_carve_out_rule_precedes_the_phone_rules()
    {
        var config = BuildWith(
            new RuleEntry("vscode-updates", "VS Code updates", [], ["update.code.visualstudio.com"], RouteExit.Lan, CarveOut: true),
            new RuleEntry("vscode", "VS Code", ["Code.exe"], [], RouteExit.Phone));

        var rules = RouteRules(config);
        var update = rules.Select((r, i) => (r, i)).Single(x => x.r!["domain_suffix"]?.AsArray().Any(d => (string)d! == "update.code.visualstudio.com") == true);
        var process = rules.Select((r, i) => (r, i)).Single(x => x.r!["process_path_regex"] is not null);

        Assert.True(update.i < process.i);
        Assert.Equal("lan-only", (string)update.r!["outbound"]!);
        Assert.Equal("phone", (string)process.r!["outbound"]!);
        Assert.Equal("vscode-updates", config.RuleIndexToEntryId[update.i]);
        Assert.Equal("vscode", config.RuleIndexToEntryId[process.i]);
    }
}
