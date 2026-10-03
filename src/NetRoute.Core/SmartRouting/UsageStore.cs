using System.Globalization;
using System.Text.Json;

namespace NetRoute.Core;

/// Unreadable: the file exists but could not be read (locked, access denied). It was left untouched, so the caller must
/// not save over it this session (the same rule as settings.json).
public sealed record UsageLoadResult(UsageSnapshot Usage, bool Unreadable);

/// Reads and writes %AppData%\NetRouteWidget\usage.json. Only row keys, day keys and byte totals are stored.
public static class UsageStore
{
    const int Version = 1;
    const string DayFormat = "yyyy-MM-dd";

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal sealed record FileDto(int Version, Dictionary<string, DayDto>? Days);
    internal sealed record DayDto(Dictionary<string, RowDto>? Rows, long PhoneAdapter, int Gaps);
    internal sealed record RowDto(TrafficDto? Phone, TrafficDto? Lan, long Kept);
    internal sealed record TrafficDto(long Up, long Down);

    public static UsageLoadResult Load(string path, DateOnly today)
    {
        if (!File.Exists(path)) return new(UsageSnapshot.Empty, false);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(UsageSnapshot.Empty, true);
        }

        try
        {
            var dto = JsonSerializer.Deserialize<FileDto>(text, Options);
            if (dto is not { Version: Version, Days: { } days }) throw new JsonException("Unsupported usage file");
            return new(Prune(Read(days), today), false);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            SetAside(path);
            return new(UsageSnapshot.Empty, false);
        }
    }

    /// Writes to a temp file first so a crash never leaves a half-written file.
    public static void Save(string path, UsageSnapshot usage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var days = new Dictionary<string, DayDto>();
        foreach (var (day, data) in usage.Days)
        {
            if (data.Rows.Count == 0 && data.PhoneAdapterBytes == 0) continue;
            days[day.ToString(DayFormat, CultureInfo.InvariantCulture)] = new DayDto(
                data.Rows.ToDictionary(
                    r => r.Key,
                    r => new RowDto(
                        new TrafficDto(r.Value.Phone.Up, r.Value.Phone.Down),
                        new TrafficDto(r.Value.Lan.Up, r.Value.Lan.Down),
                        r.Value.Kept)),
                data.PhoneAdapterBytes, data.Gaps);
        }
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new FileDto(Version, days), Options));
        File.Move(temp, path, overwrite: true);
    }

    static UsageSnapshot Read(Dictionary<string, DayDto> days)
    {
        var result = new Dictionary<DateOnly, UsageDay>();
        foreach (var (dayText, dto) in days)
        {
            if (!DateOnly.TryParseExact(dayText, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new FormatException($"Bad day key '{dayText}'");
            var rows = new Dictionary<string, UsageRow>();
            foreach (var (key, row) in dto?.Rows ?? new())
            {
                if (row is null) continue;
                rows[key] = new UsageRow(Clamp(row.Phone), Clamp(row.Lan), Math.Max(0, row.Kept));
            }
            result[day] = new UsageDay(rows, Math.Max(0, dto?.PhoneAdapter ?? 0), Math.Max(0, dto?.Gaps ?? 0));
        }
        return new UsageSnapshot(result, new Dictionary<string, UsageRate>());
    }

    static Traffic Clamp(TrafficDto? t) => t is null ? default : new Traffic(Math.Max(0, t.Up), Math.Max(0, t.Down));

    /// Drops days past the retention and, for a hand-edited or runaway day, folds the smallest rows into "other".
    static UsageSnapshot Prune(UsageSnapshot snapshot, DateOnly today)
    {
        var cutoff = today.AddDays(-(UsageSnapshot.RetentionDays - 1));
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var (day, data) in snapshot.Days)
            if (day >= cutoff) days[day] = CapRows(data);
        return new UsageSnapshot(days, snapshot.Rates);
    }

    static UsageDay CapRows(UsageDay day)
    {
        var normal = day.Rows
            .Where(r => r.Key is not (UsageAttribution.OtherKey or UsageAttribution.UnattributedKey))
            .OrderByDescending(r => r.Value.Total).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        if (normal.Count <= UsageCounter.MaxRowsPerDay) return day;

        var rows = day.Rows
            .Where(r => r.Key is UsageAttribution.OtherKey or UsageAttribution.UnattributedKey)
            .ToDictionary(r => r.Key, r => r.Value);
        foreach (var (key, row) in normal.Take(UsageCounter.MaxRowsPerDay)) rows[key] = row;
        var overflow = normal.Skip(UsageCounter.MaxRowsPerDay).Aggregate(UsageRow.Empty, (sum, r) => sum + r.Value);
        rows[UsageAttribution.OtherKey] = rows.GetValueOrDefault(UsageAttribution.OtherKey, UsageRow.Empty) + overflow;
        return new UsageDay(rows, day.PhoneAdapterBytes, day.Gaps);
    }

    static void SetAside(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it away: the next save overwrites the corrupt file, which is fine.
        }
    }
}
