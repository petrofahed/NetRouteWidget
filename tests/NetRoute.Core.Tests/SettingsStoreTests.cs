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
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.False(store.Load().Recovered);
    }

    [Fact]
    public void Unreadable_file_loads_defaults_and_is_left_alone()
    {
        const string content = "{ \"Mode\": \"Lan\" }";
        File.WriteAllText(FilePath, content);

        SettingsLoadResult result;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            result = new SettingsStore(FilePath).Load();

        Assert.True(result.Recovered);
        Assert.Equal(new AppSettings(), result.Settings);
        Assert.Equal(content, File.ReadAllText(FilePath));
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
}
