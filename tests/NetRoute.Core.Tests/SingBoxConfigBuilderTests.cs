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

    static SingBoxConfigInput Input(ConnectionId exit = ConnectionId.Phone, bool lanRulesOnPhone = false, string? lanDns = "192.168.86.1")
    {
        var input = GenericInput(exit, new Dictionary<ConnectionId, string>
        {
            [ConnectionId.Phone] = "Ethernet 5", [ConnectionId.Lan] = "Ethernet",
        }) with { Rules = Rules, LanDns = lanDns, ApiSecret = "s3cret" };
        return !lanRulesOnPhone ? input : input with
        {
            Candidates = new Dictionary<RuleTarget, IReadOnlyList<ConnectionId>>(input.Candidates) { [RuleTarget.Home] = [ConnectionId.Lan, ConnectionId.Phone] },
            Selected = new Dictionary<RuleTarget, ConnectionId?>(input.Selected) { [RuleTarget.Home] = ConnectionId.Phone },
        };
    }

    static JsonNode Parse(SingBoxConfig c) => JsonNode.Parse(c.Json)!;

    static JsonNode Outbound(JsonNode root, string tag) =>
        root["outbounds"]!.AsArray().Single(o => (string)o!["tag"]! == tag)!;

    static SingBoxConfig BuildWith(params RuleEntry[] entries) =>
        SingBoxConfigBuilder.Build(Input(ConnectionId.Lan, lanDns: null) with { Rules = new RuleSet(entries) });

    static JsonArray RouteRules(SingBoxConfig c) => Parse(c)["route"]!["rules"]!.AsArray();

    [Fact]
    public void Outbounds_bind_adapters_and_selectors_default_per_state()
    {
        var root = Parse(SingBoxConfigBuilder.Build(Input()));

        Assert.Equal("Ethernet 5", (string)Outbound(root, "phone")["bind_interface"]!);
        Assert.Equal("Ethernet", (string)Outbound(root, "lan")["bind_interface"]!);
        Assert.Equal("lan", (string)Outbound(root, "only-home")["default"]!);
        Assert.Equal("phone", (string)Outbound(root, "default")["default"]!);
        Assert.Equal("default", (string)root["route"]!["final"]!);
        Assert.Equal("remote", (string)root["route"]!["default_domain_resolver"]!); // sing-box check requires one
        // A selector switch must move connections already open, or they would stay on the old exit.
        Assert.True((bool)Outbound(root, "only-home")["interrupt_exist_connections"]!);
        Assert.True((bool)Outbound(root, "default")["interrupt_exist_connections"]!);

        var healed = Parse(SingBoxConfigBuilder.Build(Input(ConnectionId.Lan, lanRulesOnPhone: true)));
        Assert.Equal("phone", (string)Outbound(healed, "only-home")["default"]!);
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
        Assert.Equal("only-home", (string)rules[4]!["outbound"]!);
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
            new RuleEntry("claude", "Claude", ["claude.exe"], ["claude.ai"], RuleTarget.Phone),
            new RuleEntry("youtube", "YouTube", [], ["youtube.com"], RuleTarget.Home));

        var rules = RouteRules(config).Where(r => r!["outbound"] is not null && (string)r["outbound"]! is "only-phone" or "only-home").ToList();

        // claude: one process rule + one domain rule (both phone); youtube: one domain rule (only-home)
        Assert.Equal(new[] { "only-phone", "only-phone", "only-home" }, rules.Select(r => (string)r!["outbound"]!));
    }

    [Fact]
    public void A_carve_out_rule_precedes_the_phone_rules()
    {
        var config = BuildWith(
            new RuleEntry("vscode-updates", "VS Code updates", [], ["update.code.visualstudio.com"], RuleTarget.Home, CarveOut: true),
            new RuleEntry("vscode", "VS Code", ["Code.exe"], [], RuleTarget.Phone));

        var rules = RouteRules(config);
        var update = rules.Select((r, i) => (r, i)).Single(x => x.r!["domain_suffix"]?.AsArray().Any(d => (string)d! == "update.code.visualstudio.com") == true);
        var process = rules.Select((r, i) => (r, i)).Single(x => x.r!["process_path_regex"] is not null);

        Assert.True(update.i < process.i);
        Assert.Equal("only-home", (string)update.r!["outbound"]!);
        Assert.Equal("only-phone", (string)process.r!["outbound"]!);
        Assert.Equal("vscode-updates", config.RuleIndexToEntryId[update.i]);
        Assert.Equal("vscode", config.RuleIndexToEntryId[process.i]);
    }

    static SingBoxConfigInput GenericInput(ConnectionId main, IReadOnlyDictionary<ConnectionId, string>? interfaces = null) =>
        new(new RuleSet(Enum.GetValues<RuleTarget>().Select(target =>
                new RuleEntry(target.ToString(), target.ToString(), [target + ".exe"], [target + ".example.com"], target)).ToList()),
            interfaces ?? new Dictionary<ConnectionId, string>
            {
                [ConnectionId.Lan] = "Ethernet", [ConnectionId.Wifi] = "Wi-Fi", [ConnectionId.Phone] = "USB",
            }, "192.168.86.1", main,
            new Dictionary<RuleTarget, IReadOnlyList<ConnectionId>>
            {
                [RuleTarget.Home] = [ConnectionId.Lan, ConnectionId.Wifi],
                [RuleTarget.Lan] = [ConnectionId.Lan, ConnectionId.Wifi],
                [RuleTarget.Wifi] = [ConnectionId.Wifi, ConnectionId.Lan],
                [RuleTarget.Phone] = [ConnectionId.Phone, main],
            }, new Dictionary<RuleTarget, ConnectionId?>
            {
                [RuleTarget.Home] = ConnectionId.Lan, [RuleTarget.Lan] = ConnectionId.Lan,
                [RuleTarget.Wifi] = ConnectionId.Wifi, [RuleTarget.Phone] = ConnectionId.Phone,
            }, 41234, "test");

    [Theory]
    [InlineData(ConnectionId.Lan, "lan", "Ethernet")]
    [InlineData(ConnectionId.Wifi, "wifi", "Wi-Fi")]
    [InlineData(ConnectionId.Phone, "phone", "USB")]
    public void Every_physical_default_and_target_compiles_to_matching_tags_and_index_maps(ConnectionId main, string tag, string name)
    {
        var config = SingBoxConfigBuilder.Build(GenericInput(main));
        var root = Parse(config);
        Assert.Equal(name, (string)Outbound(root, tag)["bind_interface"]!);
        Assert.Equal(tag, (string)Outbound(root, "default")["default"]!);
        foreach (var target in Enum.GetValues<RuleTarget>())
        {
            var selector = SmartRoutingPolicy.SelectorTag(target);
            Assert.Equal(target switch
            {
                RuleTarget.Home or RuleTarget.Lan => "lan",
                RuleTarget.Wifi => "wifi",
                RuleTarget.Phone => "phone",
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            }, (string)Outbound(root, selector)["default"]!);
            var indexes = config.RuleIndexToTarget.Where(pair => pair.Value == target).Select(pair => pair.Key).ToArray();
            Assert.Equal(2, indexes.Length);
            foreach (var index in indexes)
            {
                Assert.Equal(target.ToString(), config.RuleIndexToEntryId[index]);
                Assert.Equal(selector, (string)root["route"]!["rules"]![index]!["outbound"]!);
            }
            Assert.True((bool)Outbound(root, selector)["interrupt_exist_connections"]!);
        }
        Assert.Equal(new[] { "lan", "wifi", "unavailable" }, Outbound(root, "only-home")["outbounds"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Missing_lan_and_phone_produce_bound_waiting_targets_and_no_dangling_routes_or_dns()
    {
        var input = GenericInput(ConnectionId.Wifi, new Dictionary<ConnectionId, string> { [ConnectionId.Wifi] = "Wi-Fi" }) with
        {
            Selected = new Dictionary<RuleTarget, ConnectionId?>(),
        };
        var root = Parse(SingBoxConfigBuilder.Build(input));
        var outbounds = root["outbounds"]!.AsArray();
        var tags = outbounds.Select(outbound => (string)outbound!["tag"]!).ToHashSet();
        Assert.DoesNotContain("lan", tags);
        Assert.DoesNotContain("phone", tags);
        Assert.Equal("unavailable", (string)root["route"]!["rules"]![2]!["outbound"]!);
        Assert.DoesNotContain(root["dns"]!["servers"]!.AsArray(), server => (string?)server!["tag"] == "lan-dns");
        foreach (var outbound in outbounds)
        {
            if ((string)outbound!["type"]! == "direct") Assert.False(string.IsNullOrWhiteSpace((string?)outbound["bind_interface"]));
            else
            {
                var members = outbound["outbounds"]!.AsArray().Select(n => (string)n!).ToList();
                Assert.All(members, member => Assert.Contains(member, tags));
                Assert.Contains((string)outbound["default"]!, members);
            }
        }
        Assert.Equal("unavailable", (string)Outbound(root, "only-lan")["default"]!);
        Assert.Equal("unavailable", (string)Outbound(root, "only-phone")["default"]!);
        Assert.NotEqual("Wi-Fi", (string)Outbound(root, "unavailable")["bind_interface"]!);
    }

    [Fact]
    public void Selector_cannot_select_a_chosen_phone_without_explicit_membership()
    {
        var input = GenericInput(ConnectionId.Phone) with
        {
            Selected = new Dictionary<RuleTarget, ConnectionId?> { [RuleTarget.Home] = ConnectionId.Phone },
        };
        var root = Parse(SingBoxConfigBuilder.Build(input));
        Assert.Equal("unavailable", (string)Outbound(root, "only-home")["default"]!);
        Assert.DoesNotContain(Outbound(root, "only-home")["outbounds"]!.AsArray(), n => (string)n! == "phone");
        var candidates = new Dictionary<RuleTarget, IReadOnlyList<ConnectionId>>(input.Candidates) { [RuleTarget.Home] = [ConnectionId.Lan, ConnectionId.Wifi, ConnectionId.Phone] };
        root = Parse(SingBoxConfigBuilder.Build(input with { Candidates = candidates }));
        Assert.Equal("phone", (string)Outbound(root, "only-home")["default"]!);
    }
}
