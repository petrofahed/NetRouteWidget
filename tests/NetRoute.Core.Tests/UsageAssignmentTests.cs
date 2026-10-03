namespace NetRoute.Core.Tests;

public class UsageAssignmentTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];

    [Fact]
    public void A_built_in_item_row_follows_the_item_switch()
    {
        var on = new SmartRoutingSettings();
        var off = on.WithItem("youtube", false);

        Assert.Equal(new Assignment(GoesVia.Lan, true), UsageAssignment.Describe(Catalog, on, "youtube"));
        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, off, "youtube"));
        Assert.False(UsageAssignment.Set(Catalog, on, "youtube", toLan: false).IsItemOn(Catalog[0]));
        Assert.True(UsageAssignment.Set(Catalog, off, "youtube", toLan: true).IsItemOn(Catalog[0]));
    }

    [Fact]
    public void An_application_row_without_a_rule_creates_one_when_sent_to_the_lan()
    {
        var none = new SmartRoutingSettings();

        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, none, "app:chrome.exe"));
        var after = UsageAssignment.Set(Catalog, none, "app:chrome.exe", toLan: true);

        var rule = Assert.Single(after.UserRules);
        Assert.Equal(new UserRule(UserRuleType.App, "chrome.exe", true), rule);
        Assert.Equal(new Assignment(GoesVia.Lan, true), UsageAssignment.Describe(Catalog, after, "app:chrome.exe"));
    }

    [Fact]
    public void Sending_an_application_without_a_rule_to_the_phone_changes_nothing()
    {
        var none = new SmartRoutingSettings();

        Assert.Equal(none, UsageAssignment.Set(Catalog, none, "app:chrome.exe", toLan: false));
    }

    [Fact]
    public void An_application_row_with_a_rule_enables_and_disables_it_and_keeps_it_listed()
    {
        var on = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.App, "chrome.exe")] };

        var off = UsageAssignment.Set(Catalog, on, "app:chrome.exe", toLan: false);
        var rule = Assert.Single(off.UserRules);
        Assert.False(rule.Enabled);
        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, off, "app:chrome.exe"));

        var back = UsageAssignment.Set(Catalog, off, "app:chrome.exe", toLan: true);
        Assert.True(Assert.Single(back.UserRules).Enabled);
    }

    [Fact]
    public void A_user_website_row_enables_and_disables_its_rule()
    {
        var on = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };

        var off = UsageAssignment.Set(Catalog, on, "user:website:dropbox.com", toLan: false);

        Assert.False(Assert.Single(off.UserRules).Enabled);
        Assert.Equal(new Assignment(GoesVia.Phone, true), UsageAssignment.Describe(Catalog, off, "user:website:dropbox.com"));
    }

    [Theory]
    [InlineData("other")]
    [InlineData("unattributed")]
    [InlineData("user:website:not-a-rule.com")]
    [InlineData("app:")]
    [InlineData("app:notanexe")]
    public void Rows_that_cannot_be_assigned_are_locked_and_never_change_the_settings(string key)
    {
        var settings = new SmartRoutingSettings();

        Assert.False(UsageAssignment.Describe(Catalog, settings, key).CanChange);
        Assert.Equal(settings, UsageAssignment.Set(Catalog, settings, key, toLan: true));
    }
}
