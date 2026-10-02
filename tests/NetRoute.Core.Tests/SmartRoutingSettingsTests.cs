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
}
