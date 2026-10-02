using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public class SingBoxLogParserTests
{
    const string Esc = "\u001b";
    static readonly string Match = $"+0300 2026-10-02 16:50:47 {Esc}[37mDEBUG{Esc}[0m [{Esc}[38;5;151m2975681671{Esc}[0m 1ms] router: match[1] domain_suffix=example.com => route(lan-only)";
    static readonly string Sniff = $"+0300 2026-10-02 16:50:47 {Esc}[37mDEBUG{Esc}[0m [{Esc}[38;5;151m2975681671{Esc}[0m 0ms] router: match[0] => sniff";
    static readonly string FailSelector = $"+0300 2026-10-02 16:50:47 {Esc}[31mERROR{Esc}[0m [{Esc}[38;5;151m2975681671{Esc}[0m 150ms] connection: open connection to example.com:443 using outbound/selector[lan-only]: (dial tcp 172.66.147.243:443: route ip+net : no such network interface)";
    const string FailDirectPlain = "+0300 2026-10-02 16:47:37 ERROR [3855044730 5.0s] connection: open connection to 142.251.163.119:443 using outbound/direct[lan]: dial tcp 142.251.163.119:443: i/o timeout";
    const string Info = "+0300 2026-10-02 16:12:35 INFO [220090278 3ms] outbound/direct[phone]: outbound connection to 104.18.20.213:80";

    [Fact]
    public void Parses_rule_match_through_ansi_codes()
    {
        Assert.Equal(new RuleMatched("2975681671", 1, "lan-only"), SingBoxLogParser.Parse(Match));
    }

    [Fact]
    public void Parses_dial_failures_for_selector_and_direct_outbounds()
    {
        Assert.Equal(new DialFailed("2975681671", "lan-only"), SingBoxLogParser.Parse(FailSelector));
        Assert.Equal(new DialFailed("3855044730", "lan"), SingBoxLogParser.Parse(FailDirectPlain));
    }

    [Theory]
    [InlineData("")]
    [InlineData("random text")]
    public void Ignores_unrelated_lines(string line)
    {
        Assert.Null(SingBoxLogParser.Parse(line));
        Assert.Null(SingBoxLogParser.Parse(Sniff));
        Assert.Null(SingBoxLogParser.Parse(Info));
    }

    [Fact]
    public void Tracker_reports_the_entry_of_a_failed_lan_only_connection()
    {
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [1] = "youtube" });

        Assert.Null(tracker.Observe(SingBoxLogParser.Parse(Match)!));
        Assert.Equal("youtube", tracker.Observe(SingBoxLogParser.Parse(FailSelector)!));
        Assert.Equal(new[] { "youtube" }, tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Tracker_ignores_failures_of_connections_not_routed_lan_only()
    {
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [1] = "youtube" });

        tracker.Observe(new RuleMatched("9", 1, "default"));   // matched an index but not routed lan-only
        Assert.Null(tracker.Observe(new DialFailed("9", "phone")));
        Assert.Null(tracker.Observe(new DialFailed("unknown", "lan")));
        Assert.Empty(tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Tracker_maps_both_rule_indexes_to_one_entry()
    {
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [4] = "onedrive", [5] = "onedrive" });

        tracker.Observe(new RuleMatched("a", 4, "lan-only"));
        tracker.Observe(new RuleMatched("b", 5, "lan-only"));
        tracker.Observe(new DialFailed("a", "lan-only"));
        tracker.Observe(new DialFailed("b", "lan-only"));

        Assert.Equal(new[] { "onedrive" }, tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Old_failures_fall_out_of_the_window_and_clear_empties()
    {
        var time = new FakeTimeProvider();
        var tracker = new LanWaitTracker(new Dictionary<int, string> { [1] = "youtube" }, time);
        tracker.Observe(new RuleMatched("1", 1, "lan-only"));
        tracker.Observe(new DialFailed("1", "lan-only"));

        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty(tracker.RecentFailures(TimeSpan.FromSeconds(30)));

        tracker.Observe(new RuleMatched("2", 1, "lan-only"));
        tracker.Observe(new DialFailed("2", "lan-only"));
        tracker.Clear();
        Assert.Empty(tracker.RecentFailures(TimeSpan.FromSeconds(30)));
    }
}
