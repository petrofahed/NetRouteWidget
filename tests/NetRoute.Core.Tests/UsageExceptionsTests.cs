namespace NetRoute.Core.Tests;

public class UsageExceptionsTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),                                      // LAN exception
        new("claude", "ai", "AI", "Claude", ["claude.exe"], [], true, RouteExit.Phone),                            // phone exception
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    [Fact]
    public void A_lan_item_is_an_exception_of_the_phone_profile_while_switched_on()
    {
        var on = new SmartRoutingSettings();
        var off = on.WithItem("youtube", false);

        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, on, RouteExit.Phone, "youtube"));
        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, off, RouteExit.Phone, "youtube"));
        Assert.False(UsageExceptions.Toggle(Catalog, on, RouteExit.Phone, "youtube").IsItemOn(Catalog[0]));
        Assert.True(UsageExceptions.Toggle(Catalog, off, RouteExit.Phone, "youtube").IsItemOn(Catalog[0]));
    }

    [Fact]
    public void An_item_of_the_other_list_cannot_be_changed_from_here()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, false, RouteExit.Phone), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "claude"));
        Assert.Equal(new ExceptionState(false, false, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "youtube"));
        Assert.Equal(s, UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, "claude"));
    }

    [Fact]
    public void In_the_lan_profile_phone_items_and_carve_outs_are_the_exceptions()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(true, true, RouteExit.Phone), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "claude"));
        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "vscode-updates"));
        Assert.False(UsageExceptions.Toggle(Catalog, s, RouteExit.Lan, "claude").IsItemOn(Catalog[1]));
    }

    [Fact]
    public void An_application_row_creates_then_disables_then_re_enables_a_rule_in_the_active_list()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "app:chrome.exe"));
        var added = UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, "app:chrome.exe");
        Assert.Equal(new UserRule(UserRuleType.App, "chrome.exe", true), Assert.Single(added.UserRules));
        Assert.Empty(added.PhoneUserRules);
        Assert.True(UsageExceptions.Describe(Catalog, added, RouteExit.Phone, "app:chrome.exe").InException);

        var removed = UsageExceptions.Toggle(Catalog, added, RouteExit.Phone, "app:chrome.exe");
        Assert.False(Assert.Single(removed.UserRules).Enabled); // the rule stays listed on the Config tab, switched off
        Assert.False(UsageExceptions.Describe(Catalog, removed, RouteExit.Phone, "app:chrome.exe").InException);

        var again = UsageExceptions.Toggle(Catalog, removed, RouteExit.Phone, "app:chrome.exe");
        Assert.True(Assert.Single(again.UserRules).Enabled);
    }

    [Fact]
    public void In_the_lan_profile_an_application_goes_to_the_phone_list()
    {
        var added = UsageExceptions.Toggle(Catalog, new SmartRoutingSettings(), RouteExit.Lan, "app:qbittorrent.exe");

        Assert.Empty(added.UserRules);
        Assert.Equal(new UserRule(UserRuleType.App, "qbittorrent.exe", true), Assert.Single(added.PhoneUserRules));
        Assert.Equal(new ExceptionState(true, true, RouteExit.Phone), UsageExceptions.Describe(Catalog, added, RouteExit.Lan, "app:qbittorrent.exe"));
        // The same app is not an exception of the other profile.
        Assert.False(UsageExceptions.Describe(Catalog, added, RouteExit.Phone, "app:qbittorrent.exe").InException);
    }

    [Fact]
    public void A_website_rule_row_toggles_its_rule_in_the_active_list_only()
    {
        var s = new SmartRoutingSettings().WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"));

        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "user:website:dropbox.com"));
        Assert.False(Assert.Single(UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, "user:website:dropbox.com").UserRules).Enabled);
        // In the LAN profile that rule belongs to the other list: not changeable here.
        Assert.False(UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "user:website:dropbox.com").CanChange);
    }

    [Theory]
    [InlineData("other")]
    [InlineData("unattributed")]
    [InlineData("app:")]
    [InlineData("app:notanexe")]
    [InlineData("user:website:not-a-rule.com")]
    public void Rows_that_cannot_be_an_exception_are_locked_and_never_change_the_settings(string key)
    {
        var s = new SmartRoutingSettings();

        Assert.False(UsageExceptions.Describe(Catalog, s, RouteExit.Phone, key).CanChange);
        Assert.Equal(s, UsageExceptions.Toggle(Catalog, s, RouteExit.Phone, key));
    }
}
