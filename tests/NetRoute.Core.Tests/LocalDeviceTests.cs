using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public sealed class LocalDeviceTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-localdevices-").FullName;
    string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static readonly IReadOnlyList<LocalDevice> None = [];

    [Fact]
    public void An_ipv4_address_with_a_name_is_accepted_and_trimmed()
    {
        Assert.True(LocalDeviceValidator.TryCreate("  NAS ", " 192.168.86.44 ", None, out var device, out var error));

        Assert.Equal(new LocalDevice("NAS", "192.168.86.44"), device);
        Assert.Equal("", error);
    }

    [Fact]
    public void Without_a_name_the_address_is_the_name()
    {
        Assert.True(LocalDeviceValidator.TryCreate("", "192.168.86.44", None, out var device, out _));

        Assert.Equal(new LocalDevice("192.168.86.44", "192.168.86.44"), device);
    }

    [Theory]
    [InlineData("nas")]
    [InlineData("my-nas.local")]
    [InlineData("MyBookLive")]
    public void A_hostname_is_accepted(string host)
    {
        Assert.True(LocalDeviceValidator.TryCreate(null, host, None, out var device, out _));

        Assert.Equal(host, device.Address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("1.2.3")]            // numbers and dots only must be a full IPv4 address
    [InlineData("1.2.3.4.5")]
    [InlineData("999.1.1.1")]
    [InlineData("fe80::1")]          // IPv6
    [InlineData("bad host")]
    [InlineData("-x.local")]
    [InlineData("a..b")]
    [InlineData("nas_01")]
    public void A_bad_address_is_refused_with_a_reason(string? address)
    {
        Assert.False(LocalDeviceValidator.TryCreate("x", address, None, out _, out var error));

        Assert.NotEqual("", error);
    }

    [Fact]
    public void A_name_longer_than_the_limit_is_refused()
    {
        Assert.False(LocalDeviceValidator.TryCreate(new string('a', LocalDeviceValidator.MaxNameLength + 1), "192.168.86.44", None, out _, out _));
        Assert.True(LocalDeviceValidator.TryCreate(new string('a', LocalDeviceValidator.MaxNameLength), "192.168.86.44", None, out _, out _));
    }

    [Fact]
    public void An_address_longer_than_the_limit_is_refused()
    {
        var host = string.Join(".", Enumerable.Repeat(new string('a', 50), 6)); // 305 characters
        Assert.False(LocalDeviceValidator.TryCreate(null, host, None, out _, out _));
    }

    [Fact]
    public void A_duplicate_address_is_refused_whatever_its_case()
    {
        var existing = new[] { new LocalDevice("NAS", "MyNas") };

        Assert.False(LocalDeviceValidator.TryCreate("again", "mynas", existing, out _, out var error));
        Assert.Contains("already", error);
    }

    [Fact]
    public void The_twenty_first_device_is_refused()
    {
        var existing = Enumerable.Range(1, LocalDeviceValidator.MaxDevices).Select(i => new LocalDevice($"d{i}", $"10.0.0.{i}")).ToList();

        Assert.False(LocalDeviceValidator.TryCreate("one more", "10.0.0.99", existing, out _, out var error));
        Assert.Contains("20", error);
    }

    [Fact]
    public void Sanitize_keeps_the_good_entries_in_order_and_drops_nulls_duplicates_and_garbage()
    {
        var saved = new List<LocalDevice>
        {
            new("NAS", "192.168.86.44"), null!, new("dup", "192.168.86.44"), new("", "not an address!"), new("TV", "tv.local"),
        };

        var clean = LocalDeviceValidator.Sanitize(saved);

        Assert.Equal([new LocalDevice("NAS", "192.168.86.44"), new LocalDevice("TV", "tv.local")], clean);
    }

    [Fact]
    public void Sanitize_caps_the_list_and_accepts_null()
    {
        var many = Enumerable.Range(1, 100).Select(i => new LocalDevice($"d{i}", $"10.0.1.{i}")).ToList();

        Assert.Equal(LocalDeviceValidator.MaxDevices, LocalDeviceValidator.Sanitize(many).Count);
        Assert.Empty(LocalDeviceValidator.Sanitize(null));
    }

    [Fact]
    public void Settings_default_to_no_devices_and_round_trip_the_list()
    {
        Assert.Empty(new AppSettings().LocalDevices);
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings { LocalDevices = [new LocalDevice("NAS", "192.168.86.44"), new LocalDevice("TV", "tv.local")] };

        store.Save(settings);

        Assert.Equal(settings.LocalDevices, store.Load().Settings.LocalDevices);
    }

    [Fact]
    public void A_settings_file_from_before_this_version_loads_with_no_devices()
    {
        File.WriteAllText(FilePath, "{ \"Mode\": \"Lan\" }");

        var result = new SettingsStore(FilePath).Load();

        Assert.False(result.Recovered);
        Assert.Empty(result.Settings.LocalDevices);
    }

    [Fact]
    public void Garbage_in_the_saved_device_list_is_dropped_without_losing_the_other_settings()
    {
        File.WriteAllText(FilePath, """
            { "Mode": "Lan", "LocalDevices": [ { "Name": "NAS", "Address": "192.168.86.44" }, { "Name": "x", "Address": "??" }, null,
              { "Name": "NAS again", "Address": "192.168.86.44" } ] }
            """);

        var result = new SettingsStore(FilePath).Load();

        Assert.False(result.Recovered);
        Assert.Equal(RoutingMode.Lan, result.Settings.Mode);
        Assert.Equal([new LocalDevice("NAS", "192.168.86.44")], result.Settings.LocalDevices);
    }

    [Fact]
    public void A_null_device_list_in_the_file_loads_as_empty()
    {
        File.WriteAllText(FilePath, "{ \"LocalDevices\": null }");

        Assert.Empty(new SettingsStore(FilePath).Load().Settings.LocalDevices);
    }
}
