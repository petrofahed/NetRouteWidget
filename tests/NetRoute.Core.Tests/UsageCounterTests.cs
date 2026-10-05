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

    // ---- the phone share is a running balance against the Windows adapter counter ----

    static long PhoneTotal(UsageCounter counter, DateOnly day) =>
        counter.Snapshot().Days[day].Rows.Values.Sum(r => r.Phone.Total);

    static long UnattributedPhone(UsageCounter counter, DateOnly day) =>
        counter.Snapshot().Days[day].Rows.TryGetValue(UsageAttribution.UnattributedKey, out var row) ? row.Phone.Total : 0;

    [Fact]
    public void Burst_timing_skew_between_the_snapshot_and_the_adapter_reading_cancels_out()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);

        // The adapter already counted 1000 bytes, the connections only show 400 so far.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 400, process: "a.exe")], adapter: 11_000), Attr);
        Assert.Equal(600, UnattributedPhone(counter, D0));

        // The rest of the burst shows up in the connections; the adapter has nothing new.
        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 1000, process: "a.exe")], adapter: 11_000), Attr);

        Assert.Equal(0, UnattributedPhone(counter, D0));
        Assert.Equal(1000, PhoneTotal(counter, D0));
        Assert.Equal(1000, counter.Snapshot().Days[D0].PhoneAdapterBytes);
    }

    [Fact]
    public void Real_missed_phone_bytes_are_still_booked_as_unattributed()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 500, process: "a.exe")], down: 2000, adapter: 12_000), Attr);

        Assert.Equal(1500, UnattributedPhone(counter, D0));
    }

    [Fact]
    public void Framing_overhead_of_the_adapter_is_still_booked_as_unattributed()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 1000, process: "a.exe")], adapter: 11_040), Attr);

        Assert.Equal(40, UnattributedPhone(counter, D0));
    }

    [Fact]
    public void A_deficit_that_could_not_be_taken_back_is_used_up_by_the_next_positive_difference()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);

        // 600 seen, the adapter shows nothing yet and there is no unattributed share to take it back from: carry -600.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 600, process: "a.exe")], adapter: 10_000), Attr);
        Assert.Equal(0, UnattributedPhone(counter, D0));

        // 100 seen, the adapter delta is 1000: 1000 - 100 - 600 = 300.
        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 700, process: "a.exe")], adapter: 11_000), Attr);

        Assert.Equal(300, UnattributedPhone(counter, D0));
        Assert.Equal(1000, PhoneTotal(counter, D0));
    }

    [Fact]
    public void A_take_back_reduces_down_first_then_up_and_never_goes_below_zero()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);
        // Missed: 100 up, 300 down; all of it phone (adapter delta 400, nothing seen).
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 0, process: "a.exe")], up: 100, down: 300, adapter: 10_400), Attr);
        Assert.Equal(new Traffic(100, 300), Row(counter, D0, UsageAttribution.UnattributedKey).Phone);

        // 450 seen, adapter delta 0: balance -450 takes the 300 down and 100 up, 50 stays as a deficit.
        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 450, process: "a.exe")], up: 100, down: 750, adapter: 10_400), Attr);

        Assert.Equal(0, UnattributedPhone(counter, D0)); // fully taken back, never negative
        // The 50 byte deficit offsets the next positive difference: 500 - 0 - 50.
        counter.Update(Poll(T0.AddSeconds(3), [Conn("1", 0, 450, process: "a.exe")], up: 100, down: 750, adapter: 10_900), Attr);
        Assert.Equal(450, UnattributedPhone(counter, D0));
    }

    [Fact]
    public void The_deficit_is_capped_at_eight_mebibytes()
    {
        const long mib = 1024 * 1024;
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 20 * mib, process: "a.exe")], adapter: 10_000), Attr); // -20 MiB, capped to -8

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 20 * mib, process: "a.exe")], adapter: 10_000 + 9 * mib), Attr);

        Assert.Equal(mib, UnattributedPhone(counter, D0));
    }

    [Fact]
    public void Reset_baselines_forgets_the_deficit()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 600, process: "a.exe")], adapter: 10_000), Attr); // carry -600

        counter.ResetBaselines();
        counter.Update(Poll(T0.AddSeconds(2), [], adapter: 20_000), Attr);
        counter.Update(Poll(T0.AddSeconds(3), [], down: 0, adapter: 21_000), Attr);

        Assert.Equal(1000, UnattributedPhone(counter, D0));
    }

    [Fact]
    public void A_poll_without_an_adapter_reading_forgets_the_deficit()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 600, process: "a.exe")], adapter: 10_000), Attr); // carry -600

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 600, process: "a.exe")], adapter: null), Attr);
        counter.Update(Poll(T0.AddSeconds(3), [Conn("1", 0, 600, process: "a.exe")], adapter: 20_000), Attr);
        counter.Update(Poll(T0.AddSeconds(4), [Conn("1", 0, 600, process: "a.exe")], adapter: 21_000), Attr);

        Assert.Equal(1000, UnattributedPhone(counter, D0));
    }

    [Fact]
    public void Over_a_bursty_session_the_phone_column_follows_the_adapter_total()
    {
        var counter = new UsageCounter(null, D0);
        long adapter = 1_000_000, conn = 0;
        counter.Update(Poll(T0, [Conn("1", 0, conn, process: "a.exe")], adapter: adapter), Attr);

        // Each burst: the adapter leads by 300 bytes for one poll, then the connection catches up.
        for (var i = 1; i <= 20; i++)
        {
            adapter += 5000;
            conn += 4700;
            counter.Update(Poll(T0.AddSeconds(2 * i), [Conn("1", 0, conn, process: "a.exe")], adapter: adapter), Attr);
            conn += 300;
            counter.Update(Poll(T0.AddSeconds(2 * i + 1), [Conn("1", 0, conn, process: "a.exe")], adapter: adapter), Attr);
        }

        Assert.Equal(100_000, counter.Snapshot().Days[D0].PhoneAdapterBytes);
        Assert.Equal(100_000, PhoneTotal(counter, D0));
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

        Assert.Equal(new UsageRate(0, 1_000_000), counter.Snapshot().Rates["youtube"]);

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);
        Assert.True(counter.Snapshot().Rates.ContainsKey("youtube")); // still inside the window

        counter.Update(Poll(T0.AddSeconds(3), [Conn("1", 0, 3_000_000, host: "youtube.com", lanOnly: true)]), Attr);
        Assert.False(counter.Snapshot().Rates.ContainsKey("youtube"));
    }

    [Fact]
    public void A_rows_rate_also_says_how_much_of_it_is_upload()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [
            Conn("1", 3_000_000, 6_000_000, exit: "phone", process: "chrome.exe"),
            Conn("2", 0, 3_000_000, process: "chrome.exe", lanOnly: true)]), Attr);

        var snapshot = counter.Snapshot();

        Assert.Equal(new UsageRate(3_000_000, 1_000_000), snapshot.Rates["app:chrome.exe"]); // up and down together, as before
        Assert.Equal(new UsageUpRate(1_000_000, 0), snapshot.UpRates!["app:chrome.exe"]);
    }

    [Fact]
    public void A_side_below_one_kilobyte_per_second_has_no_upload_rate_either()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [
            Conn("1", 0, 3_000_000, exit: "phone", process: "chrome.exe"),
            Conn("2", 3000, 0, process: "chrome.exe", lanOnly: true)]), Attr); // LAN: 1000 B/s, all upload

        Assert.Equal(new UsageUpRate(0, 0), counter.Snapshot().UpRates!["app:chrome.exe"]);
    }

    [Fact]
    public void A_row_with_only_LAN_traffic_has_a_LAN_rate_and_a_zero_phone_rate()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "steam.exe", lanOnly: true)]), Attr);

        var rate = counter.Snapshot().Rates["app:steam.exe"];

        Assert.Equal(1_000_000, rate.LanBytesPerSecond);
        Assert.Equal(0, rate.PhoneBytesPerSecond);
        Assert.Equal(1_000_000, rate.Total);
    }

    [Fact]
    public void A_row_active_on_both_exits_shows_both_rates()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [
            Conn("1", 0, 6_000_000, exit: "phone", process: "chrome.exe"),
            Conn("2", 0, 3_000_000, process: "chrome.exe", lanOnly: true)]), Attr);

        var rate = counter.Snapshot().Rates["app:chrome.exe"];

        Assert.Equal(new UsageRate(2_000_000, 1_000_000), rate);
        Assert.Equal(3_000_000, rate.Total);
    }

    [Fact]
    public void A_side_below_one_kilobyte_per_second_reads_zero_while_the_other_side_is_active()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [
            Conn("1", 0, 3_000_000, exit: "phone", process: "chrome.exe"),
            Conn("2", 0, 3000, process: "chrome.exe", lanOnly: true)]), Attr); // LAN: 1000 B/s

        var rate = counter.Snapshot().Rates["app:chrome.exe"];

        Assert.Equal(new UsageRate(1_000_000, 0), rate);
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
    public void A_clock_that_steps_back_drops_the_rate_samples_from_its_future()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0.AddDays(1), [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.True(counter.Snapshot().Rates.ContainsKey("app:a.exe"));

        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);

        Assert.Empty(counter.Snapshot().Rates);
    }

    [Fact]
    public void A_poll_without_totals_keeps_the_totals_baseline()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")]), Attr);

        counter.Update(new UsagePoll([Conn("1", 0, 1000, process: "a.exe")], null, null, null, true, T0.AddSeconds(1)), Attr);
        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 1500, process: "a.exe")]), Attr);

        var rows = counter.Snapshot().Days[D0].Rows;
        Assert.DoesNotContain(UsageAttribution.UnattributedKey, rows.Keys);
        Assert.Equal(1500, rows["app:a.exe"].Total);
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

    [Fact]
    public void ClearRates_empties_the_rates_but_keeps_the_baselines()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.NotEmpty(counter.Snapshot().Rates);

        counter.ClearRates();
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 3_000_100, process: "a.exe")]), Attr);

        Assert.Equal(3_000_100, Row(counter, D0, "app:a.exe").Phone.Down); // the connection was not counted twice
        Assert.Empty(counter.Snapshot().Rates); // 100 B/s is idle
    }

    // ---- Live: the all-traffic speed through each exit (the widget card) ----

    [Fact]
    public void Live_is_zero_before_any_poll()
    {
        var counter = new UsageCounter(null, D0);

        Assert.Equal(new UsageRate(0, 0), counter.Live);
    }

    [Fact]
    public void Live_phone_is_the_phone_exit_growth_over_the_window()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);

        Assert.Equal(new UsageRate(1_000_000, 0), counter.Live);
    }

    [Fact]
    public void Live_lan_is_the_lan_exit_growth_over_the_window()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 6_000_000, process: "steam.exe", lanOnly: true)]), Attr);

        Assert.Equal(new UsageRate(0, 2_000_000), counter.Live);
    }

    [Fact]
    public void Live_adds_all_rows_together()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [
            Conn("1", 0, 3_000_000, process: "a.exe"),
            Conn("2", 0, 3_000_000, host: "youtube.com"),
            Conn("3", 0, 3_000_000, process: "b.exe", lanOnly: true)]), Attr);

        Assert.Equal(new UsageRate(2_000_000, 1_000_000), counter.Live);
    }

    [Fact]
    public void Live_counts_bytes_of_short_connections_that_only_show_in_the_totals_and_the_adapter()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);

        // 9 MB passed in total, none of it on an open connection; the phone adapter carried 6 MB of it, so 3 MB went over the LAN.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 0, process: "a.exe")], down: 9_000_000, adapter: 6_010_000), Attr);

        Assert.Equal(new UsageRate(2_000_000, 1_000_000), counter.Live);
        Assert.Empty(counter.Snapshot().Rates); // unattributed bytes still never show as a per-row rate
    }

    [Fact]
    public void Live_counts_seen_and_unattributed_bytes_of_the_same_poll_together()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);

        // 3 MB seen on the connection; the adapter carried 6 MB, so 3 MB were short connections.
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 3_000_000, process: "a.exe")], down: 3_000_000, adapter: 6_010_000), Attr);

        Assert.Equal(2_000_000, counter.Live.PhoneBytesPerSecond);
    }

    [Fact]
    public void Live_fades_three_seconds_after_the_last_bytes()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);

        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.Equal(1_000_000, counter.Live.PhoneBytesPerSecond); // still inside the window

        counter.Update(Poll(T0.AddSeconds(3), [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.Equal(new UsageRate(0, 0), counter.Live);
    }

    [Fact]
    public void Live_drops_samples_from_the_future_of_a_clock_that_stepped_back()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);

        counter.Update(Poll(T0.AddSeconds(-10), [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);

        Assert.Equal(new UsageRate(0, 0), counter.Live);
    }

    [Fact]
    public void Live_reads_zero_for_a_side_below_one_kilobyte_per_second()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [
            Conn("1", 0, 3_000_000, process: "a.exe"),
            Conn("2", 0, 3000, process: "b.exe", lanOnly: true)]), Attr); // LAN: 1000 B/s

        Assert.Equal(new UsageRate(1_000_000, 0), counter.Live);
    }

    [Fact]
    public void Live_trickle_is_idle()
    {
        var counter = new UsageCounter(null, D0);

        counter.Update(Poll(T0, [Conn("1", 0, 1000, process: "a.exe")]), Attr);

        Assert.Equal(new UsageRate(0, 0), counter.Live);
    }

    [Fact]
    public void Live_is_reset_by_ClearRates_and_ResetBaselines()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.NotEqual(0, counter.Live.Total);

        counter.ClearRates();
        Assert.Equal(new UsageRate(0, 0), counter.Live);

        counter.Update(Poll(T0.AddSeconds(1), [Conn("2", 0, 3_000_000, process: "a.exe")]), Attr);
        Assert.NotEqual(0, counter.Live.Total);

        counter.ResetBaselines();
        Assert.Equal(new UsageRate(0, 0), counter.Live);
    }

    [Fact]
    public void Live_after_a_phone_take_back_counts_the_burst_once_and_is_never_negative()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 0, process: "a.exe")], adapter: 10_000), Attr);

        // Burst start: the adapter is 3 MB ahead of the connections (booked as unattributed phone bytes).
        counter.Update(Poll(T0.AddSeconds(1), [Conn("1", 0, 0, process: "a.exe")], adapter: 3_010_000), Attr);
        Assert.Equal(1_000_000, counter.Live.PhoneBytesPerSecond);

        // Burst end: the connection shows the 3 MB, the adapter nothing new. The 3 MB booked earlier is taken back, so the
        // burst counts once (3 MB), not twice. This poll's own net is zero, so no sample is enqueued for it.
        counter.Update(Poll(T0.AddSeconds(2), [Conn("1", 0, 3_000_000, process: "a.exe")], adapter: 3_010_000), Attr);
        Assert.Equal(1_000_000, counter.Live.PhoneBytesPerSecond);

        // The positive sample has aged out: nothing is left, and the result is not negative.
        counter.Update(Poll(T0.AddSeconds(4), [Conn("1", 0, 3_000_000, process: "a.exe")], adapter: 3_010_000), Attr);
        Assert.Equal(0, counter.Live.PhoneBytesPerSecond);
    }

    // ---- jittered one-second polls must never put a 4th sample into the 3-second window ----

    static readonly double[] JitteredSeconds = [0, 0.99, 2.01, 2.99, 4.0, 5.01, 5.99, 7.0, 8.0, 8.99];

    [Fact]
    public void Live_of_steady_traffic_with_jittered_one_second_polls_is_exact_at_every_poll()
    {
        var counter = new UsageCounter(null, D0);
        long total = 0;
        for (var i = 0; i < JitteredSeconds.Length; i++)
        {
            total += 1_000_000;
            counter.Update(Poll(T0.AddSeconds(JitteredSeconds[i]), [Conn("1", 0, total, process: "a.exe")]), Attr);
            if (i >= 2) Assert.Equal(1_000_000, counter.Live.PhoneBytesPerSecond);
        }
    }

    [Fact]
    public void A_rows_rate_of_steady_traffic_with_jittered_one_second_polls_is_exact_at_every_poll()
    {
        var counter = new UsageCounter(null, D0);
        long total = 0;
        for (var i = 0; i < JitteredSeconds.Length; i++)
        {
            total += 1_000_000;
            counter.Update(Poll(T0.AddSeconds(JitteredSeconds[i]), [Conn("1", 0, total, process: "a.exe")]), Attr);
            if (i >= 2) Assert.Equal(new UsageRate(1_000_000, 0), counter.Snapshot().Rates["app:a.exe"]);
        }
    }

    [Fact]
    public void TodayTotals_of_an_empty_counter_is_zero()
    {
        Assert.Equal(new ExitTotals(0, 0), new UsageCounter(null, D0).TodayTotals(D0));
    }

    [Fact]
    public void TodayTotals_sums_every_row_per_exit_including_unattributed()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 10, 90, exit: "phone", host: "youtube.com"), Conn("2", 0, 300, exit: "lan", process: "steam.exe")],
            up: 10, down: 90 + 300 + 50), Attr); // 50 bytes were missed by the connection list
        var rows = counter.Snapshot().Days[D0].Rows.Values;

        var totals = counter.TodayTotals(D0);

        Assert.Equal(rows.Sum(r => r.Phone.Total), totals.PhoneBytes);
        Assert.Equal(rows.Sum(r => r.Lan.Total), totals.LanBytes);
        Assert.True(counter.Snapshot().Days[D0].Rows.ContainsKey(UsageAttribution.UnattributedKey));
        Assert.True(totals.PhoneBytes + totals.LanBytes >= 400);
    }

    [Fact]
    public void TodayTotals_counts_unattributed_bytes()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [], up: 0, down: 500, adapter: 1000), Attr);
        counter.Update(Poll(T0.AddSeconds(1), [], up: 0, down: 1000, adapter: 1500), Attr);

        var totals = counter.TodayTotals(D0);

        Assert.Equal(counter.Snapshot().Days[D0].Rows[UsageAttribution.UnattributedKey].Phone.Total, totals.PhoneBytes);
        Assert.True(totals.PhoneBytes > 0);
    }

    [Fact]
    public void TodayTotals_leaves_out_other_days()
    {
        var counter = new UsageCounter(null, D0);
        counter.Update(Poll(T0, [Conn("1", 0, 100)]), Attr);

        Assert.Equal(new ExitTotals(100, 0), counter.TodayTotals(D0));
        Assert.Equal(new ExitTotals(0, 0), counter.TodayTotals(D0.AddDays(1)));
        Assert.Equal(new ExitTotals(0, 0), counter.TodayTotals(D0.AddDays(-1)));
    }

    [Fact]
    public void TodayTotals_includes_a_restored_snapshot_and_Clear_zeroes_it()
    {
        var counter = new UsageCounter(SnapshotWith(D0, "youtube", 700), D0);

        Assert.Equal(new ExitTotals(700, 0), counter.TodayTotals(D0));

        counter.Clear();

        Assert.Equal(new ExitTotals(0, 0), counter.TodayTotals(D0));
    }
}
