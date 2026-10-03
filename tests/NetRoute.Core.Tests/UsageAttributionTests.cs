namespace NetRoute.Core.Tests;

public class UsageAttributionTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com", "googlevideo.com"], true),
        new("onedrive", "sync", "Cloud sync", "OneDrive", ["OneDrive.exe"], ["onedrive.com"], true),
    ];

    static UsageAttribution Build(SmartRoutingSettings? settings = null) =>
        UsageAttribution.Build(Catalog, settings ?? new SmartRoutingSettings());

    [Fact]
    public void A_rule_that_lists_the_application_wins_over_a_domain_rule()
    {
        Assert.Equal("onedrive", Build().Resolve("OneDrive.exe", "www.youtube.com"));
    }

    [Theory]
    [InlineData("chrome.exe", "www.youtube.com")]
    [InlineData(null, "r1---sn.googlevideo.com")]
    public void A_domain_rule_claims_the_connection_when_no_process_rule_does(string? process, string host)
    {
        Assert.Equal("youtube", Build().Resolve(process, host));
    }

    [Fact]
    public void Other_traffic_of_an_application_counts_under_the_application_lower_cased()
    {
        Assert.Equal("app:chrome.exe", Build().Resolve("Chrome.EXE", "example.com"));
        Assert.Equal("app:chrome.exe", Build().Resolve("chrome.exe", null));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "example.com")]
    [InlineData("", "")]
    [InlineData("  ", null)]
    public void Without_an_application_or_a_matching_host_the_row_is_Other(string? process, string? host)
    {
        Assert.Equal(UsageAttribution.OtherKey, Build().Resolve(process, host));
    }

    [Fact]
    public void A_user_app_rule_keeps_the_application_key_whether_it_is_on_or_off()
    {
        foreach (var enabled in new[] { true, false })
        {
            var settings = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.App, "qbittorrent.exe", enabled)] };

            Assert.Equal("app:qbittorrent.exe", Build(settings).Resolve("qBittorrent.exe", "tracker.example"));
        }
    }

    [Fact]
    public void A_switched_off_item_still_gets_its_row_so_the_phone_usage_is_visible()
    {
        var settings = new SmartRoutingSettings().WithItem("youtube", false);

        Assert.Equal("youtube", Build(settings).Resolve("chrome.exe", "www.youtube.com"));
    }

    [Fact]
    public void A_user_website_rule_is_a_row_of_its_own()
    {
        var settings = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };

        Assert.Equal("user:website:dropbox.com", Build(settings).Resolve("chrome.exe", "dl.dropbox.com"));
    }

    [Fact]
    public void Display_names()
    {
        var settings = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };
        var a = Build(settings);

        Assert.Equal("YouTube", a.DisplayName("youtube"));
        Assert.Equal("OneDrive", a.DisplayName("onedrive"));
        Assert.Equal("chrome", a.DisplayName("app:chrome.exe"));
        Assert.Equal("dropbox.com", a.DisplayName("user:website:dropbox.com"));
        Assert.Equal("Other", a.DisplayName(UsageAttribution.OtherKey));
        Assert.Equal("Unattributed (short connections)", a.DisplayName(UsageAttribution.UnattributedKey));
    }

    [Fact]
    public void A_row_from_history_whose_rule_was_deleted_still_has_a_readable_name()
    {
        var a = Build();

        Assert.Equal("gone.com", a.DisplayName("user:website:gone.com"));
        Assert.Equal("removed-item", a.DisplayName("removed-item"));
    }
}
