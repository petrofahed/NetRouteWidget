using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace NetRoute.Core;

public enum RouteExit { Phone, Lan }

public sealed record SingBoxConfigInput(
    RuleSet Rules, string PhoneInterface, string LanInterface, string? LanDns,
    RouteExit DefaultExit, bool LanRulesOnPhone, int ApiPort, string ApiSecret);

/// RuleIndexToEntryId maps route.rules indexes (as sing-box logs them in "router: match[N]") to RuleEntry ids.
public sealed record SingBoxConfig(string Json, IReadOnlyDictionary<int, string> RuleIndexToEntryId);

/// Builds the sing-box 1.14 config. Shape verified by the feasibility spike; see docs/superpowers/spikes.
public static class SingBoxConfigBuilder
{
    public const string PhoneTag = "phone";
    public const string LanTag = "lan";
    public const string LanOnlyTag = "lan-only";
    public const string DefaultTag = "default";

    public static readonly string[] ExcludedRanges =
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32",
        "fc00::/7", "fe80::/10", "ff00::/8",
    ];

    static readonly string[] RouterLocalSuffixes = ["lan", "local", "home", "home.arpa", "internal"];

    /// sing-box's process_name is an exact, case-sensitive match on the file name, but Windows file names are not
    /// case-sensitive (and users type "Steam.exe" for steam.exe). A path regex ending in the escaped name, with a
    /// leading separator so "xfoo.exe" does not match "foo.exe", is the case-insensitive form. Go RE2 syntax.
    public static string ProcessPathRegex(string name) => $@"(?i)\\{Regex.Escape(name)}$";

    public static SingBoxConfig Build(SingBoxConfigInput input)
    {
        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = LanTag },
        };
        var ruleMap = new Dictionary<int, string>();
        foreach (var entry in input.Rules.Entries)
        {
            // A LAN entry goes through the LAN-only selector (it can wait for the LAN); a phone entry goes straight to the phone.
            var outbound = entry.Exit == RouteExit.Phone ? PhoneTag : LanOnlyTag;
            // sing-box ANDs process fields with domain fields inside one rule, so they get separate rules.
            if (entry.Processes.Count > 0)
            {
                ruleMap[rules.Count] = entry.Id;
                rules.Add(new JsonObject { ["process_path_regex"] = Strings(entry.Processes.Select(ProcessPathRegex)), ["outbound"] = outbound });
            }
            if (entry.Domains.Count > 0)
            {
                ruleMap[rules.Count] = entry.Id;
                rules.Add(new JsonObject { ["domain_suffix"] = Strings(entry.Domains), ["outbound"] = outbound });
            }
        }

        var dnsServers = new JsonArray
        {
            new JsonObject { ["type"] = "udp", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = DefaultTag },
        };
        var dnsRules = new JsonArray();
        if (UsableLanDns(input.LanDns) is { } lanDns)
        {
            dnsServers.Add(new JsonObject { ["type"] = "udp", ["tag"] = "lan-dns", ["server"] = lanDns, ["detour"] = LanTag });
            dnsRules.Add(new JsonObject { ["domain_suffix"] = Strings(RouterLocalSuffixes), ["server"] = "lan-dns" });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "debug", ["timestamp"] = true },
            // ipv4_only: the TUN only captures IPv4, so an AAAA answer would send traffic around it (and around the rules).
            ["dns"] = new JsonObject { ["servers"] = dnsServers, ["rules"] = dnsRules, ["final"] = "remote", ["strategy"] = "ipv4_only" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "tun", ["tag"] = "tun-in", ["interface_name"] = "NetRoute",
                    ["address"] = Strings(["172.19.0.1/30"]),
                    ["auto_route"] = true, ["strict_route"] = false,
                    ["route_exclude_address"] = Strings(ExcludedRanges),
                },
            },
            ["outbounds"] = new JsonArray
            {
                new JsonObject { ["type"] = "direct", ["tag"] = PhoneTag, ["bind_interface"] = input.PhoneInterface },
                new JsonObject { ["type"] = "direct", ["tag"] = LanTag, ["bind_interface"] = input.LanInterface },
                new JsonObject
                {
                    ["type"] = "selector", ["tag"] = LanOnlyTag, ["outbounds"] = Strings([LanTag, PhoneTag]),
                    ["default"] = input.LanRulesOnPhone ? PhoneTag : LanTag,
                    // Without this a switch only affects new connections; open ones would stay on the old exit.
                    ["interrupt_exist_connections"] = true,
                },
                new JsonObject
                {
                    ["type"] = "selector", ["tag"] = DefaultTag, ["outbounds"] = Strings([PhoneTag, LanTag]),
                    ["default"] = input.DefaultExit == RouteExit.Lan ? LanTag : PhoneTag,
                    ["interrupt_exist_connections"] = true,
                },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = rules, ["final"] = DefaultTag,
                // sing-box 1.14 refuses direct outbounds without a domain resolver ("missing route.default_domain_resolver").
                ["default_domain_resolver"] = "remote",
            },
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{input.ApiPort}", ["secret"] = input.ApiSecret,
                },
            },
        };
        return new SingBoxConfig(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ruleMap);
    }

    /// A loopback or unparsable address is a local stub resolver (or garbage), not the router: treat it as absent.
    static string? UsableLanDns(string? value) =>
        IPAddress.TryParse(value, out var ip) && !IPAddress.IsLoopback(ip) ? ip.ToString() : null;

    static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
}
