using System.IO;

namespace NetRoute.App;

static class AppPaths
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetRouteWidget");

    public static readonly string SettingsFile = Path.Combine(Root, "settings.json");

    public static readonly string LogDir = Path.Combine(Root, "logs");

    public static readonly string SingBoxExe = Path.Combine(AppContext.BaseDirectory, "sing-box", "sing-box.exe");

    public static readonly string StatsFile = Path.Combine(Root, "stats.json");
}
