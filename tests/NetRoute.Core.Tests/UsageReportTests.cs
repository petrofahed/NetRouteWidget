namespace NetRoute.Core.Tests;

public class UsageReportTests
{
    static readonly DateOnly Today = new(2026, 10, 10);
    static readonly IReadOnlyList<RuleItem> Catalog =
        [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];

    static UsageSnapshot Snap(params (DateOnly Day, string Key, long Phone, long Lan, long Kept)[] items) => Snap(0, items);

    static UsageSnapshot Snap(long adapterPerDay, params (DateOnly Day, string Key, long Phone, long Lan, long Kept)[] items)
    {
        var days = items.GroupBy(i => i.Day).ToDictionary(
            g => g.Key,
            g => new UsageDay(
                g.ToDictionary(i => i.Key, i => new UsageRow(new Traffic(0, i.Phone), new Traffic(0, i.Lan), i.Kept)),
                adapterPerDay));
        return new UsageSnapshot(days, new Dictionary<string, UsageRate>());
    }

    static UsageSnapshot WithRates(UsageSnapshot s, params (string Key, UsageRate Rate)[] rates) =>
        s with { Rates = rates.ToDictionary(r => r.Key, r => r.Rate) };

    static UsageReportModel Build(UsageSnapshot usage, int range = 7, SmartRoutingSettings? settings = null,
                                  bool canAssign = true, UsageSort? sort = null, string? filter = null) =>
        UsageReport.Build(usage, Today, range, Catalog, settings ?? new SmartRoutingSettings(), canAssign, sort ?? UsageSort.Default, filter);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(7, 4)]
    [InlineData(15, 6)]
    [InlineData(30, 8)]
    public void A_range_is_today_plus_the_previous_days(int range, long expectedBytes)
    {
        // Days at offsets 0, 2, 3, 6, 7, 14, 15, 29 and 30 before today, 1 phone byte each.
        var usage = Snap(new[] { 0, 2, 3, 6, 7, 14, 15, 29, 30 }.Select(o => (Today.AddDays(-o), "app:a.exe", 1L, 0L, 0L)).ToArray());

        Assert.Equal(expectedBytes, Build(usage, range).PhoneTotal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(365)]
    public void An_unknown_range_falls_back_to_seven_days(int range)
    {
        var usage = Snap((Today.AddDays(-6), "app:a.exe", 1, 0, 0), (Today.AddDays(-7), "app:a.exe", 100, 0, 0));

        var model = Build(usage, range);

        Assert.Equal(7, model.RangeDays);
        Assert.Equal(1, model.PhoneTotal);
    }

    [Fact]
    public void Days_after_today_are_ignored()
    {
        Assert.Equal(0, Build(Snap((Today.AddDays(1), "app:a.exe", 5, 0, 0))).PhoneTotal);
    }

    [Fact]
    public void Rows_add_up_over_the_days_and_show_both_columns()
    {
        var usage = Snap((Today, "youtube", 10, 1000, 1000), (Today.AddDays(-1), "youtube", 5, 500, 500));

        var row = Assert.Single(Build(usage).Rows);

        Assert.Equal("YouTube", row.Name);
        Assert.Equal(15, row.PhoneBytes);
        Assert.Equal(1500, row.LanBytes);
    }

    [Fact]
    public void Totals_kept_off_and_adapter_total_cover_the_range_including_special_rows()
    {
        var usage = Snap(300,
            (Today, "youtube", 10, 1000, 1000),
            (Today, UsageAttribution.UnattributedKey, 40, 60, 60),
            (Today, UsageAttribution.OtherKey, 5, 0, 0),
            (Today.AddDays(-10), "youtube", 999, 999, 999));

        var model = Build(usage, 7);

        Assert.Equal(55, model.PhoneTotal);
        Assert.Equal(1060, model.LanTotal);
        Assert.Equal(1060, model.KeptOff);
        Assert.Equal(300, model.PhoneAdapterTotal); // only today's 300, the other day is outside the range
    }

    [Fact]
    public void A_row_with_only_a_live_rate_still_appears()
    {
        var usage = WithRates(Snap(), ("app:steam.exe", new UsageRate(RouteExit.Lan, 2_000_000)));

        var row = Assert.Single(Build(usage).Rows);

        Assert.Equal("app:steam.exe", row.Key);
        Assert.Equal(0, row.PhoneBytes + row.LanBytes);
        Assert.Equal(new UsageRate(RouteExit.Lan, 2_000_000), row.Now);
    }

    [Fact]
    public void A_row_that_moved_no_bytes_in_the_range_and_is_idle_is_left_out()
    {
        Assert.Empty(Build(Snap((Today.AddDays(-20), "app:a.exe", 5, 0, 0)), 7).Rows);
    }

    // ---- sorting ----

    static UsageSnapshot ThreeRows() => Snap(
        (Today, "app:alpha.exe", 100, 5, 0), (Today, "app:bravo.exe", 300, 1, 0), (Today, "app:charlie.exe", 200, 9, 0));

    [Fact]
    public void The_default_sort_is_phone_largest_first()
    {
        Assert.Equal(new[] { "bravo", "charlie", "alpha" }, Build(ThreeRows()).Rows.Select(r => r.Name));
    }

    [Theory]
    [InlineData(UsageSortColumn.Phone, false, new[] { "alpha", "charlie", "bravo" })]
    [InlineData(UsageSortColumn.Lan, true, new[] { "charlie", "alpha", "bravo" })]
    [InlineData(UsageSortColumn.Name, false, new[] { "alpha", "bravo", "charlie" })]
    [InlineData(UsageSortColumn.Name, true, new[] { "charlie", "bravo", "alpha" })]
    public void Rows_sort_by_the_chosen_column(UsageSortColumn column, bool descending, string[] expected)
    {
        Assert.Equal(expected, Build(ThreeRows(), sort: new UsageSort(column, descending)).Rows.Select(r => r.Name));
    }

    [Fact]
    public void Sorting_by_Now_puts_the_busiest_first_and_idle_rows_last()
    {
        var usage = WithRates(ThreeRows(),
            ("app:alpha.exe", new UsageRate(RouteExit.Phone, 10_000)),
            ("app:charlie.exe", new UsageRate(RouteExit.Lan, 50_000)));

        var names = Build(usage, sort: new UsageSort(UsageSortColumn.Now, true)).Rows.Select(r => r.Name);

        Assert.Equal(new[] { "charlie", "alpha", "bravo" }, names);
    }

    [Fact]
    public void Ties_are_broken_by_name()
    {
        var usage = Snap((Today, "app:zed.exe", 5, 0, 0), (Today, "app:abe.exe", 5, 0, 0));

        Assert.Equal(new[] { "abe", "zed" }, Build(usage).Rows.Select(r => r.Name));
    }

    [Fact]
    public void A_header_click_flips_the_same_column_and_starts_a_new_one_largest_first()
    {
        var phone = UsageSort.Default;

        Assert.Equal(new UsageSort(UsageSortColumn.Phone, false), phone.Click(UsageSortColumn.Phone));
        Assert.Equal(new UsageSort(UsageSortColumn.Lan, true), phone.Click(UsageSortColumn.Lan));
        Assert.Equal(new UsageSort(UsageSortColumn.Name, false), phone.Click(UsageSortColumn.Name));
        Assert.Equal(new UsageSort(UsageSortColumn.Name, true), phone.Click(UsageSortColumn.Name).Click(UsageSortColumn.Name));
    }

    // ---- assignment state ----

    [Fact]
    public void Goes_via_follows_the_settings_and_locks_rows_that_cannot_be_assigned()
    {
        var usage = Snap(
            (Today, "youtube", 0, 10, 0), (Today, "app:chrome.exe", 10, 0, 0),
            (Today, UsageAttribution.OtherKey, 1, 0, 0), (Today, UsageAttribution.UnattributedKey, 1, 0, 0));
        var settings = new SmartRoutingSettings().WithItem("youtube", false);

        var rows = Build(usage, settings: settings).Rows.ToDictionary(r => r.Key);

        Assert.Equal((GoesVia.Phone, true), (rows["youtube"].Via, rows["youtube"].CanChange));
        Assert.Equal((GoesVia.Phone, true), (rows["app:chrome.exe"].Via, rows["app:chrome.exe"].CanChange));
        Assert.False(rows[UsageAttribution.OtherKey].CanChange);
        Assert.False(rows[UsageAttribution.UnattributedKey].CanChange);
    }

    [Fact]
    public void Nothing_can_be_assigned_while_smart_routing_is_unavailable()
    {
        var usage = Snap((Today, "youtube", 0, 10, 0));

        Assert.False(Assert.Single(Build(usage, canAssign: false).Rows).CanChange);
    }

    [Fact]
    public void Kept_off_is_a_lower_bound_only_when_a_day_in_the_range_has_a_gap()
    {
        var rows = new Dictionary<string, UsageRow> { ["youtube"] = new(default, new Traffic(0, 10), 10) };
        UsageSnapshot WithGaps(DateOnly day, int gaps) =>
            new(new Dictionary<DateOnly, UsageDay> { [day] = new(rows, 0, gaps) }, new Dictionary<string, UsageRate>());

        Assert.True(Build(WithGaps(Today.AddDays(-1), 1), 3).KeptOffIsLowerBound);
        Assert.False(Build(WithGaps(Today.AddDays(-1), 0), 3).KeptOffIsLowerBound);
        Assert.False(Build(WithGaps(Today.AddDays(-10), 5), 3).KeptOffIsLowerBound); // the gap is outside the range
    }

    // ---- banners ----

    [Fact]
    public void Recording_since_is_set_only_when_fewer_days_were_recorded_than_the_range()
    {
        var usage = Snap((Today.AddDays(-2), "app:a.exe", 1, 0, 0), (Today, "app:a.exe", 1, 0, 0));

        Assert.Equal(Today.AddDays(-2), Build(usage, 7).RecordingSince);
        Assert.Null(Build(usage, 3).RecordingSince); // the range starts exactly on the first recorded day
        Assert.Null(Build(usage, 1).RecordingSince);
        Assert.Null(Build(Snap(), 7).RecordingSince); // nothing recorded at all
    }

    // ---- texts ----

    [Fact]
    public void Now_text()
    {
        Assert.Equal("idle", UsageReport.NowText(null));
        Assert.Equal("↕ 3 MB/s · LAN", UsageReport.NowText(new UsageRate(RouteExit.Lan, 3 * 1024 * 1024)));
        Assert.Equal("↕ 1.4 MB/s · phone", UsageReport.NowText(new UsageRate(RouteExit.Phone, 1_468_006)));
        Assert.Equal("↕ 2 KB/s · phone", UsageReport.NowText(new UsageRate(RouteExit.Phone, 2048)));
    }

    [Fact]
    public void Range_labels()
    {
        Assert.Equal(new[] { "Today", "3 days", "7 days", "15 days", "30 days" }, UsageReport.Ranges.Select(UsageReport.RangeLabel));
    }

    // ---- filter ----

    static UsageSnapshot FilterRows() => Snap(
        500,
        (Today, "youtube", 10, 1000, 1000), (Today, "app:chrome.exe", 20, 2000, 0), (Today, "app:steam.exe", 40, 4000, 4000));

    [Theory]
    [InlineData("YOUTUBE")]
    [InlineData("youtu")]
    [InlineData("Tube")]
    public void A_filter_matches_the_display_name_ignoring_case(string filter)
    {
        var row = Assert.Single(Build(FilterRows(), filter: filter).Rows);

        Assert.Equal("youtube", row.Key);
    }

    [Fact]
    public void A_filter_matches_the_row_key_even_when_the_name_differs()
    {
        // "app:" is only in the key; the display name is just the program name.
        var model = Build(FilterRows(), filter: "APP:chrome");

        Assert.Equal("app:chrome.exe", Assert.Single(model.Rows).Key);
    }

    [Fact]
    public void A_filter_is_trimmed()
    {
        Assert.Equal("youtube", Assert.Single(Build(FilterRows(), filter: "  youtube 	").Rows).Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_filter_is_no_filter(string? filter)
    {
        var model = Build(FilterRows(), filter: filter);

        Assert.Equal(3, model.Rows.Count);
        Assert.False(model.Filtered);
    }

    [Fact]
    public void Filtered_totals_sum_only_the_shown_rows_but_kept_off_and_adapter_stay_whole_period()
    {
        var model = Build(FilterRows(), filter: "app:");

        Assert.True(model.Filtered);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal(60, model.PhoneTotal);
        Assert.Equal(6000, model.LanTotal);
        Assert.Equal(5000, model.KeptOff);
        Assert.Equal(500, model.PhoneAdapterTotal);
    }

    [Fact]
    public void A_filter_that_matches_nothing_gives_no_rows_and_zero_totals_but_is_still_filtered()
    {
        var model = Build(FilterRows(), filter: "zzz");

        Assert.Empty(model.Rows);
        Assert.True(model.Filtered);
        Assert.Equal(0, model.PhoneTotal + model.LanTotal);
        Assert.Equal(5000, model.KeptOff);
    }

    [Fact]
    public void A_row_with_only_a_live_rate_is_filtered_by_the_same_rule()
    {
        var usage = WithRates(FilterRows(), ("app:vlc.exe", new UsageRate(RouteExit.Phone, 1000)));

        Assert.Equal("app:vlc.exe", Assert.Single(Build(usage, filter: "VLC").Rows).Key);
        Assert.DoesNotContain(Build(usage, filter: "steam").Rows, r => r.Key == "app:vlc.exe");
    }

    // ---- row order ----

    [Fact]
    public void Unfrozen_rows_follow_the_sorted_order()
    {
        Assert.Equal(new[] { "c", "a", "b" }, UsageRowOrder.Next(["a", "b", "c"], ["c", "a", "b"], freeze: false));
    }

    [Fact]
    public void Frozen_rows_keep_their_place_new_rows_are_appended_and_vanished_rows_leave()
    {
        var next = UsageRowOrder.Next(["a", "b", "c"], ["d", "c", "a"], freeze: true);

        Assert.Equal(new[] { "a", "c", "d" }, next);
    }

    [Fact]
    public void Freezing_with_nothing_shown_yet_gives_the_sorted_order()
    {
        Assert.Equal(new[] { "x", "y" }, UsageRowOrder.Next([], ["x", "y"], freeze: true));
    }
}
