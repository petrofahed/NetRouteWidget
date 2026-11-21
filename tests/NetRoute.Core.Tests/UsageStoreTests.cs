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

    static LedgerDay SampleLedger() => new(
        new Dictionary<ConnectionId, Traffic>
        {
            [ConnectionId.Phone] = new(1, 2),
            [ConnectionId.Lan] = new(3, 4),
            [ConnectionId.Wifi] = new(5, 6),
        },
        new TimeOnly(8, 13, 0));

    static UsageSnapshot WithDay(UsageDay day) => new(
        new Dictionary<DateOnly, UsageDay> { [D0] = day }, new Dictionary<string, UsageRate>());

    [Fact]
    public void A_ledger_round_trips_through_the_file()
    {
        UsageStore.Save(FilePath, Sample().WithLedger(new Dictionary<DateOnly, LedgerDay> { [D0] = SampleLedger() }));

        var day = UsageStore.Load(FilePath, D0).Usage.Days[D0];

        Assert.NotNull(day.Ledger);
        Assert.Equal(new Traffic(1, 2), day.Ledger.Totals[ConnectionId.Phone]);
        Assert.Equal(new Traffic(3, 4), day.Ledger.Totals[ConnectionId.Lan]);
        Assert.Equal(new Traffic(5, 6), day.Ledger.Totals[ConnectionId.Wifi]);
        Assert.Equal(new TimeOnly(8, 13, 0), day.Ledger.Since);
        Assert.Equal(777, day.PhoneAdapterBytes); // the rest of the day is untouched
        Assert.Equal(2, day.Rows.Count);
        var adapters = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath))
            .RootElement.GetProperty("days").GetProperty("2026-10-02").GetProperty("adapters");
        Assert.Equal("08:13:00", adapters.GetProperty("since").GetString());
        Assert.Equal(2, adapters.GetProperty("phone").GetProperty("down").GetInt64());
    }

    [Fact]
    public void A_day_with_only_ledger_data_is_saved()
    {
        UsageStore.Save(FilePath, WithDay(new UsageDay(new Dictionary<string, UsageRow>(), 0, 0, SampleLedger())));

        var day = Assert.Single(UsageStore.Load(FilePath, D0).Usage.Days);

        Assert.Equal(D0, day.Key);
        Assert.Empty(day.Value.Rows);
        Assert.Equal(new Traffic(3, 4), day.Value.Ledger!.Totals[ConnectionId.Lan]);
    }

    [Fact]
    public void A_file_without_adapters_still_loads_with_no_ledger()
    {
        UsageStore.Save(FilePath, Sample());
        Assert.DoesNotContain("adapters", File.ReadAllText(FilePath));

        var day = UsageStore.Load(FilePath, D0).Usage.Days[D0];

        Assert.Null(day.Ledger);
        Assert.Equal(777, day.PhoneAdapterBytes);
        Assert.Equal(2, day.Rows.Count);
    }

    [Fact]
    public void The_file_version_is_still_1()
    {
        UsageStore.Save(FilePath, Sample().WithLedger(new Dictionary<DateOnly, LedgerDay> { [D0] = SampleLedger() }));

        var version = System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath)).RootElement.GetProperty("version");

        Assert.Equal(1, version.GetInt32());
    }

    [Fact]
    public void A_damaged_adapters_field_keeps_the_rows()
    {
        const string Prefix = """{"version":1,"days":{"2026-10-02":{"rows":{"x":{"kept":3}},"phoneAdapter":5,"adapters":""";
        const string Suffix = "}}}";

        // Negative numbers clamp, a wrongly typed connection is zero, a bad since is dropped.
        File.WriteAllText(FilePath,
            Prefix + """{"phone":{"up":-5,"down":7},"lan":"oops","wifi":{"up":"x","down":2},"since":"not a time"}""" + Suffix);
        var result = UsageStore.Load(FilePath, D0);
        var day = result.Usage.Days[D0];
        Assert.False(result.Unreadable);
        Assert.False(File.Exists(FilePath + ".bad"));
        Assert.Equal(new UsageRow(default, default, 3), day.Rows["x"]);
        Assert.Equal(5, day.PhoneAdapterBytes);
        Assert.Equal(new Traffic(0, 7), day.Ledger!.Totals[ConnectionId.Phone]);
        Assert.Equal(default, day.Ledger.Totals.GetValueOrDefault(ConnectionId.Lan));
        Assert.Equal(new Traffic(0, 2), day.Ledger.Totals[ConnectionId.Wifi]);
        Assert.Null(day.Ledger.Since);

        // An "adapters" that is not an object is ignored altogether.
        foreach (var junk in new[] { "\"junk\"", "[1,2]", "7", "null", "true" })
        {
            File.WriteAllText(FilePath, Prefix + junk + Suffix);
            result = UsageStore.Load(FilePath, D0);
            day = result.Usage.Days[D0];
            Assert.False(result.Unreadable);
            Assert.False(File.Exists(FilePath + ".bad"));
            Assert.Null(day.Ledger);
            Assert.Equal(new UsageRow(default, default, 3), day.Rows["x"]);
            Assert.Equal(5, day.PhoneAdapterBytes);
        }
    }

    [Fact]
    public void Capping_a_runaway_day_keeps_its_ledger()
    {
        var rows = new Dictionary<string, UsageRow>();
        for (var i = 0; i < UsageCounter.MaxRowsPerDay + 20; i++)
            rows["app:a" + i + ".exe"] = new UsageRow(new Traffic(0, i + 1), default, 0);
        UsageStore.Save(FilePath, WithDay(new UsageDay(rows, 9, 1, SampleLedger())));

        var day = UsageStore.Load(FilePath, D0).Usage.Days[D0];

        Assert.True(day.Rows.ContainsKey(UsageAttribution.OtherKey)); // CapRows really ran
        Assert.Equal(9, day.PhoneAdapterBytes);
        Assert.Equal(1, day.Gaps);
        Assert.Equal(new Traffic(5, 6), day.Ledger!.Totals[ConnectionId.Wifi]);
        Assert.Equal(new TimeOnly(8, 13, 0), day.Ledger.Since);
    }

    [Fact]
    public void WithLedger_adds_missing_days_and_leaves_other_days_alone()
    {
        var yesterday = D0.AddDays(-1);
        var tomorrow = D0.AddDays(1);
        var untouched = new UsageDay(new Dictionary<string, UsageRow> { ["y"] = UsageRow.Empty }, 4, 1);
        var rates = new Dictionary<string, UsageRate> { ["youtube"] = new(10, 20) };
        var upRates = new Dictionary<string, UsageUpRate> { ["youtube"] = new(1, 2) };
        var usage = new UsageSnapshot(
            new Dictionary<DateOnly, UsageDay> { [D0] = Sample().Days[D0], [yesterday] = untouched }, rates, upRates);

        var result = usage.WithLedger(new Dictionary<DateOnly, LedgerDay>
        {
            [D0] = SampleLedger(),
            [tomorrow] = new(new Dictionary<ConnectionId, Traffic>(), new TimeOnly(0, 5, 0)),
        });

        Assert.Same(untouched, result.Days[yesterday]);
        Assert.Null(result.Days[yesterday].Ledger);
        Assert.Equal(Sample().Days[D0].Rows, result.Days[D0].Rows);
        Assert.Equal(777, result.Days[D0].PhoneAdapterBytes);
        Assert.Equal(2, result.Days[D0].Gaps);
        Assert.Equal(new Traffic(1, 2), result.Days[D0].Ledger!.Totals[ConnectionId.Phone]);
        var added = result.Days[tomorrow];
        Assert.Empty(added.Rows);
        Assert.Equal(0, added.PhoneAdapterBytes);
        Assert.Equal(0, added.Gaps);
        Assert.Equal(new TimeOnly(0, 5, 0), added.Ledger!.Since);
        Assert.Same(rates, result.Rates);
        Assert.Same(upRates, result.UpRates);
        Assert.Null(usage.Days[D0].Ledger); // the original snapshot is not changed

        var ledgerDays = result.LedgerDays();
        Assert.Equal(new[] { D0, tomorrow }, ledgerDays.Keys.Order());
    }
}
