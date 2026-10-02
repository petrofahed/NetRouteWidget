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
    readonly List<string> _logs = new();

    SmartRoutingController Create(Func<int>? freePort = null)
    {
        var c = new SmartRoutingController(Catalog, _host, (_, _) => _api, freePort ?? (() => 40000), null, _logs.Add, _time);
        c.WaitingDetected += _popups.Add;
        c.Notify += _notes.Add;
        return c;
    }

    static AppSettings On(bool enabled = true) => new() { SmartRouting = new SmartRoutingSettings { Enabled = enabled } };

    static NetworkStatus Net(RoutingMode mode = RoutingMode.Phone, int phoneIndex = 31, bool lan = true, int? lanMs = 12,
                             bool healing = false, bool canModify = true, bool phone = true, string lanName = "Ethernet", string? lanDns = "192.168.86.1") =>
        new(mode,
            new DetectionResult(phone ? TestAdapters.Phone(phoneIndex) : null, phone ? DetectionIssue.None : DetectionIssue.NotFound,
                lan ? TestAdapters.Lan() with { Name = lanName, DnsServer = lanDns } : null, lan ? DetectionIssue.None : DetectionIssue.NotFound),
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
    [InlineData(RoutingMode.Lan, true, "Not needed in LAN mode")]
    [InlineData(RoutingMode.Phone, false, "Needs administrator rights")]
    public async Task Is_unavailable_and_stopped_when_not_applicable(RoutingMode mode, bool canModify, string message)
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(mode, canModify: canModify), On());

        Assert.Equal(1, _host.Stops);
        Assert.Equal((SmartState.Unavailable, message), (c.Status.State, c.Status.Message));
    }

    [Fact]
    public async Task Phone_lost_stops_sing_box()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(phone: false), On());

        Assert.Equal(1, _host.Stops);
        Assert.Equal((SmartState.Unavailable, "Needs both phone and LAN connected"), (c.Status.State, c.Status.Message));
    }

    [Fact]
    public async Task A_lan_that_was_never_seen_is_unavailable()
    {
        var c = Create();

        await c.ApplyAsync(Net(lan: false), On());

        Assert.Empty(_host.Starts);
        Assert.Equal((SmartState.Unavailable, "Needs both phone and LAN connected"), (c.Status.State, c.Status.Message));
    }

    [Fact]
    public async Task Lan_lost_while_running_keeps_sing_box_running_and_marks_lan_offline()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(lan: false), On());

        Assert.Single(_host.Starts);
        Assert.Equal(0, _host.Stops);
        Assert.Equal(SmartState.Running, c.Status.State);
        Assert.False(c.Status.LanOnline);
    }

    [Fact]
    public async Task Lan_returning_with_same_name_does_not_restart()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lan: false), On());

        await c.ApplyAsync(Net(), On());

        Assert.Single(_host.Starts);
        Assert.Equal(0, _host.Stops);
    }

    [Fact]
    public async Task Lan_returning_renamed_restarts_once()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lan: false), On());

        await c.ApplyAsync(Net(lanName: "Ethernet 2"), On());
        await c.ApplyAsync(Net(lanName: "Ethernet 2"), On());

        Assert.Equal(2, _host.Starts.Count);
        Assert.Contains("\"bind_interface\": \"Ethernet 2\"", _host.Starts[1]);
    }

    [Fact]
    public async Task Crash_while_lan_is_down_restarts_with_last_known_lan()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lan: false), On());

        _host.Crash();
        await c.HandleExitAsync(1);

        Assert.Equal(2, _host.Starts.Count);
        Assert.Contains("\"bind_interface\": \"Ethernet\"", _host.Starts[1]);
        Assert.Equal(SmartState.Running, c.Status.State);
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
        _time.Advance(SmartRoutingController.LanOfflinePopupDelay);

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
        await c.KeepWaitingAsync();
        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);
        Assert.Empty(_popups);

        for (var i = 0; i < 3; i++) await c.ApplyAsync(Net(), On()); // LAN healthy x3 -> outage over
        await c.ApplyAsync(Net(lanMs: null), On());                    // new outage
        _time.Advance(SmartRoutingController.LanOfflinePopupDelay);
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
        _time.Advance(SmartRoutingController.LanOfflinePopupDelay);

        _host.RaiseLine(MatchYouTube);
        _host.RaiseLine(FailLanOnly);

        Assert.True(SpinWait.SpinUntil(() => _popups.Count == 1, TimeSpan.FromSeconds(5)));
    }

    // ---- R2: use-phone / keep-waiting only inside a real outage ----

    [Fact]
    public async Task Use_phone_while_lan_is_online_is_ignored()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.UseLanRulesOnPhoneAsync();

        Assert.Empty(_api.Selects);
        Assert.False(c.Status.LanRulesOnPhone);
    }

    [Fact]
    public async Task Keep_waiting_while_lan_is_online_is_ignored()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.KeepWaitingAsync();

        await c.ApplyAsync(Net(lanMs: null), On()); // a real outage starts afterwards
        _time.Advance(SmartRoutingController.LanOfflinePopupDelay);
        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);

        Assert.Single(_popups);
    }

    [Fact]
    public async Task Switching_off_and_on_clears_use_phone()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        await c.UseLanRulesOnPhoneAsync();
        Assert.True(c.Status.LanRulesOnPhone);

        await c.ApplyAsync(Net(), On(false));
        await c.ApplyAsync(Net(), On());

        Assert.False(c.Status.LanRulesOnPhone);
    }

    // ---- R3: popup noise on a flaky LAN ----

    [Fact]
    public async Task Lan_blip_shorter_than_delay_with_old_failures_raises_no_popup()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly); // dial failure while the LAN is still online

        await c.ApplyAsync(Net(lanMs: null), On()); // one null probe
        _time.Advance(TimeSpan.FromSeconds(5));
        await c.ApplyAsync(Net(), On()); // healthy again, well inside the delay

        Assert.Empty(_popups);
        Assert.Empty(c.Status.WaitingNames);
    }

    [Fact]
    public async Task Popup_waits_for_ten_seconds_of_lan_outage()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);

        Assert.Empty(_popups);
        Assert.Equal(new[] { "YouTube" }, c.Status.WaitingNames); // the card row shows it straight away

        _time.Advance(TimeSpan.FromSeconds(9));
        await c.ApplyAsync(Net(lanMs: null), On());
        Assert.Empty(_popups);

        _time.Advance(TimeSpan.FromSeconds(1));
        await c.ApplyAsync(Net(lanMs: null), On());
        Assert.Single(_popups);
    }

    // ---- R4: shutdown ----

    [Fact]
    public async Task Shutdown_stops_and_apply_afterwards_does_not_restart()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ShutdownAsync();
        await c.ApplyAsync(Net(), On());
        _host.Crash();
        await c.HandleExitAsync(1);

        Assert.Equal(1, _host.Stops);
        Assert.Single(_host.Starts);
        Assert.False(_host.IsRunning);
    }

    // ---- R5: errors must not vanish ----

    [Fact]
    public async Task Throwing_free_port_counts_as_start_failure()
    {
        var c = Create(() => throw new InvalidOperationException("no ports"));

        await c.ApplyAsync(Net(), On());
        Assert.NotEqual(SmartState.Running, c.Status.State);
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(), On());

        Assert.Empty(_host.Starts);
        Assert.Equal(SmartState.Faulted, c.Status.State);
    }

    [Fact]
    public async Task Throwing_stop_does_not_escape_and_does_not_wedge()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _host.StopThrows = new IOException("access denied");

        await c.ApplyAsync(Net(RoutingMode.Lan), On()); // must not throw
        Assert.Equal(SmartState.Unavailable, c.Status.State);

        _host.StopThrows = null;
        await c.ApplyAsync(Net(phoneIndex: 36), On()); // the old process is still there: a changed key restarts it

        Assert.Equal(SmartState.Running, c.Status.State);
        Assert.Equal(2, _host.Starts.Count);
    }

    [Fact]
    public async Task Throwing_event_subscriber_does_not_escape_ProcessLine()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        _time.Advance(SmartRoutingController.LanOfflinePopupDelay);
        c.StatusChanged += _ => throw new InvalidOperationException("ui gone");
        c.WaitingDetected += _ => throw new InvalidOperationException("ui gone");

        var ex = await Record.ExceptionAsync(async () =>
        {
            await c.ProcessLineAsync(MatchYouTube);
            await c.ProcessLineAsync(FailLanOnly);
        });

        Assert.Null(ex);
    }

    // ---- R7: API health, disposal, toast ----

    [Fact]
    public async Task Three_failed_polls_after_grace_restart_sing_box()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _time.Advance(SmartRoutingController.ApiStartGrace);
        _api.ConnectionsUnreachable = true;

        await c.PollStatsAsync();
        await c.PollStatsAsync();
        Assert.Single(_host.Starts);
        await c.PollStatsAsync();

        Assert.Equal(2, _host.Starts.Count);
        Assert.Equal(1, _host.Stops);
        Assert.Equal(SmartState.Running, c.Status.State);
    }

    [Fact]
    public async Task A_successful_poll_resets_the_failure_count()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _time.Advance(SmartRoutingController.ApiStartGrace);

        _api.ConnectionsUnreachable = true;
        await c.PollStatsAsync();
        await c.PollStatsAsync();
        _api.ConnectionsUnreachable = false;
        await c.PollStatsAsync();
        _api.ConnectionsUnreachable = true;
        await c.PollStatsAsync();
        await c.PollStatsAsync();

        Assert.Single(_host.Starts);
    }

    [Fact]
    public async Task Failed_polls_count_towards_the_crash_limit()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.ConnectionsUnreachable = true;

        for (var i = 0; i < 9; i++)
        {
            _time.Advance(SmartRoutingController.ApiStartGrace); // each restart starts a new grace period
            await c.PollStatsAsync();
        }

        Assert.Equal(3, _host.Starts.Count);
        Assert.Equal(SmartState.Faulted, c.Status.State);
        Assert.False(_host.IsRunning);
    }

    [Fact]
    public async Task Api_client_is_disposed_on_restart_and_on_stop()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(phoneIndex: 36), On());
        Assert.Equal(1, _api.Disposals);

        await c.StopAsync();
        Assert.Equal(2, _api.Disposals);
    }

    [Fact]
    public async Task Lan_back_toast_comes_after_the_selector_moves_back()
    {
        var c = Create();
        var selectsAtToast = -1;
        c.Notify += _ => selectsAtToast = _api.Selects.Count;
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        await c.UseLanRulesOnPhoneAsync();
        Assert.Single(_api.Selects);

        for (var i = 0; i < 3; i++) await c.ApplyAsync(Net(), On());

        Assert.Equal(2, selectsAtToast);
    }

    [Fact]
    public async Task Lan_back_toast_is_not_raised_when_smart_routing_is_not_running()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        await c.UseLanRulesOnPhoneAsync();

        for (var i = 0; i < 3; i++) await c.ApplyAsync(Net(canModify: false), On());

        Assert.Empty(_notes);
    }

    // ---- Round 2 ----

    // N1: startup grace for the API-failure counter

    [Fact]
    public async Task Api_failures_during_startup_grace_do_not_count()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.ConnectionsUnreachable = true;

        for (var i = 0; i < 5; i++)
        {
            await c.PollStatsAsync();
            _time.Advance(TimeSpan.FromSeconds(5)); // 25 s in total: still inside the 30 s grace
        }

        Assert.Single(_host.Starts);
        Assert.Equal(0, _host.Stops);
        Assert.Equal(SmartState.Running, c.Status.State);
        Assert.Equal(1, _logs.Count(l => l.Contains("Clash API not up yet")));
        Assert.DoesNotContain(_logs, l => l.Contains("exited") || l.Contains("unresponsive"));
    }

    [Fact]
    public async Task A_restart_gets_a_new_grace_period()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _api.ConnectionsUnreachable = true;
        _time.Advance(SmartRoutingController.ApiStartGrace);
        for (var i = 0; i < 3; i++) await c.PollStatsAsync(); // restarts
        Assert.Equal(2, _host.Starts.Count);

        for (var i = 0; i < 5; i++) await c.PollStatsAsync(); // inside the new grace

        Assert.Equal(2, _host.Starts.Count);
    }

    // N2: a failed stop must not leave two sing-boxes

    [Fact]
    public async Task Failed_stop_does_not_start_a_second_instance()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _host.StopThrows = new IOException("access denied"); // StopAsync throws and the process stays up

        await c.ApplyAsync(Net(phoneIndex: 36), On()); // changed key: wants a restart

        Assert.Single(_host.Starts);
        Assert.Contains(_logs, l => l.Contains("could not stop the previous sing-box"));

        await c.ApplyAsync(Net(phoneIndex: 36), On());
        await c.ApplyAsync(Net(phoneIndex: 36), On()); // third crash faults it
        Assert.Single(_host.Starts);
        Assert.Equal(SmartState.Faulted, c.Status.State);
    }

    [Fact]
    public async Task Failed_stop_status_message_is_honest()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _host.StopThrows = new IOException("access denied");

        await c.ApplyAsync(Net(RoutingMode.Lan), On());

        Assert.Equal(SmartState.Unavailable, c.Status.State);
        Assert.Equal("sing-box could not be stopped (see log)", c.Status.Message);
    }

    [Fact]
    public async Task Shutdown_with_a_throwing_stop_does_not_escape_and_stays_latched()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        _host.StopThrows = new IOException("access denied");

        await c.ShutdownAsync();
        _host.StopThrows = null;
        await c.ApplyAsync(Net(phoneIndex: 36), On());

        Assert.Single(_host.Starts);
        Assert.Contains(_logs, l => l.Contains("stopping sing-box failed"));
    }

    // N3: the toast only follows a selector move that really happened

    [Fact]
    public async Task Lan_back_toast_waits_for_the_selector_and_the_next_apply_retries()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        await c.UseLanRulesOnPhoneAsync();
        _api.SelectResult = false; // sing-box refuses the select

        for (var i = 0; i < 3; i++) await c.ApplyAsync(Net(), On());
        Assert.Empty(_notes);

        _api.SelectResult = true;
        await c.ApplyAsync(Net(), On());

        Assert.Single(_notes);
        Assert.Equal(("lan-only", "lan"), _api.Selects.Last());
    }

    // N4: a LAN without DNS yet must not forget a known one

    [Fact]
    public async Task Lan_present_without_dns_does_not_restart()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());

        await c.ApplyAsync(Net(lanDns: null), On());

        Assert.Single(_host.Starts);
        Assert.Equal(0, _host.Stops);
    }

    // N5: nothing is processed after shutdown

    [Fact]
    public async Task Lines_after_shutdown_are_ignored()
    {
        var c = Create();
        await c.ApplyAsync(Net(), On());
        await c.ApplyAsync(Net(lanMs: null), On());
        _time.Advance(SmartRoutingController.LanOfflinePopupDelay);
        await c.ShutdownAsync();
        var statusEvents = 0;
        c.StatusChanged += _ => statusEvents++;

        await c.ProcessLineAsync(MatchYouTube);
        await c.ProcessLineAsync(FailLanOnly);

        Assert.Equal(0, statusEvents);
        Assert.Empty(_popups);
    }
}
