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
}
