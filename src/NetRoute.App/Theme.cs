using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetRoute.App;

/// Colours following the Windows app theme (light/dark), shared by the card and the management/dialog windows.
static class Theme
{
    static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["CardBackground"] = ("#F2FAFAFA", "#F21F1F1F"),
        ["WindowBackground"] = ("#FFF3F3F3", "#FF202020"), // opaque: the card's is translucent
        ["CardBorder"] = ("#22000000", "#33FFFFFF"),
        ["TextPrimary"] = ("#FF1B1B1B", "#FFF3F3F3"),
        ["TextSecondary"] = ("#FF5F5F5F", "#FFB0B0B0"),
        ["ButtonBackground"] = ("#FFE6E6E6", "#FF2D2D2D"),
        ["Accent"] = ("#FF2563EB", "#FF3B82F6"),
        ["Warning"] = ("#FFB45309", "#FFF5A623"),
        ["DotGreen"] = ("#FF2EA043", "#FF3FB950"),
        ["DotAmber"] = ("#FFD97706", "#FFF5A623"),
        ["DotGray"] = ("#FF8B949E", "#FF6E7681"),
    };

    public static void Apply(ResourceDictionary resources)
    {
        var light = IsLight();
        foreach (var (key, (lightHex, darkHex)) in Palette)
            resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(light ? lightHex : darkHex));
    }

    /// For a normal window (not the card): applies the palette now and re-applies it whenever the Windows theme changes,
    /// until the window closes. Pair it with ThemeMode="System", which restyles the controls themselves.
    public static void Track(Window window)
    {
        Apply(window.Resources);
        UserPreferenceChangedEventHandler handler = (_, e) =>
        {
            // SystemEvents raises this on its own thread.
            if (e.Category == UserPreferenceCategory.General) window.Dispatcher.BeginInvoke(new Action(() => Apply(window.Resources)));
        };
        SystemEvents.UserPreferenceChanged += handler;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= handler;
    }

    static bool IsLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
