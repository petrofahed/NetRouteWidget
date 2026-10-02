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

        Assert.Equal(new SmartRow("⚡ Smart routing ON · 4 rules · 1.2 GB kept off 4G today", SmartTone.Normal),
            SmartRoutingPresenter.Row(Status(today: stats)));
        Assert.Equal(new SmartRow("⚡ Smart routing off", SmartTone.Muted), SmartRoutingPresenter.Row(Status(SmartState.Off)));
        Assert.Equal(new SmartRow("⚡ Smart routing starting…", SmartTone.Muted), SmartRoutingPresenter.Row(Status(SmartState.Starting)));
        Assert.Equal(new SmartRow("⚡ Smart routing paused — Not needed in LAN mode", SmartTone.Muted),
            SmartRoutingPresenter.Row(Status(SmartState.Unavailable, "Not needed in LAN mode")));
        Assert.Equal(new SmartRow("⚠ Smart routing stopped — sing-box keeps crashing (see log)", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(SmartState.Faulted, "Smart routing stopped — sing-box keeps crashing (see log)")));
        Assert.Equal(new SmartRow("⏸ LAN-only traffic waiting (LAN offline)", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(waiting: ["YouTube"])));
        Assert.Equal(new SmartRow("⚠ Using phone for all traffic — LAN offline", SmartTone.Warning),
            SmartRoutingPresenter.Row(Status(lanRulesOnPhone: true)));
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
}
