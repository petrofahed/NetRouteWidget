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
            var adapter = adapters.FirstOrDefault(a => string.Equals(a.Mac, mac, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) return settings; // not present right now; retry next start

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
