using System.Globalization;

namespace NetRoute.Core.Tests;

public sealed class DataSavedCounterTests : IDisposable
{
    static readonly DateOnly Day1 = new(2026, 10, 2);
    static readonly RuleSet Rules = new(
    [
        new RuleEntry("youtube", "YouTube", [], ["youtube.com", "googlevideo.com"]),
        new RuleEntry("onedrive", "OneDrive", ["OneDrive.exe"], ["onedrive.live.com"]),
    ]);
    readonly string _dir = Directory.CreateTempSubdirectory("netroute-stats-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static SingBoxConnection Lan(string id, string? host, string? process, long up, long down) =>
        new(id, host, process, ["lan", "lan-only"], up, down);

    [Fact]
    public void Counts_lan_only_byte_deltas_while_phone_is_default()
    {
        var counter = new DataSavedCounter(null, Day1);

        counter.Update([Lan("1", "rr1.googlevideo.com", "chrome.exe", 100, 900)], Rules, phoneIsDefault: true, Day1);
        counter.Update([Lan("1", "rr1.googlevideo.com", "chrome.exe", 150, 1850)], Rules, phoneIsDefault: true, Day1);

        Assert.Equal(2000, counter.Snapshot.BytesByEntry["youtube"]);
        Assert.Equal(2000, counter.Snapshot.Total);
    }

    [Fact]
    public void Process_match_wins_and_counts_once()
    {
        var counter = new DataSavedCounter(null, Day1);

        counter.Update([Lan("1", "youtube.com", "OneDrive.exe", 0, 500)], Rules, true, Day1);

        Assert.Equal(new Dictionary<string, long> { ["onedrive"] = 500 }, counter.Snapshot.BytesByEntry);
    }

    [Fact]
    public void Ignores_traffic_when_lan_is_default_or_not_lan_only_or_unknown()
    {
        var counter = new DataSavedCounter(null, Day1);

        counter.Update([Lan("1", "youtube.com", null, 0, 500)], Rules, phoneIsDefault: false, Day1);
        counter.Update([new SingBoxConnection("2", "youtube.com", null, ["phone", "default"], 0, 700)], Rules, true, Day1);
        counter.Update([Lan("3", "example.org", "chrome.exe", 0, 900)], Rules, true, Day1);

        Assert.Equal(0, counter.Snapshot.Total);
    }

    [Fact]
    public void A_new_day_starts_from_zero_and_old_restored_stats_are_ignored()
    {
        var restoredYesterday = new DailyStats(Day1.AddDays(-1), new Dictionary<string, long> { ["youtube"] = 9 });
        var counter = new DataSavedCounter(restoredYesterday, Day1);
        Assert.Equal(0, counter.Snapshot.Total);

        counter.Update([Lan("1", "youtube.com", null, 0, 100)], Rules, true, Day1);
        counter.Update([Lan("1", "youtube.com", null, 0, 300)], Rules, true, Day1.AddDays(1));

        Assert.Equal(Day1.AddDays(1), counter.Snapshot.Day);
        Assert.Equal(200, counter.Snapshot.Total);
    }

    [Fact]
    public void Stats_file_round_trips_and_garbage_loads_as_null()
    {
        var path = Path.Combine(_dir, "stats.json");
        var stats = new DailyStats(Day1, new Dictionary<string, long> { ["youtube"] = 42 });

        DataSavedCounter.SaveFile(path, stats);
        var loaded = DataSavedCounter.LoadFile(path);

        Assert.Equal(Day1, loaded!.Day);
        Assert.Equal(42, loaded.BytesByEntry["youtube"]);
        File.WriteAllText(path, "{ nope");
        Assert.Null(DataSavedCounter.LoadFile(path));
        Assert.Null(DataSavedCounter.LoadFile(Path.Combine(_dir, "missing.json")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"BytesByEntry\":{}}")]
    [InlineData("{\"Day\":\"not a date\"}")]
    public void Valid_but_incomplete_json_loads_as_null(string json)
    {
        var path = Path.Combine(_dir, "stats.json");
        File.WriteAllText(path, json);

        Assert.Null(DataSavedCounter.LoadFile(path));
    }

    [Fact]
    public void Stats_file_day_does_not_depend_on_the_current_culture()
    {
        var path = Path.Combine(_dir, "stats.json");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            DataSavedCounter.SaveFile(path, new DailyStats(Day1, new Dictionary<string, long> { ["youtube"] = 7 }));

            Assert.Contains("\"2026-10-02\"", File.ReadAllText(path));
            Assert.Equal(Day1, DataSavedCounter.LoadFile(path)!.Day);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
