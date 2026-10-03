namespace NetRoute.Core.Tests;

public sealed class SmartRoutingSettingsTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-smart-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static RuleItem Item(string id, bool defaultOn = true) => new(id, "g", "G", id, [], [id + ".com"], defaultOn);

    [Theory]
    [InlineData(UserRuleType.Website, "https://www.YouTube.com/watch?v=x", "youtube.com")]
    [InlineData(UserRuleType.Website, "*.dropbox.com", "dropbox.com")]
    [InlineData(UserRuleType.Website, "  Netflix.com:443/ ", "netflix.com")]
    [InlineData(UserRuleType.App, @"C:\Apps\qbittorrent.exe", "qbittorrent.exe")]
    [InlineData(UserRuleType.App, "\"Steam.EXE\"", "Steam.EXE")]
    [InlineData(UserRuleType.Website, "example.com\n/x", "example.com")]
    [InlineData(UserRuleType.Website, "example.com.", "example.com")]
    [InlineData(UserRuleType.App, "\" foo.exe\"", "foo.exe")]
    public void UserRule_normalises_and_validates(UserRuleType type, string raw, string expected)
    {
        Assert.True(UserRule.TryCreate(type, raw, out var rule, out var error), error);
        Assert.Equal(new UserRule(type, expected), rule);
    }

    [Theory]
    [InlineData(UserRuleType.Website, "")]
    [InlineData(UserRuleType.Website, "not a site")]
    [InlineData(UserRuleType.Website, "localhost")]
    [InlineData(UserRuleType.App, "notepad")]
    [InlineData(UserRuleType.App, "   ")]
    public void UserRule_rejects_garbage_with_a_reason(UserRuleType type, string raw)
    {
        Assert.False(UserRule.TryCreate(type, raw, out var rule, out var error));
        Assert.Null(rule);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Items_default_to_the_catalog_and_can_be_overridden()
    {
        var settings = new SmartRoutingSettings();

        Assert.True(settings.IsItemOn(Item("youtube")));
        Assert.False(settings.IsItemOn(Item("off", defaultOn: false)));
        var changed = settings.WithItem("youtube", false);
        Assert.False(changed.IsItemOn(Item("youtube")));
        Assert.True(settings.IsItemOn(Item("youtube"))); // original untouched
    }

    [Fact]
    public void User_rules_are_added_replaced_and_removed_case_insensitively()
    {
        var settings = new SmartRoutingSettings()
            .WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"))
            .WithUserRule(new UserRule(UserRuleType.Website, "DropBox.com", Enabled: false));

        var only = Assert.Single(settings.UserRules);
        Assert.False(only.Enabled);
        Assert.Empty(settings.WithoutUserRule(new UserRule(UserRuleType.Website, "dropbox.com")).UserRules);
    }

    [Fact]
    public void Settings_with_smart_routing_round_trip_with_value_equality()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings
        {
            SmartRouting = new SmartRoutingSettings { Enabled = true }
                .WithItem("facebook", false)
                .WithUserRule(new UserRule(UserRuleType.App, "qbittorrent.exe")),
        };

        store.Save(settings);

        Assert.Equal(settings, store.Load().Settings);
        Assert.Contains("\"App\"", File.ReadAllText(Path.Combine(_dir, "settings.json"))); // enum written as text
    }

    [Theory]
    [InlineData("""{"Mode":"Phone","SmartRouting":{"Items":null}}""")]
    [InlineData("""{"Mode":"Phone","SmartRouting":{"UserRules":null}}""")]
    [InlineData("""{"Mode":"Phone","SmartRouting":{"UserRules":[null]}}""")]
    [InlineData("""{"Mode":"Phone","SmartRouting":{"UserRules":[{"Type":"Website"}]}}""")]
    [InlineData("""{"Mode":"Phone","SmartRouting":{"UserRules":[{"Type":7,"Value":"a.com"}]}}""")]
    public void Invalid_smart_routing_is_recovered_to_defaults(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);

        var loaded = new SettingsStore(path).Load();

        Assert.True(loaded.Recovered);
        Assert.Equal(new SmartRoutingSettings(), loaded.Settings.SmartRouting);
    }

    [Theory]
    [InlineData("""{ "Mode": "Phone" }""")]
    [InlineData("""{ "Mode": "Phone", "SmartRouting": null }""")]
    public void Missing_or_null_smart_routing_loads_as_disabled_defaults(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);

        var loaded = new SettingsStore(path).Load();

        Assert.False(loaded.Recovered);
        Assert.Equal(new SmartRoutingSettings(), loaded.Settings.SmartRouting);
        Assert.False(loaded.Settings.SmartRouting.Enabled);
    }

    [Fact]
    public void Last_lan_interface_round_trips_and_is_part_of_equality()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = new AppSettings { SmartRouting = new SmartRoutingSettings { Enabled = true, LastLanInterface = "Ethernet 2" } };

        store.Save(settings);

        var loaded = store.Load().Settings;
        Assert.Equal("Ethernet 2", loaded.SmartRouting.LastLanInterface);
        Assert.Equal(settings, loaded);
        Assert.NotEqual(settings.SmartRouting, settings.SmartRouting with { LastLanInterface = "Ethernet" });
        Assert.NotEqual(settings.SmartRouting, settings.SmartRouting with { LastLanInterface = null });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ethernet 2")]
    public void Any_last_lan_interface_is_valid(string? name) =>
        Assert.True(new SmartRoutingSettings { LastLanInterface = name }.IsValid());

    [Fact]
    public void Remembering_the_lan_name_only_reports_a_change()
    {
        var settings = new SmartRoutingSettings();

        Assert.True(settings.TryRememberLan("Ethernet", out var first));
        Assert.Equal("Ethernet", first.LastLanInterface);
        Assert.False(first.TryRememberLan("Ethernet", out _));   // same name: no write
        Assert.False(first.TryRememberLan(null, out _));          // LAN absent: keep the last known
        Assert.False(first.TryRememberLan("  ", out _));
        Assert.True(first.TryRememberLan("Ethernet 2", out var renamed));
        Assert.Equal("Ethernet 2", renamed.LastLanInterface);
    }

    [Fact]
    public void Usage_range_defaults_to_seven_days_and_takes_part_in_equality()
    {
        Assert.Equal(7, new SmartRoutingSettings().UsageRangeDays);
        Assert.NotEqual(new SmartRoutingSettings(), new SmartRoutingSettings { UsageRangeDays = 30 });
    }

    [Fact]
    public void A_settings_file_without_the_usage_range_loads_with_the_default()
    {
        var loaded = System.Text.Json.JsonSerializer.Deserialize<SmartRoutingSettings>("""{"Enabled":true}""");

        Assert.Equal(7, loaded!.UsageRangeDays);
    }

    [Fact]
    public void A_settings_file_without_phone_rules_loads_with_none()
    {
        var loaded = System.Text.Json.JsonSerializer.Deserialize<SmartRoutingSettings>("""{"Enabled":true,"UserRules":[{"Type":0,"Value":"a.exe","Enabled":true}]}""");

        Assert.Empty(loaded!.PhoneUserRules);
        Assert.Single(loaded.UserRules);
        Assert.True(loaded.IsValid());
    }

    [Fact]
    public void Phone_rules_round_trip_replace_and_remove_independently_of_the_lan_rules()
    {
        var rule = new UserRule(UserRuleType.App, "claude.exe");
        var s = new SmartRoutingSettings().WithUserRule(rule).WithPhoneUserRule(rule);

        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<SmartRoutingSettings>(json)!;

        Assert.Equal(s, back);
        Assert.Single(back.PhoneUserRules);

        var toggled = back.WithPhoneUserRule(rule with { Enabled = false });
        Assert.False(Assert.Single(toggled.PhoneUserRules).Enabled);
        Assert.True(Assert.Single(toggled.UserRules).Enabled);

        var removed = toggled.WithoutPhoneUserRule(rule);
        Assert.Empty(removed.PhoneUserRules);
        Assert.Single(removed.UserRules);
    }

    [Fact]
    public void Phone_rules_take_part_in_equality_and_validity()
    {
        Assert.NotEqual(new SmartRoutingSettings(), new SmartRoutingSettings().WithPhoneUserRule(new UserRule(UserRuleType.App, "a.exe")));
        Assert.False((new SmartRoutingSettings { PhoneUserRules = null! }).IsValid());
        Assert.False(new SmartRoutingSettings { PhoneUserRules = [new UserRule(UserRuleType.App, " ")] }.IsValid());
    }

    [Fact]
    public void A_phone_rule_has_its_own_id()
    {
        var rule = new UserRule(UserRuleType.Website, "Example.com");

        Assert.Equal("user:website:example.com", UserRule.IdOf(rule));
        Assert.Equal("phone-user:website:example.com", UserRule.PhoneIdOf(rule));
    }
}
