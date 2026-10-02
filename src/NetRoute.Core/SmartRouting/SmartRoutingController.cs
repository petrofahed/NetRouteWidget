namespace NetRoute.Core;

public enum SmartState { Off, Unavailable, Starting, Running, Faulted }

public sealed record SmartRoutingStatus(
    SmartState State, string? Message, RouteExit DefaultExit, bool LanRulesOnPhone, bool LanOnline,
    IReadOnlyList<string> WaitingNames, int RuleCount, DailyStats Today)
{
    public bool IsActive => State == SmartState.Running;
    public bool Waiting => WaitingNames.Count > 0;
}

/// Owns the sing-box lifecycle for Smart routing: when it runs, with which config, which exit the selectors use,
/// crash/restart policy, LAN-outage "waiting" detection and the kept-off-4G counter.
/// All state is guarded by one gate; host events arrive on other threads and queue behind it.
/// LAN-only traffic never falls back to the phone on its own: when the LAN adapter vanishes sing-box keeps running
/// (bound to the last-known LAN name) so those connections wait; only UseLanRulesOnPhoneAsync moves them.
public sealed class SmartRoutingController
{
    public const int CrashLimit = 3;
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LanOfflinePopupDelay = TimeSpan.FromSeconds(10);
    public const int LanHealthyChecksToRevert = 3;
    public const int ApiFailureLimit = 3;
    /// sing-box needs a moment before its Clash API listens; polls that fail inside this window are not failures.
    public static readonly TimeSpan ApiStartGrace = TimeSpan.FromSeconds(30);

    const string FaultMessage = "Smart routing stopped — sing-box keeps crashing (see log)";
    const string NeedsBothMessage = "Needs both phone and LAN connected";

    readonly IReadOnlyList<RuleItem> _catalog;
    readonly ISingBoxHost _host;
    readonly Func<int, string, ISingBoxApi> _createApi;
    readonly Func<int> _freePort;
    readonly Action<string> _log;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly List<DateTimeOffset> _crashes = new();
    readonly Queue<string> _recentLines = new();
    readonly DataSavedCounter _counter;

    ISingBoxApi? _api;
    LanWaitTracker? _tracker;
    RuleSet _rules = new([]);
    NetworkStatus? _net;
    AppSettings? _settings;
    string? _runningKey;
    string? _lastLanName;
    string? _lastLanDns;
    volatile bool _stopping;
    bool _shutdown;
    bool _faulted;
    bool _lastEnabled;
    RouteExit _wantedExit = RouteExit.Phone;
    RouteExit _appliedExit = RouteExit.Phone;
    bool _lanRulesOnPhone;
    bool _appliedLanRulesOnPhone;
    bool _lanOnline = true;
    DateTimeOffset? _lanOfflineSince;
    int _lanHealthyCount;
    int _apiFailures;
    DateTimeOffset _apiGraceUntil;
    bool _graceLogged;
    bool _lanBackToastPending;
    bool _popupRaised;
    bool _keepWaiting;

    public SmartRoutingController(
        IReadOnlyList<RuleItem> catalog, ISingBoxHost host, Func<int, string, ISingBoxApi> createApi,
        Func<int> freePort, DailyStats? restoredStats, Action<string> log, TimeProvider? time = null)
    {
        _catalog = catalog;
        _host = host;
        _createApi = createApi;
        _freePort = freePort;
        _log = log;
        _time = time ?? TimeProvider.System;
        _counter = new DataSavedCounter(restoredStats, Today());
        _host.LineReceived += line => _ = ProcessLineAsync(line);
        _host.Exited += code => _ = HandleExitAsync(code);
        Status = new SmartRoutingStatus(SmartState.Off, null, RouteExit.Phone, false, true, [], 0, _counter.Snapshot);
    }

    public SmartRoutingStatus Status { get; private set; }
    public event Action<SmartRoutingStatus>? StatusChanged;
    public event Action<IReadOnlyList<string>>? WaitingDetected;
    public event Action<string>? Notify;

    public async Task ApplyAsync(NetworkStatus net, AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shutdown) return;
            try
            {
                _net = net;
                _settings = settings;
                if (net.Adapters.Lan is { } lan)
                {
                    _lastLanName = lan.Name;
                    _lastLanDns = lan.DnsServer ?? _lastLanDns; // a LAN with no DNS yet must not forget a known one
                }

                var smart = settings.SmartRouting;
                if (smart.Enabled && !_lastEnabled)
                {
                    // Switching it on again clears a fault and any leftover outage choices.
                    _faulted = false;
                    _crashes.Clear();
                    _lanRulesOnPhone = false;
                    _keepWaiting = false;
                    _popupRaised = false;
                    _lanBackToastPending = false;
                }
                _lastEnabled = smart.Enabled;
                _rules = RuleSet.Build(_catalog, smart);
                UpdateLanHealth(net);

                var (state, _) = Desired();
                if (state != SmartState.Running)
                {
                    _lanBackToastPending = false;
                    await StopIfRunningAsync().ConfigureAwait(false);
                    Publish();
                    return;
                }

                _wantedExit = net.Mode == RoutingMode.Phone && net.IsHealing ? RouteExit.Lan : RouteExit.Phone;
                if (!_host.IsRunning || _runningKey != KeyFor(net)) await StartAsync(ct).ConfigureAwait(false);
                else await SyncSelectorsAsync(ct).ConfigureAwait(false);
                // Only once the selector really moved back (a failed select is retried by the next Apply).
                if (_lanBackToastPending && _host.IsRunning && _appliedLanRulesOnPhone == _lanRulesOnPhone)
                {
                    _lanBackToastPending = false;
                    Raise(Notify, "LAN back — LAN-only traffic is back on the LAN");
                }
                Publish();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Smart routing: apply failed: {ex.Message}");
                SafePublish();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UseLanRulesOnPhoneAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Only meaningful inside a real outage; a stray click must not move LAN-only traffic to the phone.
            if (_shutdown || Desired().State != SmartState.Running || _lanOnline) return;
            _lanRulesOnPhone = true;
            _lanBackToastPending = false;
            _popupRaised = true;
            _log("Smart routing: LAN-only traffic uses the phone until the LAN is back");
            await SyncSelectorsAsync(default).ConfigureAwait(false);
            Publish();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Smart routing: could not switch LAN-only traffic to the phone: {ex.Message}");
            SafePublish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task KeepWaitingAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shutdown || Desired().State != SmartState.Running || _lanOnline) return;
            _keepWaiting = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PollStatsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_shutdown || _api is null || !_host.IsRunning) return;
            try
            {
                if (await _api.GetConnectionsAsync(ct).ConfigureAwait(false) is not { } connections)
                {
                    if (_time.GetUtcNow() < _apiGraceUntil)
                    {
                        if (!_graceLogged) _log("Smart routing: Clash API not up yet");
                        _graceLogged = true;
                        return;
                    }
                    if (++_apiFailures < ApiFailureLimit) return;
                    // A hung sing-box keeps the TUN capturing traffic, which would kill the internet: treat it as a crash.
                    _log("Smart routing: control API unresponsive, restarting sing-box");
                    await StopIfRunningAsync().ConfigureAwait(false);
                    RecordCrash("control API unresponsive");
                    if (Desired().State == SmartState.Running) await StartAsync(default).ConfigureAwait(false);
                    Publish();
                    return;
                }
                _apiFailures = 0;
                _counter.Update(connections, _rules, _appliedExit == RouteExit.Phone, Today());
                Publish();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Smart routing: stats poll failed: {ex.Message}");
                SafePublish();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Stops sing-box; a later ApplyAsync starts it again.
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopIfRunningAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Final stop for app exit: after this nothing restarts sing-box, even if a queued Apply or exit event arrives.
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _shutdown = true;
            await StopIfRunningAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ProcessLineAsync(string line)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shutdown) return;
            try
            {
                _recentLines.Enqueue(line);
                if (_recentLines.Count > 20) _recentLines.Dequeue();
                if (_tracker is null || SingBoxLogParser.Parse(line) is not { } ev) return;
                if (_tracker.Observe(ev) is not null) Publish();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Smart routing: log line handling failed: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task HandleExitAsync(int code)
    {
        if (_stopping || _shutdown) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping || _shutdown || _host.IsRunning) return; // a stop we asked for, or already restarted
            try
            {
                _runningKey = null;
                DropApi();
                var lastError = _recentLines.LastOrDefault(l => l.Contains("FATAL") || l.Contains("ERROR"));
                RecordCrash($"sing-box exited unexpectedly (code {code}){(lastError is null ? "" : ": " + lastError)}");
                if (Desired().State == SmartState.Running) await StartAsync(default).ConfigureAwait(false);
                Publish();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Smart routing: restart after exit failed: {ex.Message}");
                SafePublish();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    (SmartState State, string? Message) Desired()
    {
        if (_net is not { } net || _settings is not { } settings || !settings.SmartRouting.Enabled) return (SmartState.Off, null);
        if (_faulted) return (SmartState.Faulted, FaultMessage);
        if (!net.CanModify) return (SmartState.Unavailable, "Needs administrator rights");
        if (net.Mode == RoutingMode.Lan) return (SmartState.Unavailable, "Not needed in LAN mode");
        // Losing the phone must stop sing-box (v1 then falls back to the LAN). Losing the LAN must NOT: LAN-only
        // traffic has to wait for it, so sing-box keeps running against the last-known LAN adapter.
        if (net.Adapters.Phone is null) return (SmartState.Unavailable, NeedsBothMessage);
        if (net.Adapters.Lan is null && _lastLanName is null) return (SmartState.Unavailable, NeedsBothMessage);
        return (SmartState.Running, null);
    }

    string? LanName(NetworkStatus net) => net.Adapters.Lan?.Name ?? _lastLanName;
    string? LanDns(NetworkStatus net) => net.Adapters.Lan?.DnsServer ?? _lastLanDns;

    string KeyFor(NetworkStatus net) =>
        $"{net.Adapters.Phone!.Name}|{LanName(net)}|{LanDns(net)}|{_rules.Fingerprint}";

    async Task StartAsync(CancellationToken ct)
    {
        await StopIfRunningAsync().ConfigureAwait(false);
        if (_host.IsRunning)
        {
            // The old process survived a failed stop: starting another would leave two sing-boxes fighting over the TUN.
            RecordCrash("could not stop the previous sing-box");
            return;
        }
        _apiFailures = 0;
        _graceLogged = false;
        var net = _net!;
        try
        {
            var port = _freePort();
            var secret = Guid.NewGuid().ToString("N");
            var config = SingBoxConfigBuilder.Build(new SingBoxConfigInput(
                _rules, net.Adapters.Phone!.Name, LanName(net)!, LanDns(net),
                _wantedExit, _lanRulesOnPhone, port, secret));
            _tracker = new LanWaitTracker(config.RuleIndexToEntryId, _time);
            _api = _createApi(port, secret);
            _log($"Smart routing: starting sing-box ({_rules.Entries.Count} rules, exit {_wantedExit})");
            await _host.StartAsync(config.Json, ct).ConfigureAwait(false);
            _apiGraceUntil = _time.GetUtcNow() + ApiStartGrace;
            _runningKey = KeyFor(net);
            _appliedExit = _wantedExit;
            _appliedLanRulesOnPhone = _lanRulesOnPhone;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runningKey = null;
            DropApi();
            RecordCrash($"sing-box could not start: {ex.Message}");
        }
    }

    async Task StopIfRunningAsync()
    {
        if (!_host.IsRunning)
        {
            _runningKey = null;
            DropApi();
            return;
        }
        _stopping = true;
        try
        {
            await _host.StopAsync().ConfigureAwait(false);
            _log("Smart routing: sing-box stopped");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Smart routing: stopping sing-box failed: {ex.Message}");
        }
        finally
        {
            _stopping = false;
        }
        // A failed stop leaves the old process (and its config key and API client) in place, so state stays truthful.
        if (_host.IsRunning) return;
        _runningKey = null;
        DropApi();
    }

    void DropApi()
    {
        var api = _api;
        _api = null;
        try
        {
            (api as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _log($"Smart routing: disposing API client failed: {ex.Message}");
        }
    }

    async Task SyncSelectorsAsync(CancellationToken ct)
    {
        if (_api is null) return;
        if (_appliedExit != _wantedExit
            && await _api.SelectAsync(SingBoxConfigBuilder.DefaultTag, Tag(_wantedExit), ct).ConfigureAwait(false))
        {
            _appliedExit = _wantedExit;
            _log($"Smart routing: default exit is now {_wantedExit}");
        }
        if (_appliedLanRulesOnPhone != _lanRulesOnPhone
            && await _api.SelectAsync(SingBoxConfigBuilder.LanOnlyTag,
                   _lanRulesOnPhone ? SingBoxConfigBuilder.PhoneTag : SingBoxConfigBuilder.LanTag, ct).ConfigureAwait(false))
        {
            _appliedLanRulesOnPhone = _lanRulesOnPhone;
        }
    }

    static string Tag(RouteExit exit) => exit == RouteExit.Lan ? SingBoxConfigBuilder.LanTag : SingBoxConfigBuilder.PhoneTag;

    /// When a probe ends a "use phone until LAN is back" period it queues the "LAN back" toast (see ApplyAsync).
    void UpdateLanHealth(NetworkStatus net)
    {
        var healthy = net.Adapters.Lan is not null && net.LanLatencyMs is not null;
        if (!healthy)
        {
            if (_lanOnline)
            {
                // Only failures seen from now on belong to this outage; earlier ones are stale.
                _tracker?.Clear();
                _lanOfflineSince = _time.GetUtcNow();
            }
            _lanHealthyCount = 0;
            _lanOnline = false;
            return;
        }
        if (_lanOnline || ++_lanHealthyCount < LanHealthyChecksToRevert) return;

        _lanOnline = true;
        _lanOfflineSince = null;
        _popupRaised = false;
        _keepWaiting = false;
        _tracker?.Clear();
        if (!_lanRulesOnPhone) return;
        _lanRulesOnPhone = false; // pushed to sing-box by SyncSelectorsAsync (or a restart) later in ApplyAsync
        _lanBackToastPending = true;
    }

    void RecordCrash(string message)
    {
        var now = _time.GetUtcNow();
        _crashes.Add(now);
        _crashes.RemoveAll(t => now - t > CrashWindow);
        _log("Smart routing: " + message);
        if (_crashes.Count < CrashLimit || _faulted) return;
        _faulted = true;
        _log("Smart routing: " + FaultMessage);
        Raise(Notify, FaultMessage);
    }

    void Publish()
    {
        var (desired, message) = Desired();
        // Not wanted but still alive: a stop failed. Say so instead of claiming the stop worked.
        if (desired != SmartState.Running && _host.IsRunning) message = "sing-box could not be stopped (see log)";
        var state = desired == SmartState.Running && !(_host.IsRunning && _runningKey is not null) ? SmartState.Starting : desired;
        IReadOnlyList<string> waiting = state == SmartState.Running && !_lanOnline && !_lanRulesOnPhone && _tracker is not null
            ? _tracker.RecentFailures(WaitWindow)
                .Select(id => _rules.Entries.FirstOrDefault(e => e.Id == id)?.Name ?? id).ToList()
            : [];

        Status = new SmartRoutingStatus(state, message, _appliedExit, _lanRulesOnPhone, _lanOnline, waiting,
            _rules.Entries.Count, _counter.Snapshot);
        Raise(StatusChanged, Status);

        if (waiting.Count == 0 || _popupRaised || _keepWaiting) return;
        // A flaky LAN must not spam popups: only a sustained outage asks the question.
        if (_lanOfflineSince is not { } since || _time.GetUtcNow() - since < LanOfflinePopupDelay) return;
        _popupRaised = true;
        Raise(WaitingDetected, waiting);
    }

    void SafePublish()
    {
        try
        {
            Publish();
        }
        catch (Exception ex)
        {
            _log($"Smart routing: publishing status failed: {ex.Message}");
        }
    }

    /// Subscribers run under the gate on whatever thread got here; one that throws must not break the state machine.
    void Raise<T>(Action<T>? handler, T arg)
    {
        if (handler is null) return;
        foreach (var d in handler.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                d(arg);
            }
            catch (Exception ex)
            {
                _log($"Smart routing: event subscriber failed: {ex.Message}");
            }
        }
    }

    DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
}
