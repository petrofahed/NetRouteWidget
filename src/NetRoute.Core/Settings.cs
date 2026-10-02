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

    [JsonIgnore]
    public AdapterOverrides Overrides => new(PhoneOverride, LanOverrideMac);
}

public sealed record SettingsLoadResult(AppSettings Settings, bool Recovered);

public sealed class SettingsStore(string path)
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// Missing file: defaults (written). Corrupt file: defaults (rewritten), Recovered = true.
    /// Unreadable file (locked, access denied): defaults, Recovered = true, file left as it is.
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
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                ?? throw new JsonException("Settings file is null");
            if (!Enum.IsDefined(settings.Mode)) throw new JsonException($"Unknown mode {(int)settings.Mode}");
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
            return new(new AppSettings(), Recovered: true);
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
