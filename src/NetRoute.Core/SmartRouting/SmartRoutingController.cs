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
public sealed class SmartRoutingController
{
    public const int CrashLimit = 3;
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(30);
    public const int LanHealthyChecksToRevert = 3;

    const string FaultMessage = "Smart routing stopped — sing-box keeps crashing (see log)";

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
    bool _stopping;
    bool _faulted;
    bool _lastEnabled;
    RouteExit _wantedExit = RouteExit.Phone;
    RouteExit _appliedExit = RouteExit.Phone;
    bool _lanRulesOnPhone;
    bool _appliedLanRulesOnPhone;
    bool _lanOnline = true;
    int _lanHealthyCount;
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
        await _gate.WaitAsync(ct);
        try
        {
            _net = net;
            _settings = settings;
            var smart = settings.SmartRouting;
            if (smart.Enabled && !_lastEnabled)
            {
                _faulted = false; // switching it on again clears a fault
                _crashes.Clear();
            }
            _lastEnabled = smart.Enabled;
            _rules = RuleSet.Build(_catalog, smart);
            UpdateLanHealth(net);

            var (state, _) = Desired();
            if (state != SmartState.Running)
            {
                await StopIfRunningAsync();
                Publish();
                return;
            }

            _wantedExit = net.Mode == RoutingMode.Phone && net.IsHealing ? RouteExit.Lan : RouteExit.Phone;
            if (!_host.IsRunning || _runningKey != KeyFor(net)) await StartAsync(ct);
            else await SyncSelectorsAsync(ct);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UseLanRulesOnPhoneAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _lanRulesOnPhone = true;
            _popupRaised = true;
            _log("Smart routing: LAN-only traffic uses the phone until the LAN is back");
            await SyncSelectorsAsync(default);
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void KeepWaiting() => _keepWaiting = true;

    public async Task PollStatsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_api is null || !_host.IsRunning) return;
            if (await _api.GetConnectionsAsync(ct) is not { } connections) return;
            _counter.Update(connections, _rules, _appliedExit == RouteExit.Phone, Today());
            Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopIfRunningAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task ProcessLineAsync(string line)
    {
        await _gate.WaitAsync();
        try
        {
            _recentLines.Enqueue(line);
            if (_recentLines.Count > 20) _recentLines.Dequeue();
            if (_tracker is null || SingBoxLogParser.Parse(line) is not { } ev) return;
            if (_tracker.Observe(ev) is not null) Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task HandleExitAsync(int code)
    {
        if (_stopping) return;
        await _gate.WaitAsync();
        try
        {
            if (_stopping || _host.IsRunning) return; // a stop we asked for, or already restarted
            _runningKey = null;
            _api = null;
            var lastError = _recentLines.LastOrDefault(l => l.Contains("FATAL") || l.Contains("ERROR"));
            RecordCrash($"sing-box exited unexpectedly (code {code}){(lastError is null ? "" : ": " + lastError)}");
            if (Desired().State == SmartState.Running) await StartAsync(default);
            Publish();
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
        if (net.Adapters.Phone is null || net.Adapters.Lan is null) return (SmartState.Unavailable, "Needs both phone and LAN connected");
        return (SmartState.Running, null);
    }

    string KeyFor(NetworkStatus net) =>
        $"{net.Adapters.Phone!.Name}|{net.Adapters.Lan!.Name}|{net.Adapters.Lan.DnsServer}|{_rules.Fingerprint}";

    async Task StartAsync(CancellationToken ct)
    {
        await StopIfRunningAsync();
        var net = _net!;
        var port = _freePort();
        var secret = Guid.NewGuid().ToString("N");
        var config = SingBoxConfigBuilder.Build(new SingBoxConfigInput(
            _rules, net.Adapters.Phone!.Name, net.Adapters.Lan!.Name, net.Adapters.Lan.DnsServer,
            _wantedExit, _lanRulesOnPhone, port, secret));
        _tracker = new LanWaitTracker(config.RuleIndexToEntryId, _time);
        _api = _createApi(port, secret);
        _log($"Smart routing: starting sing-box ({_rules.Entries.Count} rules, exit {_wantedExit})");
        try
        {
            await _host.StartAsync(config.Json, ct);
            _runningKey = KeyFor(net);
            _appliedExit = _wantedExit;
            _appliedLanRulesOnPhone = _lanRulesOnPhone;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runningKey = null;
            _api = null;
            RecordCrash($"sing-box could not start: {ex.Message}");
        }
    }

    async Task StopIfRunningAsync()
    {
        if (!_host.IsRunning)
        {
            _runningKey = null;
            return;
        }
        _stopping = true;
        try
        {
            await _host.StopAsync();
        }
        finally
        {
            _stopping = false;
        }
        _runningKey = null;
        _api = null;
        _log("Smart routing: sing-box stopped");
    }

    async Task SyncSelectorsAsync(CancellationToken ct)
    {
        if (_api is null) return;
        if (_appliedExit != _wantedExit
            && await _api.SelectAsync(SingBoxConfigBuilder.DefaultTag, Tag(_wantedExit), ct))
        {
            _appliedExit = _wantedExit;
            _log($"Smart routing: default exit is now {_wantedExit}");
        }
        if (_appliedLanRulesOnPhone != _lanRulesOnPhone
            && await _api.SelectAsync(SingBoxConfigBuilder.LanOnlyTag,
                   _lanRulesOnPhone ? SingBoxConfigBuilder.PhoneTag : SingBoxConfigBuilder.LanTag, ct))
        {
            _appliedLanRulesOnPhone = _lanRulesOnPhone;
        }
    }

    static string Tag(RouteExit exit) => exit == RouteExit.Lan ? SingBoxConfigBuilder.LanTag : SingBoxConfigBuilder.PhoneTag;

    void UpdateLanHealth(NetworkStatus net)
    {
        var healthy = net.Adapters.Lan is not null && net.LanLatencyMs is not null;
        if (!healthy)
        {
            _lanHealthyCount = 0;
            _lanOnline = false;
            return;
        }
        if (_lanOnline || ++_lanHealthyCount < LanHealthyChecksToRevert) return;

        _lanOnline = true;
        _popupRaised = false;
        _keepWaiting = false;
        _tracker?.Clear();
        if (!_lanRulesOnPhone) return;
        _lanRulesOnPhone = false;
        Notify?.Invoke("LAN back — LAN-only traffic is back on the LAN");
        _ = Task.CompletedTask; // selectors are synced by ApplyAsync right after this call
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
        Notify?.Invoke(FaultMessage);
    }

    void Publish()
    {
        var (desired, message) = Desired();
        var state = desired == SmartState.Running && !(_host.IsRunning && _runningKey is not null) ? SmartState.Starting : desired;
        IReadOnlyList<string> waiting = state == SmartState.Running && !_lanOnline && !_lanRulesOnPhone && _tracker is not null
            ? _tracker.RecentFailures(WaitWindow)
                .Select(id => _rules.Entries.FirstOrDefault(e => e.Id == id)?.Name ?? id).ToList()
            : [];

        Status = new SmartRoutingStatus(state, message, _appliedExit, _lanRulesOnPhone, _lanOnline, waiting,
            _rules.Entries.Count, _counter.Snapshot);
        StatusChanged?.Invoke(Status);

        if (waiting.Count == 0 || _popupRaised || _keepWaiting) return;
        _popupRaised = true;
        WaitingDetected?.Invoke(waiting);
    }

    DateOnly Today() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
}
