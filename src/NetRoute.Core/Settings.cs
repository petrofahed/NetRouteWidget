using System.Text.Json;
using System.Text.Json.Serialization;
using NetRoute.Core.PathMap;

namespace NetRoute.Core;

public sealed record AppSettings
{
    /// Schema version of this settings file. 0 (the default) means an old file written before this field existed
    /// (v5.3 and earlier) — System.Text.Json leaves missing properties at their default, which is exactly 0 here.
    public const int CurrentVersion = 6;

    public int SettingsVersion { get; init; }
    public RoutingMode Mode { get; init; } = RoutingMode.Phone;
    public string? PhoneOverride { get; init; }
    public string? LanOverrideMac { get; init; }
    public string? WifiOverrideMac { get; init; }
    public double? CardLeft { get; init; }
    public double? CardTop { get; init; }
    public bool CardVisible { get; init; } = true;
    public bool StartWithWindows { get; init; }

    /// Moves the internet to the other connection while the preferred one has no internet. Off: the user's choice
    /// always stands, so a flaky LAN can never push traffic onto the metered phone connection by itself.
    public bool AutoHeal { get; init; } = true;
    public SmartRoutingSettings SmartRouting { get; init; } = new();

    /// Devices on the local network the user tests in the path window's "Local network" tab.
    public IReadOnlyList<LocalDevice> LocalDevices { get; init; } = Array.Empty<LocalDevice>();

    [JsonIgnore]
    public AdapterOverrides Overrides => new(PhoneOverride, LanOverrideMac, WifiOverrideMac);
}

/// Unreadable: the file exists but could not be read (locked, access denied). It was left untouched, so the caller
/// must not save over it this session.
public sealed record SettingsLoadResult(AppSettings Settings, bool Recovered, bool Unreadable = false);

public sealed class SettingsStore(string path, int readAttempts = 3, TimeSpan? retryDelay = null)
{
    readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(200);

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// A one-time, untouched copy of the pre-v6 file, written the first time an old file is loaded. Sits next to the
    /// live settings file so a bad migration can be undone by hand.
    public string BackupPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "settings.v5-backup.json");

    /// Missing file: defaults (written). Corrupt file: defaults (rewritten), Recovered = true.
    /// Unreadable file (locked, access denied): defaults, Recovered = true, Unreadable = true, file left as it is.
    /// A locked file (IOException) is retried a few times first; access denied is not.
    public SettingsLoadResult Load()
    {
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            Save(defaults);
            return new(defaults, false);
        }

        try
        {
            var raw = ReadWithRetry();
            var settings = JsonSerializer.Deserialize<AppSettings>(raw, Options)
                ?? throw new JsonException("Settings file is null");
            if (!Enum.IsDefined(settings.Mode)) throw new JsonException($"Unknown mode {(int)settings.Mode}");
            if (settings.SmartRouting is null) settings = settings with { SmartRouting = new SmartRoutingSettings() };
            settings = settings with { LocalDevices = LocalDeviceValidator.Sanitize(settings.LocalDevices) }; // bad entries are dropped, never a failed load
            if (!settings.SmartRouting.IsValid()) throw new JsonException("Invalid Smart routing settings");
            if (settings.SettingsVersion < AppSettings.CurrentVersion) BackupOnce();
            return new(settings, false);
        }
        catch (JsonException)
        {
            var defaults = new AppSettings();
            try { Save(defaults); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return new(defaults, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(new AppSettings(), Recovered: true, Unreadable: true);
        }
    }

    /// Copies the pre-migration file to BackupPath byte-for-byte (so a BOM, if any, survives), once.
    /// A failed backup must never block loading.
    void BackupOnce()
    {
        if (File.Exists(BackupPath)) return;
        try
        {
            var temp = BackupPath + ".tmp";
            File.Copy(path, temp, overwrite: true);
            File.Move(temp, BackupPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    string ReadWithRetry()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < readAttempts) { Thread.Sleep(_retryDelay); }
        }
    }

    /// Writes to a temp file first so a crash never leaves a half-written settings file.
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
    }
}
