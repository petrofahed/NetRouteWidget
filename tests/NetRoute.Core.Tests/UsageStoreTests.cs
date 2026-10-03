using System.Globalization;

namespace NetRoute.Core.Tests;

public sealed class UsageStoreTests : IDisposable
{
    static readonly DateOnly D0 = new(2026, 10, 2);
    readonly string _dir = Directory.CreateTempSubdirectory("usage-store-").FullName;
    string FilePath => Path.Combine(_dir, "usage.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static UsageSnapshot Sample() => new(
        new Dictionary<DateOnly, UsageDay>
        {
            [D0] = new(new Dictionary<string, UsageRow>
            {
                ["youtube"] = new(new Traffic(1, 2), new Traffic(3, 4), 5),
                ["app:chrome.exe"] = new(new Traffic(10, 20), default, 0),
            }, 777, 2),
        },
        new Dictionary<string, UsageRate>());

    [Fact]
    public void A_saved_snapshot_loads_back_the_same()
    {
        UsageStore.Save(FilePath, Sample());

        var result = UsageStore.Load(FilePath, D0);

        Assert.False(result.Unreadable);
        var day = Assert.Single(result.Usage.Days);
        Assert.Equal(D0, day.Key);
        Assert.Equal(777, day.Value.PhoneAdapterBytes);
        Assert.Equal(2, day.Value.Gaps);
        Assert.Equal(new UsageRow(new Traffic(1, 2), new Traffic(3, 4), 5), day.Value.Rows["youtube"]);
        Assert.Equal(new UsageRow(new Traffic(10, 20), default, 0), day.Value.Rows["app:chrome.exe"]);
    }

    [Fact]
    public void The_file_has_the_documented_shape_and_leaves_no_temp_file()
    {
        UsageStore.Save(FilePath, Sample());

        var text = File.ReadAllText(FilePath);

        Assert.Contains("\"version\":1", text);
        Assert.Contains("\"2026-10-02\"", text);
        Assert.Contains("\"phoneAdapter\":777", text);
        Assert.Contains("\"gaps\":2", text);
        Assert.Contains("\"kept\":5", text);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Day_keys_and_numbers_are_culture_invariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA"); // Hijri calendar, Arabic digits
            UsageStore.Save(FilePath, Sample());
            var text = File.ReadAllText(FilePath);
            var loaded = UsageStore.Load(FilePath, D0);

            Assert.Contains("\"2026-10-02\"", text);
            Assert.Equal(D0, Assert.Single(loaded.Usage.Days).Key);
            Assert.Equal(777, loaded.Usage.Days[D0].PhoneAdapterBytes);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void A_missing_file_is_an_empty_history()
    {
        var result = UsageStore.Load(FilePath, D0);

        Assert.Empty(result.Usage.Days);
        Assert.False(result.Unreadable);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version":2,"days":{}}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"days":{"yesterday":{"rows":{},"phoneAdapter":0}}}""")]
    public void Corrupt_file_is_set_aside_and_starts_empty(string content)
    {
        File.WriteAllText(FilePath, content);

        var result = UsageStore.Load(FilePath, D0);

        Assert.Empty(result.Usage.Days);
        Assert.False(result.Unreadable);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(content, File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void A_second_corrupt_file_replaces_the_first_bad_copy()
    {
        File.WriteAllText(FilePath + ".bad", "old");
        File.WriteAllText(FilePath, "{ broken");

        UsageStore.Load(FilePath, D0);

        Assert.Equal("{ broken", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void Negative_values_are_clamped_to_zero()
    {
        File.WriteAllText(FilePath,
            """{"version":1,"days":{"2026-10-02":{"rows":{"x":{"phone":{"up":-5,"down":7},"lan":{"up":0,"down":0},"kept":-1}},"phoneAdapter":-9}}}""");

        var day = UsageStore.Load(FilePath, D0).Usage.Days[D0];

        Assert.Equal(new UsageRow(new Traffic(0, 7), default, 0), day.Rows["x"]);
        Assert.Equal(0, day.PhoneAdapterBytes);
    }

    [Fact]
    public void A_row_with_missing_parts_loads_as_zeros()
    {
        File.WriteAllText(FilePath, """{"version":1,"days":{"2026-10-02":{"rows":{"x":{"kept":3}},"phoneAdapter":1}}}""");

        Assert.Equal(new UsageRow(default, default, 3), UsageStore.Load(FilePath, D0).Usage.Days[D0].Rows["x"]);
    }

    [Fact]
    public void Locked_file_is_reported_unreadable_and_left_untouched()
    {
        UsageStore.Save(FilePath, Sample());
        var before = File.ReadAllText(FilePath);

        UsageLoadResult result;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
            result = UsageStore.Load(FilePath, D0);

        Assert.True(result.Unreadable);
        Assert.Empty(result.Usage.Days);
        Assert.Equal(before, File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void Days_older_than_the_retention_are_dropped_on_load()
    {
        var row = Sample().Days[D0];
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var offset in new[] { 40, 35, 34, 0 }) days[D0.AddDays(-offset)] = row;
        UsageStore.Save(FilePath, new UsageSnapshot(days, new Dictionary<string, UsageRate>()));

        var loaded = UsageStore.Load(FilePath, D0).Usage;

        Assert.Equal(new[] { D0.AddDays(-34), D0 }, loaded.Days.Keys.Order());
    }

    [Fact]
    public void A_day_with_too_many_rows_keeps_the_largest_and_folds_the_rest_into_Other()
    {
        var rows = new Dictionary<string, UsageRow>();
        for (var i = 0; i < UsageCounter.MaxRowsPerDay + 20; i++)
            rows["app:a" + i + ".exe"] = new UsageRow(new Traffic(0, i + 1), default, 0);
        rows[UsageAttribution.UnattributedKey] = new UsageRow(new Traffic(0, 99_999), default, 0);
        var total = rows.Values.Sum(r => r.Total);
        UsageStore.Save(FilePath, new UsageSnapshot(
            new Dictionary<DateOnly, UsageDay> { [D0] = new(rows, 0) }, new Dictionary<string, UsageRate>()));

        var loaded = UsageStore.Load(FilePath, D0).Usage.Days[D0].Rows;

        Assert.Equal(UsageCounter.MaxRowsPerDay + 2, loaded.Count); // the cap, plus "other" and "unattributed"
        Assert.Equal(total, loaded.Values.Sum(r => r.Total));
        Assert.False(loaded.ContainsKey("app:a0.exe")); // the smallest rows folded away
        Assert.Equal(Enumerable.Range(1, 20).Sum(), loaded[UsageAttribution.OtherKey].Total);
    }

    [Fact]
    public void Empty_days_are_not_written()
    {
        UsageStore.Save(FilePath, new UsageSnapshot(
            new Dictionary<DateOnly, UsageDay> { [D0] = new(new Dictionary<string, UsageRow>(), 0) }, new Dictionary<string, UsageRate>()));

        Assert.Empty(UsageStore.Load(FilePath, D0).Usage.Days);
    }
}
