using System.Text.Json;

namespace NetRoute.Core;

public sealed record DailyStats(DateOnly Day, IReadOnlyDictionary<string, long> BytesByEntry)
{
    public long Total => BytesByEntry.Values.Sum();
}

/// Bytes that LAN-only rules kept off 4G today, per entry. Counts connection byte deltas between polls,
/// only while the phone is the default exit (otherwise that traffic would not have used 4G anyway).
public sealed class DataSavedCounter
{
    readonly Dictionary<string, long> _lastSeen = new();
    Dictionary<string, long> _today;
    DateOnly _day;

    public DataSavedCounter(DailyStats? restored, DateOnly today)
    {
        _day = today;
        _today = restored is not null && restored.Day == today ? new(restored.BytesByEntry) : new();
    }

    public DailyStats Snapshot => new(_day, new Dictionary<string, long>(_today));

    public void Update(IEnumerable<SingBoxConnection> connections, RuleSet rules, bool phoneIsDefault, DateOnly today)
    {
        if (today != _day)
        {
            _day = today;
            _today = new();
        }

        var alive = new HashSet<string>();
        foreach (var c in connections)
        {
            alive.Add(c.Id);
            var total = c.Upload + c.Download;
            var delta = total - _lastSeen.GetValueOrDefault(c.Id);
            _lastSeen[c.Id] = total;
            if (!phoneIsDefault || !c.IsLanOnly || delta <= 0) continue;

            var entry = rules.FindByProcess(c.ProcessName) ?? rules.FindByHost(c.Host); // process match wins
            if (entry is not null) _today[entry.Id] = _today.GetValueOrDefault(entry.Id) + delta;
        }
        foreach (var gone in _lastSeen.Keys.Where(id => !alive.Contains(id)).ToList()) _lastSeen.Remove(gone);
    }

    public static DailyStats? LoadFile(string path)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredStats>(File.ReadAllText(path));
            return stored is null ? null : new DailyStats(DateOnly.Parse(stored.Day), stored.BytesByEntry ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    public static void SaveFile(string path, DailyStats stats)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new StoredStats(stats.Day.ToString("yyyy-MM-dd"), new Dictionary<string, long>(stats.BytesByEntry))));
        File.Move(temp, path, overwrite: true);
    }

    sealed record StoredStats(string Day, Dictionary<string, long>? BytesByEntry);
}
