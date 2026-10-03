namespace NetRoute.Core.Tests;

public class SmartRoutingPresenterTests
{
    static readonly DailyStats NoStats = new(new DateOnly(2026, 10, 2), new Dictionary<string, long>());

    static SmartRoutingStatus Status(SmartState state = SmartState.Running, string? message = null, bool lanRulesOnPhone = false,
                                     IReadOnlyList<string>? waiting = null, int rules = 4, DailyStats? today = null) =>
        new(state, message, RouteExit.Phone, lanRulesOnPhone, true, waiting ?? [], rules, today ?? NoStats);

    [Theory]
    [InlineData(0, "0 KB")]
    [InlineData(1536, "2 KB")]
    [InlineData(5 * 1024 * 1024 + 300_000, "5.3 MB")]
    [InlineData(1_288_490_189, "1.2 GB")]
    [InlineData(512, "1 KB")]
    [InlineData(2560, "3 KB")]
    [InlineData(1048575, "1 MB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741823, "1 GB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(-5, "0 KB")]
    public void Bytes_are_human_readable(long bytes, string expected) => Assert.Equal(expected, ByteFormat.Human(bytes));

    [Fact]
    public void Card_row_texts_per_state()
    {
        var stats = new DailyStats(NoStats.Day, new Dictionary<string, long> { ["youtube"] = 1_288_490_189 });

        Assert.Equal(new SmartRow("⚡ Smart routing ON · Phone + exceptions · 4 rules · 1.2 GB kept off 4G today", SmartTone.Normal),
            SmartRoutingPresenter.Row(Status(today: stats)));
        Assert.Equal(new SmartRow("⚡ Smart routing off", SmartTone.Muted), SmartRoutingPresenter.Row(Status(SmartState.Off)));
        Assert.Equal(new SmartRow("⚡ Smart routing starting…", SmartTone.Muted), SmartRoutingPresenter.Row(Status(SmartState.Starting)));
        Assert.Equal(new SmartRow("⚡ Smart routing paused — Needs both phone and LAN connected", SmartTone.Muted),
            SmartRoutingPresenter.Row(Status(SmartState.Unavailable, "Needs both phone and LAN connected")));
        Assert.Equal(new SmartRow("⚠ Smart routing stopped — sing-box keeps crashing (see log)", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(SmartState.Faulted, "Smart routing stopped — sing-box keeps crashing (see log)")));
        Assert.Equal(new SmartRow("⏸ LAN-only traffic waiting (LAN offline)", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(waiting: ["YouTube"])));
        Assert.Equal(new SmartRow("⚠ Using phone for all traffic — LAN offline", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(lanRulesOnPhone: true)));
    }

    [Fact]
    public void The_lan_profile_row_names_the_profile_and_omits_the_kept_figure()
    {
        Assert.Equal(new SmartRow("⚡ Smart routing ON · LAN + exceptions · 9 rules", SmartTone.Normal), SmartRoutingPresenter.Row(LanProfileStatus(9)));
    }

    static SmartRoutingStatus LanProfileStatus(int rules) =>
        new(SmartState.Running, null, RouteExit.Lan, false, true, [], rules, new DailyStats(new DateOnly(2026, 10, 3), new Dictionary<string, long>()), Profile: RouteExit.Lan);

    [Fact]
    public void Faulted_row_appends_the_detail_when_there_is_one()
    {
        const string message = "Smart routing stopped — sing-box keeps crashing (see log)";

        Assert.Equal(new SmartRow($"⚠ {message} — FATAL bind: access denied", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(SmartState.Faulted, message) with { Detail = "FATAL bind: access denied" }));
        Assert.Equal(new SmartRow($"⚠ {message}", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(SmartState.Faulted, message) with { Detail = "" }));
        Assert.Equal(new SmartRow($"⚠ {message}", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(SmartState.Faulted, message) with { Detail = null }));
    }

    [Fact]
    public void A_paused_message_reads_as_a_sentence_not_a_repeat()
    {
        Assert.Equal(new SmartRow("⚡ Smart routing paused in Auto mode", SmartTone.Muted),
            SmartRoutingPresenter.Row(Status(SmartState.Unavailable, "Paused in Auto mode")));
        Assert.Equal(new SmartRow("⚡ Smart routing paused — Needs administrator rights", SmartTone.Muted),
            SmartRoutingPresenter.Row(Status(SmartState.Unavailable, "Needs administrator rights")));
    }

    [Fact]
    public void Off_with_a_message_shows_a_warning_row()
    {
        Assert.Equal(new SmartRow("⚠ sing-box could not be stopped (see log)", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(SmartState.Off, "sing-box could not be stopped (see log)")));
        Assert.Equal(new SmartRow("⚡ Smart routing off", SmartTone.Muted), SmartRoutingPresenter.Row(Status(SmartState.Off, "")));
    }

    [Fact]
    public void Waiting_text_names_the_items()
    {
        Assert.Equal("YouTube is waiting — the LAN is offline.", SmartRoutingPresenter.WaitingText(["YouTube"]));
        Assert.Equal("YouTube, Windows Update are waiting — the LAN is offline.",
            SmartRoutingPresenter.WaitingText(["YouTube", "Windows Update"]));
    }

    static readonly IReadOnlyList<RuleItem> Catalog =
    [
        new("youtube", "video", "Video & social", "YouTube", [], ["youtube.com"], true),
        new("facebook", "video", "Video & social", "Facebook", [], ["facebook.com"], true),
        new("steam", "games", "Game launchers", "Steam", ["steam.exe"], [], true),
    ];

    [Fact]
    public void Page_groups_items_with_states_and_today_bytes()
    {
        var settings = new SmartRoutingSettings { Enabled = true }
            .WithItem("facebook", false)
            .WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"));
        var stats = new DailyStats(NoStats.Day, new Dictionary<string, long> { ["youtube"] = 300, ["user:website:dropbox.com"] = 7 });

        var page = SmartRoutingPage.Build(Catalog, settings, Status(today: stats));

        Assert.True(page.Enabled);
        Assert.Equal(307, page.TotalToday);
        var video = page.Groups[0];
        Assert.Equal(("video", "Video & social", (bool?)null, 300L), (video.Id, video.Name, video.On, video.TodayBytes));
        Assert.Equal(new[] { true, false }, video.Items.Select(i => i.On));
        Assert.True(page.Groups[1].On);
        Assert.Equal(7, Assert.Single(page.UserRules).TodayBytes);
        Assert.False(page.CanUsePhone);
    }

    [Fact]
    public void Page_shows_user_app_rule_bytes_under_the_usage_row_key_and_website_rules_under_theirs()
    {
        var settings = new SmartRoutingSettings { Enabled = true }
            .WithUserRule(new UserRule(UserRuleType.App, "qBittorrent.exe"))
            .WithUserRule(new UserRule(UserRuleType.Website, "dropbox.com"));
        var stats = new DailyStats(NoStats.Day, new Dictionary<string, long>
        {
            ["app:qbittorrent.exe"] = 700, ["user:website:dropbox.com"] = 7, ["user:app:qbittorrent.exe"] = 99999,
        });

        var page = SmartRoutingPage.Build(Catalog, settings, Status(today: stats));

        Assert.Equal(new[] { 700L, 7L }, page.UserRules.Select(r => r.TodayBytes));
    }

    [Fact]
    public void Can_use_phone_only_while_running_with_lan_offline()
    {
        var offline = Status() with { LanOnline = false };

        Assert.True(SmartRoutingPage.Build(Catalog, new() { Enabled = true }, offline).CanUsePhone);
        Assert.False(SmartRoutingPage.Build(Catalog, new() { Enabled = true }, offline with { LanRulesOnPhone = true }).CanUsePhone);
    }

    [Fact]
    public void Group_toggle_sets_every_item_in_the_group()
    {
        var settings = SmartRoutingPage.WithGroup(Catalog, new SmartRoutingSettings(), "video", false);

        Assert.False(settings.IsItemOn(Catalog[0]));
        Assert.False(settings.IsItemOn(Catalog[1]));
        Assert.True(settings.IsItemOn(Catalog[2]));
    }

    [Theory]
    [InlineData(true, false)]   // fully on: switch the group off
    [InlineData(false, true)]   // fully off: switch it on
    [InlineData(null, true)]    // mixed: switch it ON, never off
    public void Clicking_a_group_turns_it_on_unless_it_is_fully_on(bool? current, bool expected) =>
        Assert.Equal(expected, SmartRoutingPage.NextGroupState(current));

    static readonly IReadOnlyList<RuleItem> V4Catalog =
    [
        new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true),
        new("claude", "ai", "AI", "Claude", ["claude.exe"], [], true, RouteExit.Phone),
        new("vscode-updates", "upd", "Updates", "VS Code updates", [], ["update.code.visualstudio.com"], true, RouteExit.Lan, CarveOut: true),
    ];

    static SmartRoutingStatus StatusFor(RouteExit profile, Dictionary<string, long>? kept = null) =>
        new(SmartState.Running, null, profile, false, true, [], 3, new DailyStats(new DateOnly(2026, 10, 3), kept ?? new()), Profile: profile);

    [Fact]
    public void The_phone_view_lists_the_lan_items_and_carve_outs_with_the_lan_rules()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "a.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "b.exe")],
        };

        var page = SmartRoutingPage.Build(V4Catalog, settings, StatusFor(RouteExit.Phone), RouteExit.Phone);

        Assert.Equal(new[] { "youtube", "vscode-updates" }, page.Groups.SelectMany(g => g.Items).Select(i => i.Id));
        Assert.Equal(new[] { "a.exe" }, page.UserRules.Select(r => r.Rule.Value));
        Assert.Equal(RouteExit.Phone, page.EditingProfile);
        Assert.Equal(RouteExit.Phone, page.ActiveProfile);
    }

    [Fact]
    public void The_lan_view_lists_the_phone_items_and_carve_outs_with_the_phone_rules()
    {
        var settings = new SmartRoutingSettings
        {
            UserRules = [new UserRule(UserRuleType.App, "a.exe")],
            PhoneUserRules = [new UserRule(UserRuleType.App, "b.exe")],
        };

        var page = SmartRoutingPage.Build(V4Catalog, settings, StatusFor(RouteExit.Phone), RouteExit.Lan);

        Assert.Equal(new[] { "claude", "vscode-updates" }, page.Groups.SelectMany(g => g.Items).Select(i => i.Id));
        Assert.Equal(new[] { "b.exe" }, page.UserRules.Select(r => r.Rule.Value));
        Assert.Equal(RouteExit.Lan, page.EditingProfile);
        Assert.Equal(RouteExit.Phone, page.ActiveProfile); // editing the other list does not change the active profile
    }

    [Fact]
    public void A_phone_rule_shows_its_usage_row_bytes()
    {
        var settings = new SmartRoutingSettings { PhoneUserRules = [new UserRule(UserRuleType.App, "B.exe")] };

        var page = SmartRoutingPage.Build(V4Catalog, settings, StatusFor(RouteExit.Lan, new() { ["app:b.exe"] = 700 }), RouteExit.Lan);

        Assert.Equal(700, Assert.Single(page.UserRules).TodayBytes);
    }

    [Fact]
    public void Group_toggles_work_on_the_carve_out_group_from_either_view()
    {
        var s = SmartRoutingPage.WithGroup(V4Catalog, new SmartRoutingSettings(), "upd", on: false);

        Assert.False(s.IsItemOn(V4Catalog.Single(i => i.Id == "vscode-updates")));
    }

    [Theory]
    [InlineData(RouteExit.Phone, RouteExit.Lan, false, true, "→ via phone")]  // Phone view, LAN item switched off
    [InlineData(RouteExit.Lan, RouteExit.Phone, false, true, "→ via LAN")]    // LAN view, phone item switched off
    [InlineData(RouteExit.Lan, RouteExit.Phone, false, false, "→ phone")]     // LAN view, phone item on
    [InlineData(RouteExit.Lan, RouteExit.Lan, true, false, "→ LAN")]          // LAN view, carve-out on
    [InlineData(RouteExit.Lan, RouteExit.Lan, true, true, "→ via phone")]     // LAN view, carve-out off: its app's phone rule catches it
    public void Item_captions_say_where_the_item_goes(RouteExit editing, RouteExit exit, bool carveOut, bool off, string expected)
    {
        var item = new PageItem("x", "X", On: !off, TodayBytes: 0, exit, carveOut);

        Assert.Equal(expected, SmartRoutingPage.ItemCaption(item, editing));
    }

    [Fact]
    public void A_phone_view_item_that_is_on_shows_its_bytes()
    {
        var item = new PageItem("youtube", "YouTube", On: true, TodayBytes: 2048);

        Assert.Equal("2 KB", SmartRoutingPage.ItemCaption(item, RouteExit.Phone));
    }
}
