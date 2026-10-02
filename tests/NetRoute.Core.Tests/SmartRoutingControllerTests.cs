using Microsoft.Extensions.Time.Testing;

namespace NetRoute.Core.Tests;

public class SmartRoutingControllerTests
{
    // One domain-only item: its rule is route.rules index 3 (0 sniff, 1 hijack-dns, 2 private).
    static readonly IReadOnlyList<RuleItem> Catalog = [new("youtube", "video", "Video", "YouTube", [], ["youtube.com"], true)];
    const string MatchYouTube = "+0300 2026-10-02 16:50:47 DEBUG [42 1ms] router: match[3] domain_suffix=youtube.com => route(lan-only)";
    const string FailLanOnly = "+0300 2026-10-02 16:50:52 ERROR [42 5.0s] connection: open connection to 1.2.3.4:443 using outbound/selector[lan-only]: dial tcp 1.2.3.4:443: i/o timeout";

    readonly FakeSingBoxHost _host = new();
    readonly FakeSingBoxApi _api = new();
    readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
    readonly List<IReadOnlyList<string>> _popups = new();
    readonly List<string> _notes = new();

    SmartRoutingController Create()
    {
        var c = new SmartRoutingController(Catalog, _host, (_, _) => _api, () => 40000, null, _ => { }, _time);
        c.WaitingDetected += _popups.Add;
        c.Notify += _notes.Add;
        return c;
    }

    static AppSettings On(bool enabled = true) => new() { SmartRouting = new SmartRoutingSettings { Enabled = enabled } };

    static NetworkStatus Net(RoutingMode mode = RoutingMode.Phone, int phoneIndex = 31, bool lan = true, int? lanMs = 12,
                             bool healing = false, bool canModify = true) =>
        new(mode,
            new DetectionResult(TestAdapters.Phone(phoneIndex), DetectionIssue.None,
                lan ? TestAdapters.Lan() with { DnsServer = "192.168.86.1" } : null, lan ? DetectionIssue.None : DetectionIssue.NotFound),
            InternetPath.Phone, InternetPath.None, 30, lan ? lanMs : null, canModify, null, IsHealing: healing);

    [Fact]
    public async Task Disabled_does_not_start()
    {
        var c = Create();

        await c.ApplyAsync(Net(), On(false));

        Assert.Empty(_host.Starts);
        Assert.Equal(SmartState.Off, c.Status.State);
    }

    [Fact]
    public async Task Starts_in_phone_mode_with_a_config_bound_to_both_adapters()
    {
        var c = Create();

        await c.ApplyAsync(Net(), On());

        var json = Assert.Single(_host.Starts);
        Assert.Contains("\"Ethernet 31\"", json);
        Assert.Contains("\"Ethernet\"", json);
        Assert.Contains("127.0.0.1:40000", json);
        Assert.Equal((SmartState.Running, 1, RouteExit.Phone), (c.Status.State, c.Status.RuleCount, c.Status.DefaultExit));
    }

    [Theory]
    [InlineData(RoutingMode.Lan, true, true, "Not needed in LAN mode")]
    [InlineData(RoutingMode.Phone, false, true, "Needs administrator rights")]
    [InlineData(RoutingMode.Phone, true, false, "Needs both phone and LAN connected")]
    public async Task Is_unavailable_and_stopped_when_not_applicable(RoutingMode mode, bool canModify, bool lan, string message)
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(mode, lan: lan, canModify: canModify), On());

        Assert.Equal(1, _host.Stops);
        Assert.Equal((SmartState.Unavailable, message), (c.Status.State, c.Status.Message));
    }

    [Fact]
    public async Task Same_inputs_do_not_restart_and_auto_mode_uses_phone()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(RoutingMode.Auto), On());

        Assert.Single(_host.Starts);
        Assert.Equal(RouteExit.Phone, c.Status.DefaultExit);
    }

    [Fact]
    public async Task Heal_switches_the_default_selector_without_restart()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(healing: true), On());

        Assert.Single(_host.Starts);
        Assert.Contains(("default", "lan"), _api.Selects);
        Assert.Equal(RouteExit.Lan, c.Status.DefaultExit);
    }

    [Fact]
    public async Task Adapter_rename_restarts_with_new_config()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(phoneIndex: 36), On()); // replugged phone: "Ethernet 36"

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(1, _host.Stops);
        Assert.Contains("\"Ethernet 36\"", _host.Starts[1]);
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Rule_change_restarts()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(), On() with { SmartRouting = On().SmartRouting.WithItem("youtube", false) });

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(0, c.Status.RuleCount);
    }

    [Fact]
    public async Task Unexpected_exit_restarts_then_faults_after_three_in_five_minutes()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        for (var i = 0; i < 3; i++)
        {
            _host.Crash();
            await c.HandleExitAsync(1);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(3, _host.Starts.Count); // initial + 2 restarts; the 3rd crash faults instead
        Assert.Equal(SmartState.Faulted, c.Status.State);
        Assert.Equal("Smart routing stopped — sing-box keeps crashing (see log)", c.Status.Message);
        Assert.Contains("Smart routing stopped — sing-box keeps crashing (see log)", _notes);

        await c.ApplyAsync(Net(), On(false));
        await c.ApplyAsync(Net(), On()); // user switches it off and on again
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Crashes_spread_beyond_the_window_do_not_fault()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        for (var i = 0; i < 3; i++)
        {
            _host.Crash();
            await c.HandleExitAsync(1);
            _time.Advance(TimeSpan.FromMinutes(3));
        }

        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Start_failure_counts_as_crash_and_faults_after_three()
    {
        var c = Create();
        _host.StartThrows = new FileNotFoundException("sing-box.exe not found");

        await c.ApplyAsync(Net(), On());
        Assert.NotEqual(SmartState.Running, c.Status.State);
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(), On());

        Assert.Equal(SmartState.Faulted, c.Status.State);
    }

    [Fact]
    public async Task Waiting_popup_is_raised_once_per_lan_outage()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On()); // LAN offline

        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);
        await c.ProcessLineAsync(MatchYouTube.Replace("[42", "[43"));
        await c.ProcessLineAsync(FailLanOnly.Replace("[42", "[43"));

        Assert.Equal(new[] { "YouTube" }, Assert.Single(_popups));
        Assert.Equal(new[] { "YouTube" }, c.Status.WaitingNames);
    }

    [Fact]
    public async Task Failures_while_lan_is_online_do_not_count_as_waiting()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);

        Assert.Empty(_popups);
        Assert.Empty(c.Status.WaitingNames);
    }

    [Fact]
    public async Task Keep_waiting_suppresses_until_the_lan_returns()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        c.KeepWaiting();
        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);
        Assert.Empty(_popups);

        for (var i = 0; i < 3; i++) await c.ApplyAsync(Net(), On()); // LAN healthy x3 -> outage over
        await c.ApplyAsync(Net(lanMs: null), On());                    // new outage
        await c.ProcessLineAsync(MatchYouTube.Replace("[42", "[44"));
        await c.ProcessLineAsync(FailLanOnly.Replace("[42", "[44"));

        Assert.Single(_popups);
    }

    [Fact]
    public async Task Use_phone_until_lan_back_switches_selector_and_reverts_after_three_healthy_checks()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());

        await c.UseLanRulesOnPhoneAsync();

        Assert.Contains(("lan-only", "phone"), _api.Selects);
        Assert.True(c.Status.LanRulesOnPhone);
        Assert.Empty(c.Status.WaitingNames);

        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(), On());
        Assert.True(c.Status.LanRulesOnPhone);
        await c.ApplyAsync(Net(), On());

        Assert.False(c.Status.LanRulesOnPhone);
        Assert.Equal(("lan-only", "lan"), _api.Selects.Last());
        Assert.Contains("LAN back — LAN-only traffic is back on the LAN", _notes);
    }

    [Fact]
    public async Task Stats_poll_counts_lan_only_bytes_while_phone_is_default()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.Connections.Add(new SingBoxConnection("1", "youtube.com", "chrome.exe", ["lan", "lan-only"], 10, 990));

        await c.PollStatsAsync();

        Assert.Equal(1000, c.Status.Today.Total);
    }

    [Fact]
    public async Task Stop_is_not_counted_as_a_crash()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.StopAsync();
        await c.ApplyAsync(Net(), On());

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task Host_events_are_wired_to_the_controller()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());

        _host.RaiseLine(MatchYouTube);
        _host.RaiseLine(FailLanOnly);

        Assert.True(SpinWait.SpinUntil(() => _popups.Count == 1, TimeSpan.FromSeconds(5)));
    }
}
