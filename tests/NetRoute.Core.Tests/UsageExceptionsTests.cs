namespace NetRoute.Core.Tests;

public class UsageExceptionsTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),                                      // LAN exception
        new("claude", "ai", "AI", "Claude", ["claude.exe"], [], true, RouteExit.Phone),                            // phone exception
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    /// What a click does when the menu displayed the current state.
    static SmartRoutingSettings Toggle(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings s, RouteExit profile, string key) =>
        UsageExceptions.Set(catalog, s, profile, key, !UsageExceptions.Describe(catalog, s, profile, key).InException);

    [Fact]
    public void A_lan_item_is_an_exception_of_the_phone_profile_while_switched_on()
    {
        var on = new SmartRoutingSettings();
        var off = on.WithItem("youtube", false);

        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, on, RouteExit.Phone, "youtube"));
        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, off, RouteExit.Phone, "youtube"));
        Assert.False(Toggle(Catalog, on, RouteExit.Phone, "youtube").IsItemOn(Catalog[0]));
        Assert.True(Toggle(Catalog, off, RouteExit.Phone, "youtube").IsItemOn(Catalog[0]));
    }

    [Fact]
    public void An_item_of_the_other_list_cannot_be_changed_from_here()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, false, RouteExit.Phone), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "claude"));
        Assert.Equal(new ExceptionState(false, false, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "youtube"));
        Assert.Equal(s, Toggle(Catalog, s, RouteExit.Phone, "claude"));
    }

    [Fact]
    public void In_the_lan_profile_phone_items_and_carve_outs_are_the_exceptions()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(true, true, RouteExit.Phone), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "claude"));
        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Lan, "vscode-updates"));
        Assert.False(Toggle(Catalog, s, RouteExit.Lan, "claude").IsItemOn(Catalog[1]));
    }

    [Fact]
    public void An_application_row_creates_then_disables_then_re_enables_a_rule_in_the_active_list()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), UsageExceptions.Describe(Catalog, s, RouteExit.Phone, "app:chrome.exe"));
        var added = Toggle(Catalog, s, RouteExit.Phone, "app:chrome.exe");
        Assert.Equal(new UserRule(UserRuleType.App, "chrome.exe", true), Assert.Single(added.UserRules));
        Assert.Empty(added.PhoneUserRules);
        Assert.True(UsageExceptions.Describe(Catalog, added, RouteExit.Phone, "app:chrome.exe").InException);

        var removed = Toggle(Catalog, added, RouteExit.Phone, "app:chrome.exe");
        Assert.False(Assert.Single(removed.UserRules).Enabled); // the rule stays listed on the Config tab, switched off
        Assert.False(UsageExceptions.Describe(Catalog, removed, RouteExit.Phone, "app:chrome.exe").InException);

        var again = Toggle(Catalog, removed, RouteExit.Phone, "app:chrome.exe");
        Assert.True(Assert.Single(again.UserRules).Enabled);
    }

    [Fact]
    public void In_the_lan_profile_an_application_goes_to_the_phone_list()
    {
        var added = Toggle(Catalog, new SmartRoutingSettings(), RouteExit.Lan, "app:qbittorrent.exe");

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
        Assert.False(Assert.Single(Toggle(Catalog, s, RouteExit.Phone, "user:website:dropbox.com").UserRules).Enabled);
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
        Assert.Equal(s, Toggle(Catalog, s, RouteExit.Phone, key));
    }

    [Fact]
    public void Set_switches_an_item_on_and_off_and_is_idempotent()
    {
        var s = new SmartRoutingSettings();

        var off = UsageExceptions.Set(Catalog, s, RouteExit.Phone, "youtube", false);
        Assert.False(off.IsItemOn(Catalog[0]));
        Assert.Equal(off, UsageExceptions.Set(Catalog, off, RouteExit.Phone, "youtube", false)); // second time: nothing changes
        Assert.True(UsageExceptions.Set(Catalog, off, RouteExit.Phone, "youtube", true).IsItemOn(Catalog[0]));
        Assert.Equal(s, UsageExceptions.Set(Catalog, s, RouteExit.Phone, "youtube", true)); // already on
    }

    [Fact]
    public void Set_adds_and_removes_an_app_row_and_is_idempotent()
    {
        var s = new SmartRoutingSettings();

        var added = UsageExceptions.Set(Catalog, s, RouteExit.Phone, "app:chrome.exe", true);
        Assert.Equal("chrome.exe", Assert.Single(added.UserRules).Value);
        Assert.Equal(added, UsageExceptions.Set(Catalog, added, RouteExit.Phone, "app:chrome.exe", true));
        Assert.Equal(s, UsageExceptions.Set(Catalog, s, RouteExit.Phone, "app:chrome.exe", false)); // nothing to remove
        Assert.False(Assert.Single(UsageExceptions.Set(Catalog, added, RouteExit.Phone, "app:chrome.exe", false).UserRules).Enabled);
    }

    [Fact]
    public void Set_switches_a_website_row()
    {
        var s = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };

        var off = UsageExceptions.Set(Catalog, s, RouteExit.Phone, "user:website:dropbox.com", false);

        Assert.False(Assert.Single(off.UserRules).Enabled);
        Assert.Equal(off, UsageExceptions.Set(Catalog, off, RouteExit.Phone, "user:website:dropbox.com", false));
    }

    [Fact]
    public void Set_uses_the_profile_it_is_given_to_pick_the_list()
    {
        var s = new SmartRoutingSettings();

        var lan = UsageExceptions.Set(Catalog, s, RouteExit.Lan, "app:qbittorrent.exe", true);
        var phone = UsageExceptions.Set(Catalog, s, RouteExit.Phone, "app:qbittorrent.exe", true);

        Assert.Empty(lan.UserRules);
        Assert.Equal("qbittorrent.exe", Assert.Single(lan.PhoneUserRules).Value);
        Assert.Empty(phone.PhoneUserRules);
        Assert.Equal("qbittorrent.exe", Assert.Single(phone.UserRules).Value);
    }

    [Fact]
    public void Set_leaves_a_locked_row_alone()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(s, UsageExceptions.Set(Catalog, s, RouteExit.Phone, "claude", true));
        Assert.Equal(s, UsageExceptions.Set(Catalog, s, RouteExit.Phone, "other", true));
    }

    static readonly IReadOnlyList<RuleItem> ClaimCatalog =
    [
        new("vscode", "ai", "AI", "VS Code", ["Code.exe"], [], true, RouteExit.Phone),
        new("steam", "games", "Games", "Steam", ["steam.exe"], [], true),
    ];

    [Fact]
    public void A_user_rule_for_an_app_a_builtin_item_of_the_other_list_claims_is_what_the_row_acts_on_in_the_phone_profile()
    {
        var s = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.App, "code.exe")] };

        Assert.Equal(new ExceptionState(true, true, RouteExit.Lan), UsageExceptions.Describe(ClaimCatalog, s, RouteExit.Phone, "vscode"));
        var off = UsageExceptions.Set(ClaimCatalog, s, RouteExit.Phone, "vscode", false);
        Assert.False(Assert.Single(off.UserRules).Enabled);
        Assert.Equal(new ExceptionState(false, true, RouteExit.Lan), UsageExceptions.Describe(ClaimCatalog, off, RouteExit.Phone, "vscode"));
        Assert.True(Assert.Single(UsageExceptions.Set(ClaimCatalog, off, RouteExit.Phone, "vscode", true).UserRules).Enabled);
        Assert.False(UsageExceptions.Describe(ClaimCatalog, new SmartRoutingSettings(), RouteExit.Phone, "vscode").CanChange); // no such rule: locked
    }

    [Fact]
    public void A_phone_rule_for_an_app_a_lan_item_claims_is_what_the_row_acts_on_in_the_lan_profile()
    {
        var s = new SmartRoutingSettings { PhoneUserRules = [new UserRule(UserRuleType.App, "STEAM.exe", Enabled: false)] };

        Assert.Equal(new ExceptionState(false, true, RouteExit.Phone), UsageExceptions.Describe(ClaimCatalog, s, RouteExit.Lan, "steam"));
        var on = UsageExceptions.Set(ClaimCatalog, s, RouteExit.Lan, "steam", true);
        Assert.True(Assert.Single(on.PhoneUserRules).Enabled);
        Assert.Empty(on.UserRules);
        Assert.False(UsageExceptions.Describe(ClaimCatalog, new SmartRoutingSettings(), RouteExit.Lan, "steam").CanChange);
    }
}
