namespace NetRoute.Core.Tests;

public class RuleSetTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com", "googlevideo.com"], true),
        new("facebook", "video", "Video", "Facebook", [], ["facebook.com"], true),
        new("onedrive", "sync", "Sync", "OneDrive", ["OneDrive.exe", "OneDrive.Sync.Service.exe"], ["onedrive.live.com"], true),
    ];

    [Fact]
    public void Build_keeps_enabled_items_and_enabled_user_rules()
    {
        var settings = new SmartRoutingSettings()
            .WithItem("facebook", false)
            .WithUserRule(new UserRule(UserRuleType.App, "qbittorrent.exe"))
            .WithUserRule(new UserRule(UserRuleType.Website, "netflix.com", Enabled: false));

        var set = RuleSet.Build(Catalog, settings);

        Assert.Equal(new[] { "youtube", "onedrive", "user:app:qbittorrent.exe" }, set.Entries.Select(e => e.Id));
        var user = set.Entries[2];
        Assert.Equal("qbittorrent.exe", user.Name);
        Assert.Equal(new[] { "qbittorrent.exe" }, user.Processes);
        Assert.Empty(user.Domains);
    }

    [Theory]
    [InlineData("youtube.com", "youtube")]
    [InlineData("rr1---sn-q4flrnld.googlevideo.com", "youtube")]
    [InlineData("notyoutube.com", null)]
    [InlineData(null, null)]
    public void FindByHost_matches_domain_suffixes_only(string? host, string? expected)
    {
        Assert.Equal(expected, RuleSet.Build(Catalog, new()).FindByHost(host)?.Id);
    }

    [Fact]
    public void FindByProcess_is_case_insensitive()
    {
        var set = RuleSet.Build(Catalog, new());

        Assert.Equal("onedrive", set.FindByProcess("onedrive.sync.service.EXE")?.Id);
        Assert.Null(set.FindByProcess("chrome.exe"));
    }

    [Fact]
    public void Fingerprint_changes_when_rules_change()
    {
        var a = RuleSet.Build(Catalog, new()).Fingerprint;
        var b = RuleSet.Build(Catalog, new SmartRoutingSettings().WithItem("youtube", false)).Fingerprint;

        Assert.NotEqual(a, b);
        Assert.Equal(a, RuleSet.Build(Catalog, new()).Fingerprint);
    }

    [Fact]
    public void BuildAll_includes_items_and_rules_that_are_switched_off()
    {
        IReadOnlyList<RuleItem> catalog = [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];
        var settings = new SmartRoutingSettings
        {
            Items = new Dictionary<string, bool> { ["youtube"] = false },
            UserRules = [new UserRule(UserRuleType.App, "qbittorrent.exe", Enabled: false)],
        };

        var all = RuleSet.BuildAll(catalog, settings);
        var enabled = RuleSet.Build(catalog, settings);

        Assert.Equal(new[] { "youtube", "user:app:qbittorrent.exe" }, all.Entries.Select(e => e.Id));
        Assert.Empty(enabled.Entries);
    }

    static readonly IReadOnlyList<RuleItem> V4Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),                                       // LAN exception
        new("claude", "ai", "AI", "Claude", ["claude.exe"], ["claude.ai"], true, RouteExit.Phone),                  // phone exception
        new("vscode", "ai", "AI", "VS Code", ["Code.exe"], [], true, RouteExit.Phone),
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    [Fact]
    public void The_phone_profile_activates_the_lan_items_and_carve_outs_and_the_lan_rules()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "qbittorrent.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "ignored.exe")],
        };

        var set = RuleSet.Build(V4Catalog, settings, ConnectionId.Phone);

        Assert.Equal(new[] { "vscode-updates", "youtube", "user:app:qbittorrent.exe" }, set.Entries.Select(e => e.Id));
        Assert.All(set.Entries, e => Assert.Equal(RuleTarget.Home, e.Target));
    }

    [Fact]
    public void Carve_outs_come_first_in_the_lan_profile()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "ignored.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.Website, "example.com")],
        };

        var set = RuleSet.Build(V4Catalog, settings, ConnectionId.Lan);

        // The update domain (LAN) is matched before the code.exe rule (phone), so a VS Code update never rides the phone.
        // The LAN list stays active after the phone entries: those downloads wait for the LAN instead of healing onto 4G.
        Assert.Equal(new[] { "vscode-updates", "claude", "vscode", "phone-user:website:example.com", "youtube", "user:app:ignored.exe" },
            set.Entries.Select(e => e.Id));
        Assert.Equal(new[] { RuleTarget.Home, RuleTarget.Phone, RuleTarget.Phone, RuleTarget.Phone, RuleTarget.Home, RuleTarget.Home },
            set.Entries.Select(e => e.Target));
        Assert.True(set.Entries[0].CarveOut);
    }

    [Fact]
    public void Switched_off_items_and_disabled_rules_are_left_out_of_both_profiles()
    {
        var settings = new SmartRoutingSettings
        {
            PhoneUserRules = [new UserRule(UserRuleType.App, "x.exe", Enabled: false)],
        }.WithItem("claude", false).WithItem("vscode-updates", false);

        var set = RuleSet.Build(V4Catalog, settings, ConnectionId.Lan);

        Assert.Equal(new[] { "vscode", "youtube" }, set.Entries.Select(e => e.Id));
    }

    [Fact]
    public void The_lan_profile_leaves_out_lan_items_and_rules_that_are_switched_off()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "off.exe", Enabled: false)],
        }.WithItem("youtube", false);

        var set = RuleSet.Build(V4Catalog, settings, ConnectionId.Lan);

        Assert.DoesNotContain(set.Entries, e => e.Id == "youtube" || e.Id == "user:app:off.exe");
    }

    [Fact]
    public void The_two_argument_build_is_the_phone_profile()
    {
        var settings = new SmartRoutingSettings();

        Assert.Equal(
            RuleSet.Build(V4Catalog, settings, ConnectionId.Phone).Fingerprint,
            RuleSet.Build(V4Catalog, settings).Fingerprint);
    }

    [Fact]
    public void BuildAll_includes_both_lists_and_every_catalog_item()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "chrome.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "chrome.exe")],
        };

        var all = RuleSet.BuildAll(V4Catalog, settings);

        Assert.Equal(
            new[] { "youtube", "claude", "vscode", "vscode-updates", "user:app:chrome.exe", "phone-user:app:chrome.exe" },
            all.Entries.Select(e => e.Id));
    }

    [Fact]
    public void The_fingerprint_changes_with_the_profile()
    {
        var settings = new SmartRoutingSettings();

        Assert.NotEqual(
            RuleSet.Build(V4Catalog, settings, ConnectionId.Phone).Fingerprint,
            RuleSet.Build(V4Catalog, settings, ConnectionId.Lan).Fingerprint);
    }

    [Theory]
    [InlineData(ConnectionId.Phone, "user:app:own.exe", RuleTarget.Home)]
    [InlineData(ConnectionId.Lan, "phone-user:app:own.exe", RuleTarget.Phone)]
    [InlineData(ConnectionId.Wifi, "wifi-user:app:own.exe", RuleTarget.Phone)]
    public void Every_profile_keeps_carveouts_first_and_its_own_exception_target(ConnectionId profile, string id, RuleTarget target)
    {
        var settings = new SmartRoutingSettings().WithUserRule(profile, new(UserRuleType.App, "own.exe"));
        var set = RuleSet.Build(V4Catalog, settings, profile);

        Assert.Equal("vscode-updates", set.Entries[0].Id);
        Assert.Equal(RuleTarget.Home, set.Entries[0].Target);
        Assert.Equal(target, set.Entries.Single(entry => entry.Id == id).Target);
        Assert.Equal(RuleTarget.Home, set.Entries.Single(entry => entry.Id == "youtube").Target);
        Assert.Equal(profile != ConnectionId.Phone, set.Entries.Any(entry => entry.Id == "claude"));
    }

    [Theory]
    [InlineData(ConnectionId.Phone)]
    [InlineData(ConnectionId.Lan)]
    [InlineData(ConnectionId.Wifi)]
    public void Explicit_exits_are_literal_and_inactive_nonmetered_rules_are_inherited(ConnectionId profile)
    {
        var settings = new SmartRoutingSettings();
        foreach (var list in Connections.All)
        {
            settings = settings.WithUserRule(list, new(UserRuleType.App, "lan.exe", Exit: ConnectionId.Lan))
                .WithUserRule(list, new(UserRuleType.App, "wifi.exe", Exit: ConnectionId.Wifi))
                .WithUserRule(list, new(UserRuleType.App, "phone.exe", Exit: ConnectionId.Phone));
        }
        var set = RuleSet.Build([], settings, profile);
        Assert.Equal(7, set.Entries.Count); // three own, two non-metered rules from each other list
        Assert.Equal(set.Entries.Count, set.Entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(set.Entries.Where(entry => entry.Name == "lan.exe"), entry => Assert.Equal(RuleTarget.Lan, entry.Target));
        Assert.All(set.Entries.Where(entry => entry.Name == "wifi.exe"), entry => Assert.Equal(RuleTarget.Wifi, entry.Target));
        Assert.Equal(UserRule.IdOf(profile, new(UserRuleType.App, "phone.exe")), Assert.Single(set.Entries, entry => entry.Target == RuleTarget.Phone).Id);
        Assert.Equal(UserRule.IdOf(profile, new(UserRuleType.App, "lan.exe")), set.FindByProcess("lan.exe")!.Id);
    }

    [Fact]
    public void Fingerprint_tracks_only_included_rules_with_target_and_order()
    {
        var settings = new SmartRoutingSettings().WithUserRule(ConnectionId.Phone, new(UserRuleType.App, "home.exe"));
        var before = RuleSet.Build([], settings, ConnectionId.Phone).Fingerprint;
        var inactive = settings.WithUserRule(ConnectionId.Wifi, new(UserRuleType.App, "phone.exe", Exit: ConnectionId.Phone));
        Assert.Equal(before, RuleSet.Build([], inactive, ConnectionId.Phone).Fingerprint);
        var inherited = inactive.WithUserRule(ConnectionId.Wifi, new(UserRuleType.App, "lan.exe", Exit: ConnectionId.Lan));
        Assert.NotEqual(before, RuleSet.Build([], inherited, ConnectionId.Phone).Fingerprint);
        var literal = settings.WithUserRule(ConnectionId.Phone, new(UserRuleType.App, "home.exe", Exit: ConnectionId.Lan));
        Assert.NotEqual(before, RuleSet.Build([], literal, ConnectionId.Phone).Fingerprint);
        var reordered = settings.WithUserRule(ConnectionId.Phone, new(UserRuleType.App, "second.exe"));
        var entries = RuleSet.Build([], reordered, ConnectionId.Phone).Entries;
        Assert.NotEqual(new RuleSet(entries).Fingerprint, new RuleSet(entries.Reverse().ToList()).Fingerprint);
    }
}
