namespace NetRoute.Core.Tests;

public class UsageCounterMeasuredTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("onedrive", "sync", "Sync", "OneDrive", ["onedrive.exe"], [], true)];
    static readonly UsageAttribution Attr = UsageAttribution.Build(Catalog, new SmartRoutingSettings());
    static readonly DateOnly D0 = new(2026, 10, 2);
    static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    static readonly IReadOnlyList<MeasuredTraffic> None = [];

    static MeasuredTraffic Sample(string? process, RouteExit exit, long up, long down) => new(process, exit, new Traffic(up, down));

    static UsageRow Row(UsageCounter counter, DateOnly day, string key) => counter.Snapshot().Days[day].Rows[key];

    [Fact]
    public void Bytes_are_counted_per_application_and_exit()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([
            Sample("chrome.exe", RouteExit.Phone, 10, 90),
            Sample("chrome.exe", RouteExit.Lan, 0, 50),
            Sample(null, RouteExit.Phone, 0, 7),
        ], Attr, null, T0);

        Assert.Equal(new UsageRow(new Traffic(10, 90), new Traffic(0, 50), 0), Row(counter, D0, "app:chrome.exe"));
        Assert.Equal(new Traffic(0, 7), Row(counter, D0, UsageAttribution.OtherKey).Phone);
    }

    [Fact]
    public void A_built_in_application_item_keeps_its_own_row()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("OneDrive.exe", RouteExit.Lan, 5, 95)], Attr, null, T0);

        Assert.Equal(100, Row(counter, D0, "onedrive").Lan.Total);
    }

    [Fact]
    public void Nothing_is_kept_off_4G_by_measured_traffic()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Lan, 0, 5000)], Attr, null, T0);

        Assert.Equal(0, Row(counter, D0, "app:chrome.exe").Kept);
        Assert.Equal(0, counter.TodayKept(D0).Total);
    }

    [Fact]
    public void Empty_and_zero_samples_add_nothing_and_do_not_dirty_the_counter()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured(None, Attr, null, T0);
        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 0)], Attr, null, T0.AddSeconds(1));

        Assert.False(counter.TakeDirty());
        Assert.Empty(counter.Snapshot().Days);
    }

    [Fact]
    public void Adding_marks_the_counter_dirty_once()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 10)], Attr, null, T0);

        Assert.True(counter.TakeDirty());
        Assert.False(counter.TakeDirty());
    }

    [Fact]
    public void Live_speed_follows_the_measured_bytes_and_decays_to_zero_when_traffic_stops()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 6000)], Attr, null, T0);
        Assert.Equal(2000, counter.Live.PhoneBytesPerSecond); // 6000 bytes over the 3 s window
        Assert.Equal(2000, counter.Snapshot().Rates["app:chrome.exe"].PhoneBytesPerSecond);

        counter.AddMeasured(None, Attr, null, T0.AddSeconds(4));

        Assert.Equal(new UsageRate(0, 0), counter.Live);
        Assert.Empty(counter.Snapshot().Rates);
    }

    [Fact]
    public void The_windows_phone_adapter_total_grows_between_measured_polls()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured(None, Attr, 1000, T0); // the first reading is only a baseline
        counter.AddMeasured(None, Attr, 1600, T0.AddSeconds(1));
        counter.AddMeasured(None, Attr, null, T0.AddSeconds(2)); // unreadable: baseline dropped, nothing added
        counter.AddMeasured(None, Attr, 2000, T0.AddSeconds(3));

        Assert.Equal(600, counter.Snapshot().Days[D0].PhoneAdapterBytes);
    }

    [Fact]
    public void A_new_local_day_starts_a_new_day_of_history()
    {
        var counter = new UsageCounter(null, D0);

        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 10)], Attr, null, new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.Zero));
        counter.AddMeasured([Sample("chrome.exe", RouteExit.Phone, 0, 20)], Attr, null, new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero));

        Assert.Equal(10, Row(counter, D0, "app:chrome.exe").Phone.Total);
        Assert.Equal(20, Row(counter, D0.AddDays(1), "app:chrome.exe").Phone.Total);
    }

    [Fact]
    public void Rows_beyond_the_daily_cap_fold_into_other()
    {
        var counter = new UsageCounter(null, D0);
        var many = Enumerable.Range(0, UsageCounter.MaxRowsPerDay + 5)
            .Select(i => Sample($"app{i}.exe", RouteExit.Phone, 0, 10)).ToList();

        counter.AddMeasured(many, Attr, null, T0);

        var rows = counter.Snapshot().Days[D0].Rows;
        Assert.Equal(UsageCounter.MaxRowsPerDay + 1, rows.Count); // the cap plus "other"
        Assert.Equal(50, rows[UsageAttribution.OtherKey].Phone.Total);
    }
}
