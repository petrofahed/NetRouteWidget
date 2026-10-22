namespace NetRoute.Core.Tests;

public class SettingsMigrationTests
{
    [Fact]
    public void Wireless_lan_choice_moves_to_wifi_with_mode_and_rules()
    {
        var rule = new UserRule(UserRuleType.App, "Teams.exe", true);
        var v5 = new AppSettings { Mode = RoutingMode.Lan, LanOverrideMac = "AA-BB-CC-00-00-18",
            SmartRouting = new SmartRoutingSettings { PhoneUserRules = [rule], LastLanInterface = "Wi-Fi" } };

        var v6 = SettingsMigration.ToV6(v5, [TestAdapters.Lan(), TestAdapters.Wifi()]);

        Assert.Equal("AA-BB-CC-00-00-18", v6.WifiOverrideMac);
        Assert.Null(v6.LanOverrideMac);
        Assert.Equal(RoutingMode.Wifi, v6.Mode);
        Assert.Equal([rule], v6.SmartRouting.WifiUserRules);
        Assert.Equal([rule], v6.SmartRouting.PhoneUserRules);
        Assert.Null(v6.SmartRouting.LastLanInterface);
        Assert.Equal(6, v6.SettingsVersion);
    }

    [Fact]
    public void Wired_lan_choice_is_kept()
    {
        var v5 = new AppSettings { Mode = RoutingMode.Lan, LanOverrideMac = "AA-BB-CC-00-00-10",
            SmartRouting = new SmartRoutingSettings { PhoneUserRules = [new UserRule(UserRuleType.App, "a.exe")], LastLanInterface = "Ethernet" } };

        var v6 = SettingsMigration.ToV6(v5, [TestAdapters.Lan(), TestAdapters.Wifi()]);

        Assert.Equal(v5 with { SettingsVersion = AppSettings.CurrentVersion }, v6);
    }

    [Fact]
    public void Migration_waits_when_the_lan_override_adapter_is_absent()
    {
        var v5 = new AppSettings { Mode = RoutingMode.Lan, LanOverrideMac = "AA-BB-CC-00-00-18" };
        Assert.Equal(v5, SettingsMigration.ToV6(v5, [TestAdapters.Lan()]));
    }

    [Fact]
    public void Already_v6_is_untouched()
    {
        var v6 = new AppSettings { Mode = RoutingMode.Lan, LanOverrideMac = "AA-BB-CC-00-00-18", SettingsVersion = AppSettings.CurrentVersion };

        var result = SettingsMigration.ToV6(v6, [TestAdapters.Wifi()]);

        Assert.Equal(v6, result);
    }

    [Fact]
    public void A_wireless_match_wins_over_a_non_wireless_duplicate_mac()
    {
        // A Hyper-V vEthernet switch gets the same MAC as the physical NIC it binds to. Put the non-wireless
        // duplicate first in the list to prove the wireless one isn't just picked by accident of ordering.
        const string sharedMac = "AA-BB-CC-00-00-18";
        var virtualDuplicate = TestAdapters.HyperV() with { Mac = sharedMac };
        var wifi = TestAdapters.Wifi(mac: sharedMac);
        var rule = new UserRule(UserRuleType.App, "Teams.exe");
        var v5 = new AppSettings { Mode = RoutingMode.Lan, LanOverrideMac = sharedMac,
            SmartRouting = new SmartRoutingSettings { PhoneUserRules = [rule] } };

        var v6 = SettingsMigration.ToV6(v5, [virtualDuplicate, wifi]);

        Assert.Equal(sharedMac, v6.WifiOverrideMac);
        Assert.Null(v6.LanOverrideMac);
        Assert.Equal(RoutingMode.Wifi, v6.Mode);
        Assert.Equal([rule], v6.SmartRouting.WifiUserRules);
        Assert.Equal(6, v6.SettingsVersion);
    }

    [Fact]
    public void No_lan_override_just_bumps_the_version()
    {
        var v5 = new AppSettings { Mode = RoutingMode.Phone };

        var v6 = SettingsMigration.ToV6(v5, [TestAdapters.Lan(), TestAdapters.Wifi()]);

        Assert.Equal(RoutingMode.Phone, v6.Mode);
        Assert.Null(v6.WifiOverrideMac);
        Assert.Null(v6.LanOverrideMac);
        Assert.Equal(6, v6.SettingsVersion);
    }
}
