namespace NetRoute.Core.Tests;

public class UsageExceptionsTests
{
    [Theory]
    [InlineData(ConnectionId.Phone, ConnectionId.Lan)]
    [InlineData(ConnectionId.Phone, ConnectionId.Wifi)]
    [InlineData(ConnectionId.Lan, ConnectionId.Phone)]
    [InlineData(ConnectionId.Lan, ConnectionId.Wifi)]
    [InlineData(ConnectionId.Wifi, ConnectionId.Lan)]
    [InlineData(ConnectionId.Wifi, ConnectionId.Phone)]
    public void Explicit_destinations_update_only_the_captured_profile_and_keep_identity(ConnectionId profile, ConnectionId exit)
    {
        var old = new UserRule(UserRuleType.App, "editor.exe");
        var settings = new SmartRoutingSettings().WithUserRule(profile, old);
        var changed = UsageExceptions.Set([], settings, profile, "app:editor.exe", true, exit);
        var rule = Assert.Single(changed.RulesFor(profile));
        Assert.Equal(exit, rule.Exit);
        Assert.Equal(UserRule.IdOf(old), UserRule.IdOf(rule));
        Assert.All(Connections.All.Where(id => id != profile), id => Assert.Empty(changed.RulesFor(id)));
        Assert.Equal(SmartRoutingPolicy.TargetOf(exit), UsageExceptions.Describe([], changed, profile, "app:editor.exe").Target);
        Assert.Equal(SmartRoutingPolicy.TargetOf(exit), RuleSet.Build([], changed, profile).Entries.Single(entry => entry.Id == UserRule.IdOf(profile, rule)).Target);
    }

    [Fact]
    public void Legacy_toggle_preserves_null_but_explicit_LAN_is_distinct_from_Home()
    {
        var rule = new UserRule(UserRuleType.App, "editor.exe");
        var settings = new SmartRoutingSettings().WithUserRule(ConnectionId.Phone, rule);
        var off = UsageExceptions.Set([], settings, ConnectionId.Phone, "app:editor.exe", false);
        Assert.Null(Assert.Single(off.UserRules).Exit);
        Assert.Equal(RuleTarget.Home, UsageExceptions.Describe([], settings, ConnectionId.Phone, "app:editor.exe").Target);
        var explicitLan = UsageExceptions.Set([], settings, ConnectionId.Phone, "app:editor.exe", true, ConnectionId.Lan);
        Assert.Equal(RuleTarget.Lan, UsageExceptions.Describe([], explicitLan, ConnectionId.Phone, "app:editor.exe").Target);
    }

    [Fact]
    public void Menu_actions_capture_key_profile_and_all_non_main_destinations()
    {
        var actions = UsageExceptions.Actions("app:editor.exe", ConnectionId.Wifi, new ExceptionState(false, true, ConnectionId.Phone));
        Assert.Equal(Connections.All.Where(id => id != ConnectionId.Wifi), actions.Select(a => a.Destination!.Value).ToArray());
        Assert.All(actions, a => { Assert.Equal("app:editor.exe", a.Key); Assert.Equal(ConnectionId.Wifi, a.Profile); });
    }

    [Fact]
    // A catalog Usage key is an aggregate built-in item, not an editable custom app rule.
    public void Builtin_menu_keeps_Home_and_never_rewrites_catalog_destination()
    {
        var settings = new SmartRoutingSettings().WithItem("youtube", false);
        var state = UsageExceptions.Describe(Catalog, settings, ConnectionId.Phone, "youtube");
        var action = Assert.Single(UsageExceptions.Actions("youtube", ConnectionId.Phone, state));
        Assert.Null(action.Destination);
        Assert.Equal(RuleTarget.Home, state.Target);
        var changed = UsageExceptions.Set(Catalog, settings, action.Profile, action.Key, action.On, action.Destination);
        Assert.True(changed.IsItemOn(Catalog[0]));
        Assert.Empty(changed.UserRules);
    }

    [Fact]
    public void Open_menu_intent_edits_captured_profile_against_latest_settings()
    {
        var action = UsageExceptions.Actions("app:editor.exe", ConnectionId.Wifi, new(false, true, ConnectionId.Phone))
            .Single(a => a.Destination == ConnectionId.Lan);
        var rule = new UserRule(UserRuleType.App, "editor.exe", false, ConnectionId.Phone);
        var latest = new SmartRoutingSettings().WithUserRule(ConnectionId.Phone, rule)
            .WithUserRule(ConnectionId.Wifi, rule);
        var changed = UsageExceptions.Set([], latest, action.Profile, action.Key, action.On, action.Destination);
        Assert.Equal(ConnectionId.Phone, Assert.Single(changed.UserRules).Exit);
        Assert.False(Assert.Single(changed.UserRules).Enabled);
        Assert.Equal(ConnectionId.Lan, Assert.Single(changed.WifiUserRules).Exit);
        Assert.True(Assert.Single(changed.WifiUserRules).Enabled);
        var disabled = UsageExceptions.Set([], changed, ConnectionId.Wifi, action.Key, false);
        Assert.Equal(ConnectionId.Lan, Assert.Single(disabled.WifiUserRules).Exit);
    }

    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),                                      // LAN exception
        new("claude", "ai", "AI", "Claude", ["claude.exe"], [], true, RouteExit.Phone),                            // phone exception
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    /// What a click does when the menu displayed the current state.
    static SmartRoutingSettings Toggle(IReadOnlyList<RuleItem> catalog, SmartRoutingSettings s, ConnectionId profile, string key) =>
        UsageExceptions.Set(catalog, s, profile, key, !UsageExceptions.Describe(catalog, s, profile, key).InException);

    [Fact]
    public void A_lan_item_is_an_exception_of_the_phone_profile_while_switched_on()
    {
        var on = new SmartRoutingSettings();
        var off = on.WithItem("youtube", false);

        Assert.Equal(new ExceptionState(true, true, ConnectionId.Lan, Home: true) { FixedTarget = true }, UsageExceptions.Describe(Catalog, on, ConnectionId.Phone, "youtube"));
        Assert.Equal(new ExceptionState(false, true, ConnectionId.Lan, Home: true) { FixedTarget = true }, UsageExceptions.Describe(Catalog, off, ConnectionId.Phone, "youtube"));
        Assert.False(Toggle(Catalog, on, ConnectionId.Phone, "youtube").IsItemOn(Catalog[0]));
        Assert.True(Toggle(Catalog, off, ConnectionId.Phone, "youtube").IsItemOn(Catalog[0]));
    }

    [Fact]
    public void An_item_of_the_other_list_cannot_be_changed_from_here()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, false, ConnectionId.Phone) { FixedTarget = true }, UsageExceptions.Describe(Catalog, s, ConnectionId.Phone, "claude"));
        Assert.Equal(new ExceptionState(false, false, ConnectionId.Lan, Home: true) { FixedTarget = true }, UsageExceptions.Describe(Catalog, s, ConnectionId.Lan, "youtube"));
        Assert.Equal(s, Toggle(Catalog, s, ConnectionId.Phone, "claude"));
    }

    [Fact]
    public void In_the_lan_profile_phone_items_and_carve_outs_are_the_exceptions()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(true, true, ConnectionId.Phone) { FixedTarget = true }, UsageExceptions.Describe(Catalog, s, ConnectionId.Lan, "claude"));
        Assert.Equal(new ExceptionState(true, true, ConnectionId.Lan, Home: true) { FixedTarget = true }, UsageExceptions.Describe(Catalog, s, ConnectionId.Lan, "vscode-updates"));
        Assert.False(Toggle(Catalog, s, ConnectionId.Lan, "claude").IsItemOn(Catalog[1]));
    }

    [Fact]
    public void An_application_row_creates_then_disables_then_re_enables_a_rule_in_the_active_list()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(new ExceptionState(false, true, ConnectionId.Lan, Home: true), UsageExceptions.Describe(Catalog, s, ConnectionId.Phone, "app:chrome.exe"));
        var added = Toggle(Catalog, s, ConnectionId.Phone, "app:chrome.exe");
        Assert.Equal(new UserRule(UserRuleType.App, "chrome.exe", true), Assert.Single(added.UserRules));
        Assert.Empty(added.PhoneUserRules);
        Assert.True(UsageExceptions.Describe(Catalog, added, ConnectionId.Phone, "app:chrome.exe").InException);

        var removed = Toggle(Catalog, added, ConnectionId.Phone, "app:chrome.exe");
        Assert.False(Assert.Single(removed.UserRules).Enabled); // the rule stays listed on the Config tab, switched off
        Assert.False(UsageExceptions.Describe(Catalog, removed, ConnectionId.Phone, "app:chrome.exe").InException);

        var again = Toggle(Catalog, removed, ConnectionId.Phone, "app:chrome.exe");
        Assert.True(Assert.Single(again.UserRules).Enabled);
    }

    [Fact]
    public void In_the_lan_profile_an_application_goes_to_the_phone_list()
    {
        var added = Toggle(Catalog, new SmartRoutingSettings(), ConnectionId.Lan, "app:qbittorrent.exe");

        Assert.Empty(added.UserRules);
        Assert.Equal(new UserRule(UserRuleType.App, "qbittorrent.exe", true), Assert.Single(added.PhoneUserRules));
        Assert.Equal(new ExceptionState(true, true, ConnectionId.Phone), UsageExceptions.Describe(Catalog, added, ConnectionId.Lan, "app:qbittorrent.exe"));
        // The same app is not an exception of the other profile.
        Assert.False(UsageExceptions.Describe(Catalog, added, ConnectionId.Phone, "app:qbittorrent.exe").InException);
    }

    [Fact]
    public void A_website_rule_row_toggles_its_rule_in_the_active_list_only()
    {
        var s = new SmartRoutingSettings().WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"));

        Assert.Equal(new ExceptionState(true, true, ConnectionId.Lan, Home: true), UsageExceptions.Describe(Catalog, s, ConnectionId.Phone, "user:website:dropbox.com"));
        Assert.False(Assert.Single(Toggle(Catalog, s, ConnectionId.Phone, "user:website:dropbox.com").UserRules).Enabled);
        // In the LAN profile that rule belongs to the other list: not changeable here.
        Assert.False(UsageExceptions.Describe(Catalog, s, ConnectionId.Lan, "user:website:dropbox.com").CanChange);
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

        Assert.False(UsageExceptions.Describe(Catalog, s, ConnectionId.Phone, key).CanChange);
        Assert.Equal(s, Toggle(Catalog, s, ConnectionId.Phone, key));
    }

    [Fact]
    public void Set_switches_an_item_on_and_off_and_is_idempotent()
    {
        var s = new SmartRoutingSettings();

        var off = UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "youtube", false);
        Assert.False(off.IsItemOn(Catalog[0]));
        Assert.Equal(off, UsageExceptions.Set(Catalog, off, ConnectionId.Phone, "youtube", false)); // second time: nothing changes
        Assert.True(UsageExceptions.Set(Catalog, off, ConnectionId.Phone, "youtube", true).IsItemOn(Catalog[0]));
        Assert.Equal(s, UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "youtube", true)); // already on
    }

    [Fact]
    public void Set_adds_and_removes_an_app_row_and_is_idempotent()
    {
        var s = new SmartRoutingSettings();

        var added = UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "app:chrome.exe", true);
        Assert.Equal("chrome.exe", Assert.Single(added.UserRules).Value);
        Assert.Equal(added, UsageExceptions.Set(Catalog, added, ConnectionId.Phone, "app:chrome.exe", true));
        Assert.Equal(s, UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "app:chrome.exe", false)); // nothing to remove
        Assert.False(Assert.Single(UsageExceptions.Set(Catalog, added, ConnectionId.Phone, "app:chrome.exe", false).UserRules).Enabled);
    }

    [Fact]
    public void Set_switches_a_website_row()
    {
        var s = new SmartRoutingSettings { UserRules = [new UserRule(UserRuleType.Website, "dropbox.com")] };

        var off = UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "user:website:dropbox.com", false);

        Assert.False(Assert.Single(off.UserRules).Enabled);
        Assert.Equal(off, UsageExceptions.Set(Catalog, off, ConnectionId.Phone, "user:website:dropbox.com", false));
    }

    [Fact]
    public void Set_uses_the_profile_it_is_given_to_pick_the_list()
    {
        var s = new SmartRoutingSettings();

        var lan = UsageExceptions.Set(Catalog, s, ConnectionId.Lan, "app:qbittorrent.exe", true);
        var phone = UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "app:qbittorrent.exe", true);

        Assert.Empty(lan.UserRules);
        Assert.Equal("qbittorrent.exe", Assert.Single(lan.PhoneUserRules).Value);
        Assert.Empty(phone.PhoneUserRules);
        Assert.Equal("qbittorrent.exe", Assert.Single(phone.UserRules).Value);
    }

    [Fact]
    public void Set_leaves_a_locked_row_alone()
    {
        var s = new SmartRoutingSettings();

        Assert.Equal(s, UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "claude", true));
        Assert.Equal(s, UsageExceptions.Set(Catalog, s, ConnectionId.Phone, "other", true));
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

        Assert.Equal(new ExceptionState(true, true, ConnectionId.Lan, Home: true), UsageExceptions.Describe(ClaimCatalog, s, ConnectionId.Phone, "vscode"));
        var off = UsageExceptions.Set(ClaimCatalog, s, ConnectionId.Phone, "vscode", false);
        Assert.False(Assert.Single(off.UserRules).Enabled);
        Assert.Equal(new ExceptionState(false, true, ConnectionId.Lan, Home: true), UsageExceptions.Describe(ClaimCatalog, off, ConnectionId.Phone, "vscode"));
        Assert.True(Assert.Single(UsageExceptions.Set(ClaimCatalog, off, ConnectionId.Phone, "vscode", true).UserRules).Enabled);
        Assert.False(UsageExceptions.Describe(ClaimCatalog, new SmartRoutingSettings(), ConnectionId.Phone, "vscode").CanChange); // no such rule: locked
    }

    [Fact]
    public void A_phone_rule_for_an_app_a_lan_item_claims_is_what_the_row_acts_on_in_the_lan_profile()
    {
        var s = new SmartRoutingSettings { PhoneUserRules = [new UserRule(UserRuleType.App, "STEAM.exe", Enabled: false)] };

        Assert.Equal(new ExceptionState(false, true, ConnectionId.Phone), UsageExceptions.Describe(ClaimCatalog, s, ConnectionId.Lan, "steam"));
        var on = UsageExceptions.Set(ClaimCatalog, s, ConnectionId.Lan, "steam", true);
        Assert.True(Assert.Single(on.PhoneUserRules).Enabled);
        Assert.Empty(on.UserRules);
        Assert.False(UsageExceptions.Describe(ClaimCatalog, new SmartRoutingSettings(), ConnectionId.Lan, "steam").CanChange);
    }
}
