namespace NetRoute.Core.Tests;

public class UsageCounterTests
{
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];
    static readonly UsageAttribution Attr = UsageAttribution.Build(Catalog, new SmartRoutingSettings());
    static readonly DateOnly D0 = new(2026, 10, 2);
    static readonly DateTimeOffset T0 = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    static SingBoxConnection Conn(string id, long up, long down, string exit = "phone", string? host = null,
                                  string? process = null, bool lanOnly = false) =>
        new(id, host, process, lanOnly ? ["lan", "lan-only"] : [exit, "default"], up, down);

    /// Totals default to the sum of the open connections, i.e. no closed connections.
    static UsagePoll Poll(DateTimeOffset now, SingBoxConnection[] conns, long? up = null, long? down = null,
                          long? adapter = null, bool phoneIsDefault = true) =>
        new(conns, up ?? conns.Sum(c => c.Upload), down ?? conns.Sum(c => c.Download), adapter, phoneIsDefault, now);

    static UsageRow Row(UsageCounter counter, DateOnly day, string key) => counter.Snapshot().Days[day].Rows[key];

    static UsageSnapshot SnapshotWith(DateOnly day, string key, long bytes) =>
        new(new Dictionary<DateOnly, UsageDay>
        {
            [day] = new(new Dictionary<string, UsageRow> { [key] = new(new Traffic(0, bytes), default, 0) }, 0),
        }, new Dictionary<string, UsageRate>());

    [Fact]
    public void A_first_sighting_counts_the_current_totals_and_later_polls_only_the_growth()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 10, 90, host: "youtube.com", process: "chrome.exe")]), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 15, 190, host: "youtube.com", process: "chrome.exe")]), Attr);

        Assert.Equal(new UsageRow(new Traffic(15, 190), default, 0), Row(counter, D0, "youtube"));
    }

    [Fact]
    public void The_exit_is_the_first_entry_of_the_chain()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 100, exit: "lan", process: "steam.exe"), Conn("2", 0, 7, exit: "phone", process: "steam.exe")]), Attr);

        var row = Row(counter, D0, "app:steam.exe");
        Assert.Equal(new Traffic(0, 100), row.Lan);
        Assert.Equal(new Traffic(0, 7), row.Phone);
    }

    [Fact]
    public void Lan_only_bytes_are_kept_off_4G_only_while_the_phone_is_the_default_exit()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, host: "youtube.com", lanOnly: true)], phoneIsDefault: true), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 1500, host: "youtube.com", lanOnly: true)], phoneIsDefault: false), Attr);

        var row = Row(counter, D0, "youtube");

        Assert.Equal(1500, row.Lan.Total);
        Assert.Equal(1000, row.Kept);
    }

    [Fact]
    public void A_reused_id_with_smaller_counters_counts_as_a_new_connection()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 1000, 0, process: "a.exe")]), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 5, 0, process: "a.exe")]), Attr);

        Assert.Equal(1005, Row(counter, D0, "app:a.exe").Phone.Up);
    }

    [Fact]
    public void A_connection_that_closes_and_a_new_one_with_the_same_id_later_is_counted_in_full()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 100, process: "a.exe")]), Attr);
        counter.Update(Poll(T0.AddSeconds(1), []), Attr);

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 40, process: "a.exe")]), Attr);

        Assert.Equal(140, Row(counter, D0, "app:a.exe").Phone.Down);
    }

    [Fact]
    public void Connections_that_moved_no_data_create_no_row()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")]), Attr);

        Assert.Empty(counter.Snapshot().Days);
        Assert.False(counter.TakeDirty());
    }

    [Fact]
    public void Bytes_of_closed_connections_become_an_unattributed_row_taken_from_the_phone_adapter()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")], adapter: 50_000), Attr);

        // Nothing grew on the open connection, but the totals grew by 2000: connections that closed between polls.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 1000, process: "a.exe")], down: 3000, adapter: 52_000), Attr);

        var unattributed = Row(counter, D0, UsageAttribution.UnattributedKey);
        Assert.Equal(new Traffic(0, 2000), unattributed.Phone);
        Assert.Equal(default, unattributed.Lan);
        Assert.Equal(2000, counter.Snapshot().Days[D0].PhoneAdapterBytes);
    }

    [Fact]
    public void Missed_bytes_the_phone_adapter_does_not_explain_are_lan_and_count_as_kept_while_the_phone_is_the_default()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 100, process: "a.exe")], adapter: 10_000), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 100, process: "a.exe")], down: 1600, adapter: 11_000), Attr);

        var unattributed = Row(counter, D0, UsageAttribution.UnattributedKey);
        Assert.Equal(new Traffic(0, 1000), unattributed.Phone);
        Assert.Equal(new Traffic(0, 500), unattributed.Lan);
        Assert.Equal(500, unattributed.Kept);
    }

    [Theory]
    [InlineData(true, 3000, 0)]
    [InlineData(false, 0, 3000)]
    public void Without_a_phone_adapter_reading_missed_bytes_follow_the_default_exit(bool phoneIsDefault, long phone, long lan)
    {
        var counter = new UsageCounter(null, D0);

        // First poll after sing-box started: 4000 bytes went through in total, one open connection explains 1000.
        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")], down: 4000, adapter: null, phoneIsDefault: phoneIsDefault), Attr);

        var unattributed = Row(counter, D0, UsageAttribution.UnattributedKey);
        Assert.Equal(phone, unattributed.Phone.Total);
        Assert.Equal(lan, unattributed.Lan.Total);
    }

    [Fact]
    public void An_adapter_counter_that_moves_backwards_is_a_new_baseline_not_negative_traffic()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 100, process: "a.exe")], adapter: 9_000_000), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 100, process: "a.exe")], adapter: 500), Attr);
        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 100, process: "a.exe")], adapter: 900), Attr);

        Assert.Equal(400, counter.Snapshot().Days[D0].PhoneAdapterBytes);
    }

    [Fact]
    public void Totals_going_backwards_after_a_restart_count_from_zero()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")]), Attr);

        // sing-box restarted without the caller resetting: every total, and every id, starts over.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("9", 0, 300, process: "a.exe")]), Attr);

        var snapshot = counter.Snapshot();
        Assert.Equal(1300, snapshot.Days[D0].Rows.Values.Sum(r => r.Phone.Total));
        Assert.DoesNotContain(snapshot.Days[D0].Rows.Values, r => r.Phone.Up < 0 || r.Phone.Down < 0);
    }

    [Fact]
    public void Reset_baselines_counts_open_connections_again_without_losing_history()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.NotEmpty(counter.Snapshot().Rates);

        counter.ResetBaselines();
        Assert.Empty(counter.Snapshot().Rates);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("2", 0, 400, process: "a.exe")]), Attr);

        Assert.Equal(3_000_400, Row(counter, D0, "app:a.exe").Phone.Down);
    }

    [Fact]
    public void Clear_wipes_the_history_but_does_not_recount_connections_that_are_still_open()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 5_000_000, process: "a.exe")]), Attr);

        counter.Clear();
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 5_000_000, process: "a.exe")]), Attr);

        Assert.Empty(counter.Snapshot().Days);
        Assert.True(counter.TakeDirty());
    }

    [Fact]
    public void TakeDirty_is_true_once_after_a_change()
    {
        var counter = new UsageCounter(null, D0);
        Assert.False(counter.TakeDirty());

        counter.Update(Poll(T0, [Conn("1", 0, 10, process: "a.exe")]), Attr);

        Assert.True(counter.TakeDirty());
        Assert.False(counter.TakeDirty());
    }

    // ---- rates ("Now") ----

    [Fact]
    public void A_busy_row_has_a_rate_over_the_last_three_seconds_that_fades_when_it_stops()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);

        Assert.Equal(new UsageRate(RouteExit.Lan, 1_000_000), counter.Snapshot().Rates["youtube"]);

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);
        Assert.True(counter.Snapshot().Rates.ContainsKey("youtube")); // still inside the window

        counter.Update(Poll(T0.AddSeconds(3), [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);
        Assert.False(counter.Snapshot().Rates.ContainsKey("youtube"));
    }

    [Fact]
    public void A_trickle_below_one_kilobyte_per_second_is_idle()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")]), Attr); // 333 B/s over the window

        Assert.Empty(counter.Snapshot().Rates);
    }

    [Fact]
    public void Unattributed_bytes_never_show_as_a_live_rate()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [], down: 9_000_000, adapter: null), Attr);

        Assert.Empty(counter.Snapshot().Rates);
    }

    // ---- days ----

    [Fact]
    public void A_download_crossing_midnight_splits_between_the_two_days()
    {
        var counter = new UsageCounter(null, D0);
        var before = new DateTimeOffset(2026, 10, 2, 23, 59, 59, TimeSpan.Zero);
        var after = new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero);

        counter.Update(Poll(before, [Conn("1", 0, 100, process: "a.exe")]), Attr);
        counter.Update(Poll(after, [Conn("1", 0, 150, process: "a.exe")]), Attr);

        Assert.Equal(100, Row(counter, D0, "app:a.exe").Phone.Down);
        Assert.Equal(50, Row(counter, D0.AddDays(1), "app:a.exe").Phone.Down);
    }

    [Fact]
    public void Days_older_than_the_retention_are_pruned_when_restored_and_on_rollover()
    {
        var oneRow = SnapshotWith(D0, "x", 1).Days[D0];
        var days = new Dictionary<DateOnly, UsageDay>();
        foreach (var offset in new[] { 40, 35, 34, 0 }) days[D0.AddDays(-offset)] = oneRow;
        var counter = new UsageCounter(new UsageSnapshot(days, new Dictionary<string, UsageRate>()), D0);

        Assert.Equal(new[] { D0.AddDays(-34), D0 }, counter.Snapshot().Days.Keys.Order());

        counter.Update(Poll(T0.AddDays(1), [Conn("1", 0, 10, process: "a.exe")]), Attr);

        Assert.DoesNotContain(D0.AddDays(-34), counter.Snapshot().Days.Keys);
        Assert.Contains(D0, counter.Snapshot().Days.Keys);
        Assert.Contains(D0.AddDays(1), counter.Snapshot().Days.Keys);
    }

    [Fact]
    public void A_clock_that_goes_backwards_writes_to_the_earlier_day()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0.AddDays(1), [Conn("1", 0, 100, process: "a.exe")]), Attr);

        counter.Update(Poll(T0, [Conn("1", 0, 160, process: "a.exe")]), Attr);

        Assert.Equal(100, Row(counter, D0.AddDays(1), "app:a.exe").Phone.Down);
        Assert.Equal(60, Row(counter, D0, "app:a.exe").Phone.Down);
    }

    [Fact]
    public void A_gap_is_recorded_on_the_day_and_survives_a_snapshot_round_trip()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 10, process: "a.exe")]), Attr);

        counter.NoteGap(D0);
        counter.NoteGap(D0);

        Assert.Equal(2, counter.Snapshot().Days[D0].Gaps);
        Assert.Equal(2, new UsageCounter(counter.Snapshot(), D0).Snapshot().Days[D0].Gaps);
        Assert.True(counter.TakeDirty());
    }

    // ---- scale ----

    [Fact]
    public void Five_thousand_connections_are_counted_correctly()
    {
        var counter = new UsageCounter(null, D0);
        var conns = Enumerable.Range(0, 5000).Select(i => Conn("c" + i, 1, 1)).ToArray();

        counter.Update(Poll(T0, conns), Attr);
        counter.Update(Poll(T0.AddSeconds(1), conns.Select(c => c with { Upload = 2, Download = 3 }).ToArray()), Attr);

        Assert.Equal(new Traffic(10_000, 15_000), Row(counter, D0, UsageAttribution.OtherKey).Phone);
    }

    [Fact]
    public void Rows_beyond_the_cap_fold_into_Other_and_no_bytes_are_lost()
    {
        var counter = new UsageCounter(null, D0);
        var conns = Enumerable.Range(0, UsageCounter.MaxRowsPerDay + 100).Select(i => Conn("c" + i, 0, 10, process: $"app{i}.exe")).ToArray();

        counter.Update(Poll(T0, conns), Attr);

        var rows = counter.Snapshot().Days[D0].Rows;
        Assert.True(rows.Count <= UsageCounter.MaxRowsPerDay + 2);
        Assert.Equal(10L * conns.Length, rows.Values.Sum(r => r.Phone.Total));
        Assert.True(rows[UsageAttribution.OtherKey].Phone.Total >= 1000);
    }

    // ---- today's kept-off-4G figure and restoring ----

    [Fact]
    public void TodayKept_lists_the_kept_bytes_per_row_including_the_unattributed_share()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, host: "youtube.com", lanOnly: true)], adapter: 10_000), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 1000, host: "youtube.com", lanOnly: true)], down: 1500, adapter: 10_000), Attr);

        var kept = counter.TodayKept(D0);

        Assert.Equal(D0, kept.Day);
        Assert.Equal(1000, kept.BytesByEntry["youtube"]);
        Assert.Equal(500, kept.BytesByEntry[UsageAttribution.UnattributedKey]);
        Assert.Equal(1500, kept.Total);
    }

    [Fact]
    public void TodayKept_of_a_day_with_no_data_is_empty()
    {
        Assert.Equal(0, new UsageCounter(null, D0).TodayKept(D0).Total);
    }

    [Fact]
    public void A_restored_snapshot_continues_where_it_left_off()
    {
        var counter = new UsageCounter(SnapshotWith(D0, "youtube", 700), D0);

        counter.Update(Poll(T0, [Conn("1", 0, 300, host: "youtube.com")]), Attr);

        Assert.Equal(1000, Row(counter, D0, "youtube").Phone.Down);
    }
}
