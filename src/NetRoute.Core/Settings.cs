using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRoute.Core;

public sealed record AppSettings
{
    public RoutingMode Mode { get; init; } = RoutingMode.Phone;
    public string? PhoneOverride { get; init; }
    public string? LanOverrideMac { get; init; }
    public double? CardLeft { get; init; }
    public double? CardTop { get; init; }
    public bool CardVisible { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public SmartRoutingSettings SmartRouting { get; init; } = new();

    [JsonIgnore]
    public AdapterOverrides Overrides => new(PhoneOverride, LanOverrideMac);
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
            var settings = JsonSerializer.Deserialize<AppSettings>(ReadWithRetry(), Options)
                ?? throw new JsonException("Settings file is null");
            if (!Enum.IsDefined(settings.Mode)) throw new JsonException($"Unknown mode {(int)settings.Mode}");
            if (settings.SmartRouting is null) settings = settings with { SmartRouting = new SmartRoutingSettings() };
            if (!settings.SmartRouting.IsValid()) throw new JsonException("Invalid Smart routing settings");
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
