namespace NetRoute.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-settings-").FullName;
    string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_loads_defaults_and_creates_the_file()
    {
        var result = new SettingsStore(FilePath).Load();

        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Equal(RoutingMode.Phone, result.Settings.Mode);
        Assert.True(result.Settings.CardVisible);
        Assert.False(result.Recovered);
        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void Saved_settings_round_trip_with_mode_as_text()
    {
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings
        {
            Mode = RoutingMode.Lan, PhoneOverride = "Phone NDIS", LanOverrideMac = "AA-BB-CC-00-00-10",
            CardLeft = 100.5, CardTop = 200, CardVisible = false, StartWithWindows = true,
        };

        store.Save(settings);

        Assert.Equal(settings, store.Load().Settings);
        Assert.Contains("\"Lan\"", File.ReadAllText(FilePath));
        Assert.DoesNotContain("Overrides", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{ \"Mode\": \"Bogus\" }")]
    public void Corrupt_file_loads_defaults_and_is_repaired(string content)
    {
        File.WriteAllText(FilePath, content);
        var store = new SettingsStore(FilePath);

        var result = store.Load();

        Assert.True(result.Recovered);
        Assert.False(result.Unreadable);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.False(store.Load().Recovered);
    }

    [Fact]
    public void Unreadable_file_loads_defaults_is_flagged_and_is_left_alone()
    {
        const string content = "{ \"Mode\": \"Lan\" }";
        File.WriteAllText(FilePath, content);

        SettingsLoadResult result;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            result = new SettingsStore(FilePath, retryDelay: TimeSpan.FromMilliseconds(10)).Load();

        Assert.True(result.Unreadable);
        Assert.True(result.Recovered);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task Briefly_locked_file_is_read_after_a_retry()
    {
        File.WriteAllText(FilePath, "{ \"Mode\": \"Lan\" }");
        var lockStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Delay(100).ContinueWith(_ => lockStream.Dispose());

        var result = new SettingsStore(FilePath, readAttempts: 5, retryDelay: TimeSpan.FromMilliseconds(100)).Load();
        await release;

        Assert.False(result.Unreadable);
        Assert.False(result.Recovered);
        Assert.Equal(RoutingMode.Lan, result.Settings.Mode);
    }

    [Fact]
    public void Corrupt_file_that_cannot_be_repaired_still_loads_defaults()
    {
        File.WriteAllText(FilePath, "{ this is not json");
        Directory.CreateDirectory(FilePath + ".tmp"); // the repair's temp file cannot be written

        var result = new SettingsStore(FilePath).Load();

        Assert.True(result.Recovered);
        Assert.Equal(new AppSettings(), result.Settings);
    }

    [Fact]
    public void Unknown_mode_number_is_treated_as_corrupt()
    {
        File.WriteAllText(FilePath, "{ \"Mode\": 7 }");

        var result = new SettingsStore(FilePath).Load();

        Assert.True(result.Recovered);
        Assert.Equal(RoutingMode.Phone, result.Settings.Mode);
    }

    [Fact]
    public void Overrides_are_built_from_settings()
    {
        var settings = new AppSettings { PhoneOverride = "P", LanOverrideMac = "M" };

        Assert.Equal(new AdapterOverrides("P", "M"), settings.Overrides);
    }

    const string V5Json = """{ "Mode": "Lan", "LanOverrideMac": "AA-BB-CC-00-00-18" }""";

    [Fact]
    public void Loading_a_v5_file_writes_the_backup_once()
    {
        // written with a BOM so the backup is checked byte-for-byte, not just text-for-text
        File.WriteAllText(FilePath, V5Json, new System.Text.UTF8Encoding(true));
        var store = new SettingsStore(FilePath);
        var originalBytes = File.ReadAllBytes(FilePath);

        var result = store.Load();

        Assert.True(result.Settings.SettingsVersion < AppSettings.CurrentVersion);
        Assert.True(File.Exists(store.BackupPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(store.BackupPath));

        store.Save(result.Settings with { CardLeft = 42 });
        store.Load();

        Assert.Equal(originalBytes, File.ReadAllBytes(store.BackupPath));
    }

    [Fact]
    public void A_v6_file_writes_no_backup()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { SettingsVersion = AppSettings.CurrentVersion });

        store.Load();

        Assert.False(File.Exists(store.BackupPath));
    }

    [Fact]
    public void A_v5_3_file_without_new_fields_loads()
    {
        File.WriteAllText(FilePath, """{ "Mode": "Lan", "LanOverrideMac": "AA-BB-CC-00-00-10" }""");

        var result = new SettingsStore(FilePath).Load();

        Assert.False(result.Recovered);
        Assert.Null(result.Settings.WifiOverrideMac);
        Assert.Equal(0, result.Settings.SettingsVersion);
        Assert.Empty(result.Settings.SmartRouting.WifiUserRules);
    }

    [Fact]
    public void TurnedOff_round_trips_and_defaults_to_empty()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { TurnedOff = [ConnectionId.Lan, ConnectionId.Wifi] });

        Assert.Equal([ConnectionId.Lan, ConnectionId.Wifi], store.Load().Settings.TurnedOff);
        Assert.Contains("\"Lan\"", File.ReadAllText(FilePath));

        File.WriteAllText(FilePath, "{ \"SettingsVersion\": 6, \"Mode\": \"Lan\" }");
        Assert.Empty(store.Load().Settings.TurnedOff);

        File.WriteAllText(FilePath, "{ \"SettingsVersion\": 6, \"Mode\": \"Lan\", \"TurnedOff\": null }");
        Assert.Empty(store.Load().Settings.TurnedOff);

        File.WriteAllText(FilePath, "{ \"SettingsVersion\": 6, \"Mode\": \"Lan\", \"TurnedOff\": [\"Lan\", \"Lan\"] }");
        Assert.Equal([ConnectionId.Lan], store.Load().Settings.TurnedOff);
    }

    [Fact]
    public void An_unknown_TurnedOff_entry_is_dropped_not_a_failed_load()
    {
        var store = new SettingsStore(FilePath);
        File.WriteAllText(FilePath, "{ \"SettingsVersion\": 6, \"Mode\": \"Lan\", \"PhoneOverride\": \"kept\", \"TurnedOff\": [\"Satellite\", \"wifi\", 7, {}, \"Lan\"] }");

        var result = store.Load();

        Assert.False(result.Recovered);
        Assert.Equal("kept", result.Settings.PhoneOverride);
        Assert.Equal([ConnectionId.Wifi, ConnectionId.Lan], result.Settings.TurnedOff);
    }

    [Theory]
    [InlineData(ConnectionId.Phone, "UserRules")]
    [InlineData(ConnectionId.Lan, "PhoneUserRules")]
    [InlineData(ConnectionId.Wifi, "WifiUserRules")]
    public void Explicit_user_rule_exits_round_trip_by_name_in_each_profile(ConnectionId profile, string listName)
    {
        var smart = new SmartRoutingSettings()
            .WithUserRule(profile, new(UserRuleType.App, "lan.exe", Exit: ConnectionId.Lan))
            .WithUserRule(profile, new(UserRuleType.App, "wifi.exe", Exit: ConnectionId.Wifi))
            .WithUserRule(profile, new(UserRuleType.App, "phone.exe", Exit: ConnectionId.Phone));
        var settings = new AppSettings { SettingsVersion = 6, SmartRouting = smart };
        var store = new SettingsStore(FilePath);
        store.Save(settings);

        var loaded = store.Load();
        Assert.False(loaded.Recovered);
        Assert.Equal(settings, loaded.Settings);
        Assert.Equal(new ConnectionId?[] { ConnectionId.Lan, ConnectionId.Wifi, ConnectionId.Phone },
            loaded.Settings.SmartRouting.RulesFor(profile).Select(rule => rule.Exit));
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(["Lan", "Wifi", "Phone"], json.RootElement.GetProperty("SmartRouting").GetProperty(listName)
            .EnumerateArray().Select(rule => rule.GetProperty("Exit").GetString()));
        Assert.False(File.Exists(store.BackupPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Legacy_user_rules_without_exit_keep_null_and_existing_backup_policy(int version)
    {
        var original = $$$"""{"SettingsVersion":{{{version}}},"Mode":"Lan","SmartRouting":{"UserRules":[{"Type":"App","Value":"home.exe"}],"PhoneUserRules":[{"Type":"App","Value":"mobile.exe"}],"WifiUserRules":[{"Type":"Website","Value":"example.com"}]}}""";
        File.WriteAllText(FilePath, original);
        var store = new SettingsStore(FilePath);
        var loaded = store.Load();

        Assert.False(loaded.Recovered);
        Assert.Null(Assert.Single(loaded.Settings.SmartRouting.UserRules).Exit);
        Assert.Null(Assert.Single(loaded.Settings.SmartRouting.PhoneUserRules).Exit);
        Assert.Null(Assert.Single(loaded.Settings.SmartRouting.WifiUserRules).Exit);
        Assert.Equal(version < 6, File.Exists(store.BackupPath));
        if (version < 6) Assert.Equal(original, File.ReadAllText(store.BackupPath));
        store.Save(loaded.Settings);
        var reloaded = store.Load();
        Assert.Equal(loaded.Settings, reloaded.Settings);
        if (version < 6) Assert.Equal(original, File.ReadAllText(store.BackupPath));
    }

    [Theory]
    [InlineData("UserRules")]
    [InlineData("PhoneUserRules")]
    [InlineData("WifiUserRules")]
    public void Unknown_numeric_user_rule_exit_uses_existing_corrupt_settings_recovery(string listName)
    {
        File.WriteAllText(FilePath, $$$"""{"SettingsVersion":6,"Mode":"Lan","SmartRouting":{"{{{listName}}}":[{"Type":"App","Value":"example.exe","Exit":999}]}}""");
        var store = new SettingsStore(FilePath);

        var loaded = store.Load();

        Assert.True(loaded.Recovered);
        Assert.Equal(new AppSettings(), loaded.Settings);
        Assert.False(store.Load().Recovered);
    }
}
