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
                                  bool canAssign = true, UsageSort? sort = null, string? filter = null,
                                  ConnectionId profile = ConnectionId.Phone) =>
        UsageReport.Build(usage, Today, range, Catalog, settings ?? new SmartRoutingSettings(), canAssign, sort ?? UsageSort.Default, filter, profile);

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

    [Theory]
    [InlineData(ConnectionId.Phone, ConnectionId.Wifi, "→ Wi-Fi")]
    [InlineData(ConnectionId.Lan, ConnectionId.Wifi, "→ Wi-Fi")]
    [InlineData(ConnectionId.Wifi, ConnectionId.Lan, "→ LAN")]
    public void Every_profile_report_uses_its_explicit_destination_without_changing_byte_buckets(ConnectionId profile, ConnectionId exit, string caption)
    {
        var settings = new SmartRoutingSettings().WithUserRule(profile, new UserRule(UserRuleType.App, "editor.exe", Exit: exit));
        var report = Build(Snap((Today, "app:editor.exe", 10, 20, 0)), settings: settings, profile: profile);
        var row = Assert.Single(report.Rows);
        Assert.Equal(profile, report.Profile);
        Assert.Equal(caption, UsageReport.TagText(row.Exception));
        Assert.Equal(10, row.PhoneBytes);
        Assert.Equal(20, row.LanBytes);
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
        var usage = WithRates(Snap(), ("app:steam.exe", new UsageRate(0, 2_000_000)));

        var row = Assert.Single(Build(usage).Rows);

        Assert.Equal("app:steam.exe", row.Key);
        Assert.Equal(0, row.PhoneBytes + row.LanBytes);
        Assert.Equal(new UsageRate(0, 2_000_000), row.Now);
    }

    [Fact]
    public void A_row_that_moved_no_bytes_in_the_range_and_is_idle_is_left_out()
    {
        Assert.Empty(Build(Snap((Today.AddDays(-20), "app:a.exe", 5, 0, 0)), 7).Rows);
    }

    // ---- sorting ----

    static UsageSnapshot ThreeRows() => Snap(
        (Today, "app:alpha.exe", 100, 5, 0), (Today, "app:bravo.exe", 300, 1, 0), (Today, "app:charlie.exe", 200, 9, 0));

    /// Up and down set apart: alpha 50 up / 10 down on the phone, bravo 5 / 400, charlie 20 / 0; LAN: bravo 7 / 3, charlie 0 / 90.
    static UsageSnapshot SplitRows() => new(
        new Dictionary<DateOnly, UsageDay>
        {
            [Today] = new UsageDay(new Dictionary<string, UsageRow>
            {
                ["app:alpha.exe"] = new(new Traffic(50, 10), default, 0),
                ["app:bravo.exe"] = new(new Traffic(5, 400), new Traffic(7, 3), 0),
                ["app:charlie.exe"] = new(new Traffic(20, 0), new Traffic(0, 90), 0),
            }, 0),
        },
        new Dictionary<string, UsageRate>());

    [Fact]
    public void The_default_sort_is_phone_upload_largest_first()
    {
        Assert.Equal(new UsageSort(UsageSortColumn.PhoneUp, true), UsageSort.Default);
        Assert.Equal(new[] { "alpha", "charlie", "bravo" }, Build(SplitRows()).Rows.Select(r => r.Name));
    }

    [Fact]
    public void Rows_and_totals_carry_the_upload_part_and_the_download_is_the_rest()
    {
        var model = Build(SplitRows());
        var bravo = model.Rows.Single(r => r.Name == "bravo");

        Assert.Equal((5, 400, 405), (bravo.PhoneUp, bravo.PhoneDown, bravo.PhoneBytes));
        Assert.Equal((7, 3, 10), (bravo.LanUp, bravo.LanDown, bravo.LanBytes));
        Assert.Equal((75, 410, 485), (model.PhoneUpTotal, model.PhoneTotal - model.PhoneUpTotal, model.PhoneTotal));
        Assert.Equal((7, 93, 100), (model.LanUpTotal, model.LanTotal - model.LanUpTotal, model.LanTotal));
    }

    [Theory]
    [InlineData(UsageSortColumn.PhoneDown, true, new[] { "bravo", "alpha", "charlie" })]
    [InlineData(UsageSortColumn.PhoneUp, false, new[] { "bravo", "charlie", "alpha" })]
    [InlineData(UsageSortColumn.LanUp, true, new[] { "bravo", "alpha", "charlie" })]
    [InlineData(UsageSortColumn.LanDown, true, new[] { "charlie", "bravo", "alpha" })]
    [InlineData(UsageSortColumn.Phone, true, new[] { "bravo", "alpha", "charlie" })]
    public void Rows_sort_by_each_upload_and_download_column(UsageSortColumn column, bool descending, string[] expected)
    {
        Assert.Equal(expected, Build(SplitRows(), sort: new UsageSort(column, descending)).Rows.Select(r => r.Name));
    }

    [Fact]
    public void A_rows_live_speed_is_split_into_upload_and_download()
    {
        var usage = SplitRows() with
        {
            Rates = new Dictionary<string, UsageRate> { ["app:bravo.exe"] = new(9_000, 0) },
            UpRates = new Dictionary<string, UsageUpRate> { ["app:bravo.exe"] = new(2_000, 0) },
        };

        var row = Build(usage).Rows.Single(r => r.Name == "bravo");

        Assert.Equal((2_000, 0), (row.NowPhoneUp, row.NowLanUp));
        Assert.Equal("↑ 2 KB/s ↓ 7 KB/s", UsageReport.NowSplitText(row.Now!.PhoneBytesPerSecond, row.NowPhoneUp));
    }

    [Fact]
    public void The_split_speed_text_is_an_en_dash_when_idle_and_never_shows_a_negative_download()
    {
        Assert.Equal("–", UsageReport.NowSplitText(0, 0));
        Assert.Equal("↑ 3 MB/s ↓ 0 KB/s", UsageReport.NowSplitText(3 * 1024 * 1024, 5 * 1024 * 1024));
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

    static UsageSnapshot RatedRows() => WithRates(ThreeRows(),
        ("app:alpha.exe", new UsageRate(10_000, 0)),
        ("app:charlie.exe", new UsageRate(0, 50_000)),
        ("app:bravo.exe", new UsageRate(30_000, 20_000)));

    [Fact]
    public void Sorting_by_Now_phone_uses_the_phone_rate_and_puts_idle_rows_last()
    {
        var names = Build(RatedRows(), sort: new UsageSort(UsageSortColumn.NowPhone, true)).Rows.Select(r => r.Name);

        Assert.Equal(new[] { "bravo", "alpha", "charlie" }, names);
    }

    [Fact]
    public void Sorting_by_Now_LAN_uses_the_LAN_rate_and_puts_idle_rows_last()
    {
        var names = Build(RatedRows(), sort: new UsageSort(UsageSortColumn.NowLan, true)).Rows.Select(r => r.Name);

        Assert.Equal(new[] { "charlie", "bravo", "alpha" }, names);
    }

    [Fact]
    public void Sorting_a_Now_column_ascending_puts_idle_rows_first_and_ties_go_by_name()
    {
        var usage = WithRates(ThreeRows(), ("app:charlie.exe", new UsageRate(5_000, 0)));

        var names = Build(usage, sort: new UsageSort(UsageSortColumn.NowPhone, false)).Rows.Select(r => r.Name);

        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, names);
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
        var phone = new UsageSort(UsageSortColumn.Phone, true);

        Assert.Equal(new UsageSort(UsageSortColumn.Phone, false), phone.Click(UsageSortColumn.Phone));
        Assert.Equal(new UsageSort(UsageSortColumn.PhoneUp, true), phone.Click(UsageSortColumn.PhoneUp));
        Assert.Equal(new UsageSort(UsageSortColumn.Lan, true), phone.Click(UsageSortColumn.Lan));
        Assert.Equal(new UsageSort(UsageSortColumn.Name, false), phone.Click(UsageSortColumn.Name));
        Assert.Equal(new UsageSort(UsageSortColumn.Name, true), phone.Click(UsageSortColumn.Name).Click(UsageSortColumn.Name));
        Assert.Equal(new UsageSort(UsageSortColumn.NowPhone, true), phone.Click(UsageSortColumn.NowPhone));
        Assert.Equal(new UsageSort(UsageSortColumn.NowLan, true), phone.Click(UsageSortColumn.NowLan));
        Assert.Equal(new UsageSort(UsageSortColumn.NowLan, false), phone.Click(UsageSortColumn.NowLan).Click(UsageSortColumn.NowLan));
    }

    // ---- assignment state ----

    [Fact]
    public void Rows_carry_the_exception_state_of_the_active_profile()
    {
        var usage = Snap(
            (Today, "youtube", 0, 10, 0), (Today, "app:chrome.exe", 10, 0, 0),
            (Today, UsageAttribution.OtherKey, 1, 0, 0), (Today, UsageAttribution.UnattributedKey, 1, 0, 0));
        var settings = new SmartRoutingSettings().WithItem("youtube", false);

        var rows = Build(usage, settings: settings).Rows.ToDictionary(r => r.Key);

        Assert.Equal(new ExceptionState(false, true, ConnectionId.Lan, Home: true) { FixedTarget = true }, rows["youtube"].Exception);     // switched off: not an exception now
        Assert.Equal(new ExceptionState(false, true, ConnectionId.Lan, Home: true), rows["app:chrome.exe"].Exception);
        Assert.False(rows[UsageAttribution.OtherKey].CanChange);
        Assert.False(rows[UsageAttribution.UnattributedKey].CanChange);
    }

    [Fact]
    public void The_profile_selects_which_list_the_rows_are_exceptions_of()
    {
        var usage = Snap((Today, "app:chrome.exe", 10, 0, 0));
        var settings = new SmartRoutingSettings().WithPhoneUserRule(new UserRule(UserRuleType.App, "chrome.exe"));

        Assert.False(Build(usage, settings: settings).Rows.Single().Exception.InException);
        Assert.True(Build(usage, settings: settings, profile: ConnectionId.Lan).Rows.Single().Exception.InException);
        Assert.Equal(ConnectionId.Phone, Build(usage, settings: settings).Profile);
        Assert.Equal(ConnectionId.Lan, Build(usage, settings: settings, profile: ConnectionId.Lan).Profile);
    }

    [Fact]
    public void Tag_and_menu_texts()
    {
        Assert.Equal("→ Home", UsageReport.TagText(new ExceptionState(true, true, ConnectionId.Lan, Home: true)));
        Assert.Equal("→ Phone", UsageReport.TagText(new ExceptionState(true, true, ConnectionId.Phone)));
        Assert.Null(UsageReport.TagText(new ExceptionState(false, true, ConnectionId.Lan, Home: true)));
        Assert.Equal("Exclude from exception", UsageReport.MenuText(new ExceptionState(true, true, ConnectionId.Lan, Home: true)));
        Assert.Equal("Send to exception (→ Home)", UsageReport.MenuText(new ExceptionState(false, true, ConnectionId.Lan, Home: true)));
        Assert.Equal("Send to exception (→ Phone)", UsageReport.MenuText(new ExceptionState(false, true, ConnectionId.Phone)));
        Assert.Equal("Can't be an exception here", UsageReport.MenuText(new ExceptionState(false, false, ConnectionId.Lan, Home: true)));
        Assert.Equal("Can't be an exception here", UsageReport.MenuText(new ExceptionState(true, false, ConnectionId.Lan, Home: true)));
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
        Assert.Equal("–", UsageReport.NowText(0));
        Assert.Equal("↕ 3 MB/s", UsageReport.NowText(3 * 1024 * 1024));
        Assert.Equal("↕ 1.4 MB/s", UsageReport.NowText(1_468_006));
        Assert.Equal("↕ 2 KB/s", UsageReport.NowText(2048));
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
        var usage = WithRates(FilterRows(), ("app:vlc.exe", new UsageRate(1000, 0)));

        Assert.Equal("app:vlc.exe", Assert.Single(Build(usage, filter: "VLC").Rows).Key);
        Assert.DoesNotContain(Build(usage, filter: "steam").Rows, r => r.Key == "app:vlc.exe");
    }

    // ---- exact adapter ledger: remainder row, totals, footer ----

    static LedgerDay Led(Traffic? phone = null, Traffic? lan = null, Traffic? wifi = null, TimeOnly? since = null)
    {
        var totals = new Dictionary<ConnectionId, Traffic>();
        if (phone is { } p) totals[ConnectionId.Phone] = p;
        if (lan is { } l) totals[ConnectionId.Lan] = l;
        if (wifi is { } w) totals[ConnectionId.Wifi] = w;
        return new LedgerDay(totals, since);
    }

    static UsageSnapshot WithLedgers(UsageSnapshot s, params (DateOnly Day, LedgerDay Ledger)[] days) =>
        s.WithLedger(days.ToDictionary(d => d.Day, d => d.Ledger));

    static UsageReportRow? Remainder(UsageReportModel model) =>
        model.Rows.SingleOrDefault(r => r.Key == UsageAttribution.NotCountedKey);

    [Fact]
    public void Remainder_is_ledger_minus_rows_per_exit_and_direction()
    {
        var usage = WithLedgers(Snap((Today, "app:a.exe", 60, 0, 0)), (Today, Led(phone: new Traffic(10, 100))));

        var row = Remainder(Build(usage))!;

        Assert.Equal("Not counted per app", row.Name);
        Assert.Equal((10, 40, 50), (row.PhoneUp, row.PhoneDown, row.PhoneBytes));
        Assert.Equal(0, row.LanBytes);
    }

    [Fact]
    public void Lan_remainder_uses_lan_plus_wifi()
    {
        var usage = WithLedgers(Snap((Today, "app:a.exe", 0, 120, 0)),
            (Today, Led(lan: new Traffic(0, 100), wifi: new Traffic(0, 50))));

        var row = Remainder(Build(usage))!;

        Assert.Equal(30, row.LanBytes);
        Assert.Equal(0, row.PhoneBytes);
    }

    [Fact]
    public void Rows_larger_than_the_ledger_give_no_negative_remainder()
    {
        // Each direction clamps on its own: up has 10 spare, down is over by 5, and the LAN is over too.
        var rows = new Dictionary<string, UsageRow> { ["app:a.exe"] = new(new Traffic(0, 15), new Traffic(0, 7), 0) };
        var usage = new UsageSnapshot(new Dictionary<DateOnly, UsageDay> { [Today] = new(rows, 0) }, new Dictionary<string, UsageRate>());

        var onlyUp = Remainder(Build(WithLedgers(usage, (Today, Led(phone: new Traffic(10, 10), lan: new Traffic(0, 7))))))!;
        Assert.Equal((10, 0, 10), (onlyUp.PhoneUp, onlyUp.PhoneDown, onlyUp.PhoneBytes));
        Assert.Equal(0, onlyUp.LanBytes);

        var model = Build(WithLedgers(usage, (Today, Led(phone: new Traffic(0, 5), lan: new Traffic(0, 3)))));
        Assert.Null(Remainder(model));
        Assert.Equal(15, model.PhoneTotal);
        Assert.Equal(7, model.LanTotal);
    }

    [Fact]
    public void A_day_without_a_ledger_adds_no_remainder_and_does_not_distort_the_next_day()
    {
        var usage = WithLedgers(
            Snap((Today.AddDays(-2), "app:a.exe", 100, 0, 0), (Today.AddDays(-1), "app:a.exe", 50, 0, 0), (Today, "app:a.exe", 40, 0, 0)),
            (Today.AddDays(-1), Led(phone: new Traffic(0, 10))),   // rows exceed it: this day's remainder is 0, not -40
            (Today, Led(phone: new Traffic(0, 100))));             // 100 - 40 = 60, whatever the earlier days did

        var model = Build(usage, 3);

        Assert.Equal(60, Remainder(model)!.PhoneBytes);
        Assert.Equal(190 + 60, model.PhoneTotal);
    }

    [Fact]
    public void The_remainder_row_is_subject_to_the_name_filter()
    {
        var usage = WithLedgers(Snap((Today, "app:chrome.exe", 10, 0, 0)), (Today, Led(phone: new Traffic(0, 100))));

        Assert.Null(Remainder(Build(usage, filter: "chrome")));
        Assert.NotNull(Remainder(Build(usage, filter: "counted")));
        Assert.NotNull(Remainder(Build(usage, filter: "Not COUNTED per")));
        var all = Build(usage);
        Assert.Equal(2, all.Rows.Count);
        var chrome = Build(usage, filter: "chrome");
        Assert.Equal(10, chrome.PhoneTotal); // hidden remainder is not in a filtered total
    }

    [Fact]
    public void The_remainder_row_cannot_become_an_exception()
    {
        var usage = WithLedgers(Snap((Today, "app:a.exe", 1, 0, 0)), (Today, Led(phone: new Traffic(0, 100))));

        var row = Remainder(Build(usage, canAssign: true))!;

        Assert.False(row.Exception.CanChange);
        Assert.False(row.CanChange);
        Assert.Null(row.Now);
        Assert.False(UsageExceptions.Describe(Catalog, new SmartRoutingSettings(), ConnectionId.Phone, UsageAttribution.NotCountedKey).CanChange);
        Assert.Empty(UsageExceptions.Actions(row.Key, ConnectionId.Phone, row.Exception));
    }

    [Fact]
    public void The_totals_include_the_remainder()
    {
        var usage = WithLedgers(Snap((Today, "app:a.exe", 60, 20, 0)),
            (Today, Led(phone: new Traffic(10, 100), lan: new Traffic(5, 45))));

        var model = Build(usage);

        Assert.Equal(60 + 50, model.PhoneTotal);
        Assert.Equal(10, model.PhoneUpTotal);
        Assert.Equal(20 + 30, model.LanTotal);
        Assert.Equal(5, model.LanUpTotal);
    }

    [Fact]
    public void LedgerTotals_cover_only_days_with_a_ledger_in_the_range()
    {
        var usage = WithLedgers(
            Snap(adapterPerDay: 0, (Today.AddDays(-2), "app:a.exe", 500, 0, 0), (Today.AddDays(-1), "app:a.exe", 7, 0, 0)),
            (Today.AddDays(-1), Led(phone: new Traffic(1, 2), lan: new Traffic(3, 4))),
            (Today, Led(phone: new Traffic(10, 20), wifi: new Traffic(0, 9))),
            (Today.AddDays(-20), Led(phone: new Traffic(1000, 1000)))); // outside the range

        var totals = Build(usage, 3).LedgerTotals;

        Assert.Equal(new Traffic(11, 22), totals[ConnectionId.Phone]);
        Assert.Equal(new Traffic(3, 4), totals[ConnectionId.Lan]);
        Assert.Equal(new Traffic(0, 9), totals.GetValueOrDefault(ConnectionId.Wifi));
        Assert.Empty(Build(Snap((Today, "app:a.exe", 5, 0, 0)), 3).LedgerTotals); // no ledger anywhere: nothing summed
    }

    [Fact]
    public void LedgerSince_is_set_only_when_the_first_ledger_day_is_in_the_range()
    {
        var since = new TimeOnly(7, 5);
        var first = Today.AddDays(-3);
        var usage = WithLedgers(Snap(), (first, Led(phone: new Traffic(0, 1), since: since)), (Today, Led(since: new TimeOnly(0, 1))));

        Assert.Equal(first.ToDateTime(since), Build(usage, 7).LedgerSince);
        Assert.Null(Build(usage, 3).LedgerSince); // the first ledger day is before this range, later days do not count
        Assert.Null(Build(WithLedgers(Snap(), (first, Led(phone: new Traffic(0, 1)))), 7).LedgerSince); // after a Clear: no Since
        Assert.Null(Build(Snap(), 7).LedgerSince);
    }

    [Fact]
    public void The_footer_lists_exact_totals_and_since_when_known()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            var day = Today.AddDays(-3);
            var ledger = Led(phone: new Traffic(0, 98L * 1024 * 1024), lan: new Traffic(0, (long)(3.2 * 1024 * 1024 * 1024)), since: new TimeOnly(7, 5));

            var withSince = UsageReport.AdapterFooter(Build(WithLedgers(Snap(), (day, ledger)), 7));
            var withoutSince = UsageReport.AdapterFooter(Build(WithLedgers(Snap(), (Today, ledger with { Since = null })), 7));

            Assert.Equal("Exact adapter totals in this period (from Windows): Phone 98 MB · LAN 3.2 GB · Wi-Fi 0 B (counted since 07:05 on 7 Oct)", withSince);
            Assert.Equal("Exact adapter totals in this period (from Windows): Phone 98 MB · LAN 3.2 GB · Wi-Fi 0 B", withoutSince);
            Assert.Equal(
                "Adapter totals are exact while the widget runs. Per-app rows are counted only while Smart routing runs or this tab is open. The Now columns are per app and can differ from the card's speed.",
                UsageReport.CountingNote);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void The_footer_falls_back_to_the_phone_adapter_text_without_a_ledger()
    {
        Assert.Equal("Phone adapter total in this period (exact, from Windows): 1 MB",
            UsageReport.AdapterFooter(Build(Snap(1024 * 1024, (Today, "app:a.exe", 1, 0, 0)))));
        Assert.Equal("Phone adapter total in this period (exact, from Windows): not measured yet",
            UsageReport.AdapterFooter(Build(Snap((Today, "app:a.exe", 1, 0, 0)))));
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
