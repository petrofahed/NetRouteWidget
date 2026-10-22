namespace NetRoute.Core;

/// One-shot upgrades applied to settings loaded from disk, run once at startup after `SettingsStore.Load`.
public static class SettingsMigration
{
    /// Brings a settings file up to v6 (the Wi-Fi connection). A LAN override that turns out to name a Wi-Fi adapter
    /// moves to the new Wi-Fi override instead, carrying the mode and the "LAN + exceptions" rules along with it.
    /// If the named adapter is simply not present right now (unplugged, not necessarily gone), migration waits:
    /// settings come back completely unchanged, version included, so the next start tries again.
    public static AppSettings ToV6(AppSettings settings, IReadOnlyList<AdapterInfo> adapters)
    {
        if (settings.SettingsVersion >= AppSettings.CurrentVersion) return settings;

        if (settings.LanOverrideMac is { } mac)
        {
            // Several present adapters can share a MAC (a Hyper-V vEthernet switch gets the same MAC as the physical
            // NIC it binds to), so match on all of them first - collapsing straight to "the" adapter would wrongly
            // fold "present but not wireless" into "absent" whenever a non-wireless duplicate happens to come first.
            var matches = adapters.Where(a => string.Equals(a.Mac, mac, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return settings; // not present right now; retry next start

            var adapter = matches.FirstOrDefault(a => a.Kind == AdapterKind.Wireless) ?? matches[0];
            if (adapter.Kind != AdapterKind.Wireless) return settings with { SettingsVersion = AppSettings.CurrentVersion };

            var smartRouting = settings.SmartRouting with { WifiUserRules = [.. settings.SmartRouting.PhoneUserRules] };
            if (smartRouting.LastLanInterface == adapter.Name) smartRouting = smartRouting with { LastLanInterface = null };

            return settings with
            {
                WifiOverrideMac = mac,
                LanOverrideMac = null,
                Mode = settings.Mode == RoutingMode.Lan ? RoutingMode.Wifi : settings.Mode,
                SmartRouting = smartRouting,
                SettingsVersion = AppSettings.CurrentVersion,
            };
        }

        return settings with { SettingsVersion = AppSettings.CurrentVersion };
    }
}
