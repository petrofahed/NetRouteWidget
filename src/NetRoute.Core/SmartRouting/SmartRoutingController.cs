using System.Text.RegularExpressions;

namespace NetRoute.Core;

public enum SmartState { Off, Unavailable, Starting, Running, Faulted }

public sealed record WaitingTarget(RuleTarget Target, ConnectionId Main, IReadOnlyList<string> Names, bool UsingMain, bool CanUseMain, long Generation);

public sealed record SmartRoutingStatus(
    SmartState State, string? Message, ConnectionId DefaultExit, IReadOnlyList<WaitingTarget> Targets,
    int RuleCount, DailyStats Today, string? Detail = null, ConnectionId Profile = ConnectionId.Phone)
{
    public bool IsActive => State == SmartState.Running;
    public bool Waiting => Targets.Any(target => target.Names.Count > 0);
}

/// Owns the single gated sing-box lifecycle, per-target exception consent/recovery and usage accounting.
/// Non-metered targets never gain Phone membership without explicit consent bound to the clicked main/context.
public sealed class SmartRoutingController
{
    public const int CrashLimit = 3;
    /// "lan" outbound binding when no LAN adapter name is known at all (first run, LAN absent): sing-box starts fine with
    /// a nonexistent bind_interface and every LAN-only dial just fails, which is the "wait" behaviour.
    public const string LanPlaceholder = "NetRoute-LAN-not-connected";
    const int DetailMaxLength = 120;
    static readonly Regex AnsiCodes = new(@"\u001B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    static readonly Regex TimestampPrefix = new(@"^\s*[+-]\d{4}\s+\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}(\.\d+)?\s+", RegexOptions.Compiled);
    static readonly Regex ConnectionIdPattern = new(@"\[\d+\s[^\]]*\]\s*", RegexOptions.Compiled);
    static readonly Regex LevelCounter = new(@"(?<=[A-Z])\[\d{4}\]\s*", RegexOptions.Compiled); // "FATAL[0000] message"
    static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    public static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan WaitWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LanOfflinePopupDelay = TimeSpan.FromSeconds(10);
    public const int LanHealthyChecksToRevert = 3;
    /// The Clash API is polled once a second, so this is about ten seconds of silence before sing-box counts as hung.
    public const int ApiFailureLimit = 10;
    /// sing-box needs a moment before its Clash API listens; polls that fail inside this window are not failures.
    public static readonly TimeSpan ApiStartGrace = TimeSpan.FromSeconds(30);

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
    readonly UsageCounter _usage;
    readonly IAdapterCounters? _counters;
    readonly ITrafficSource? _traffic;

    ISingBoxApi? _api;
    RuleWaitTracker? _tracker;
    RuleSet _rules = new([]);
    NetworkStatus? _net;
    AppSettings? _settings;
    string? _runningKey;
    readonly Dictionary<ConnectionId, string> _lastNames = new();
    readonly Dictionary<ConnectionId, string?> _bindingIdentities = new();
    IReadOnlyDictionary<ConnectionId, string> _interfaces = new Dictionary<ConnectionId, string>();
    string? _permissionContext;
    sealed class TargetState
    {
        public ConnectionId? PermittedMain;
        public long ActionGeneration;
        public bool Blocked;
        public bool AppliedUseMain, PendingBack, PopupRaised, KeepWaiting;
        public int HealthyChecks;
        public DateTimeOffset? OfflineSince;
    }
    readonly Dictionary<RuleTarget, TargetState> _targets = Enum.GetValues<RuleTarget>().ToDictionary(target => target, _ => new TargetState());
    readonly Dictionary<RuleTarget, ConnectionId?> _appliedTargets = new();
    long _nextActionGeneration;
    string? _lastLanDns;
    volatile bool _stopping;
    bool _shutdown;
    bool _faulted;
    bool _lastEnabled;
    ConnectionId _wantedExit = ConnectionId.Phone;
    ConnectionId _appliedExit = ConnectionId.Phone;
    int _apiFailures;
    DateTimeOffset _apiGraceUntil;
    bool _graceLogged;
    string? _lastCrashDetail;
    UsageAttribution _attribution;
    bool _adapterReadFailed;
    static readonly UsageRate NoSpeed = new(0, 0);
    volatile UsageRate _liveSpeed = NoSpeed;
    volatile ExitTotals _todayTotals = ExitTotals.Zero;
    bool _measuring; // under the gate: Windows tracing is counting usage because sing-box is not running
    volatile bool _isMeasuring;
    volatile bool _usageWatched; // set from the UI: the usage view is open, so Windows tracing is worth its cost
    string? _trafficError;

    public SmartRoutingController(
        IReadOnlyList<RuleItem> catalog, ISingBoxHost host, Func<int, string, ISingBoxApi> createApi,
        Func<int> freePort, UsageSnapshot? restoredUsage, Action<string> log, TimeProvider? time = null,
        IAdapterCounters? counters = null, ITrafficSource? traffic = null)
    {
        _traffic = traffic;
        _catalog = catalog;
        _host = host;
        _createApi = createApi;
        _freePort = freePort;
        _log = log;
        _time = time ?? TimeProvider.System;
        _counters = counters;
        _usage = new UsageCounter(restoredUsage, Today());
        _attribution = UsageAttribution.Build(catalog, new SmartRoutingSettings());
        _todayTotals = _usage.TodayTotals(Today());
        _host.LineReceived += line => _ = ProcessLineAsync(line);
        _host.Exited += code => _ = HandleExitAsync(code);
        Status = new SmartRoutingStatus(SmartState.Off, null, ConnectionId.Phone, [], 0, _usage.TodayKept(Today()));
    }

    public SmartRoutingStatus Status { get; private set; }

    /// The speed of everything flowing through each exit right now, all applications together (0/0 when idle or when
    /// Smart routing is not running). Immutable and read without the gate, so the UI thread can poll it every second.
    public UsageRate LiveSpeed => _liveSpeed;

    /// The bytes counted today through each exit, all applications together (history, not live speed: it is not zeroed when
    /// a poll fails or sing-box stops). Immutable and read without the gate.
    public ExitTotals TodayTotals => _todayTotals;

    /// True while usage is being counted by Windows tracing because sing-box is not running (the card shows live speeds and
    /// the Usage tab says so). Read without the gate.
    public bool IsMeasuring => _isMeasuring;

    /// Whether someone is looking at the usage numbers (the Smart routing window is open). Windows tracing runs only then,
    /// while sing-box is not running: nothing else needs per-packet work. Takes effect on the next poll.
    public void SetUsageWatched(bool watched) => _usageWatched = watched;

    public event Action<SmartRoutingStatus>? StatusChanged;
    public event Action<WaitingTarget>? WaitingDetected;
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
                _traffic?.SetAdapters(net.Adapters.Phone, net.Adapters.Lan);
                RefreshBindings(net, settings);

                var smart = settings.SmartRouting;
                if (smart.Enabled && !_lastEnabled)
                {
                    // Switching it on again clears a fault and any leftover outage choices.
                    _faulted = false;
                    _crashes.Clear();
                    ResetPermissions();
                }
                _lastEnabled = smart.Enabled;
                _rules = RuleSet.Build(_catalog, smart, SmartRoutingPolicy.ProfileOf(net));
                _attribution = UsageAttribution.Build(_catalog, smart);
                var wanted = SmartRoutingPolicy.WantedExit(net);
                var context = System.Text.Json.JsonSerializer.Serialize(new { Profile = SmartRoutingPolicy.ProfileOf(net), Main = wanted, settings.Overrides, settings.TurnedOff, Bindings = _interfaces, Slots = Connections.All.Select(id => new { Id = id, Used = net.Adapters.IsUsed(id), Off = net.Adapters.IsTurnedOff(id) }) });
                if (_permissionContext != context) ResetPermissions();
                _permissionContext = context;
                if (wanted is { } nextExit) _wantedExit = nextExit;
                foreach (var target in _targets.Values)
                    if (target.PermittedMain is { } permitted && permitted != wanted)
                    {
                        target.PermittedMain = null;
                        target.PendingBack = false;
                    }
                UpdateTargetHealth(net);

                var (state, _) = Desired();
                if (state != SmartState.Running)
                {
                    foreach (var target in _targets.Values) target.PendingBack = false;
                    await StopIfRunningAsync().ConfigureAwait(false);
                    Publish();
                    return;
                }

                if (!_host.IsRunning || _runningKey != KeyFor(net)) await StartAsync(ct).ConfigureAwait(false);
                else await SyncSelectorsAsync(ct).ConfigureAwait(false);
                NotifyAppliedRecovery();
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

    public async Task UseMainAsync(WaitingTarget request)
    {
        var target = request.Target;
        var main = request.Main;
        _ = SmartRoutingPolicy.SelectorTag(target);
        _ = SmartRoutingPolicy.Tag(main);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_shutdown || _net is null || Desired().State != SmartState.Running
                || !_host.IsRunning || _runningKey != KeyFor(_net)
                || SmartRoutingPolicy.WantedExit(_net) != main || NormalSelection(target) is not null
                || !_rules.Entries.Any(entry => entry.Target == target)
                || request.Generation != _targets[target].ActionGeneration) return;
            var state = _targets[target];
            state.PermittedMain = main;
            state.PendingBack = false;
            state.PopupRaised = true;
            await ApplyRoutingAsync(default).ConfigureAwait(false);
            Publish();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Smart routing: could not use {main} for {target}: {ex.Message}");
            SafePublish();
        }
        finally { _gate.Release(); }
    }

    public async Task KeepWaitingAsync(WaitingTarget request)
    {
        var target = request.Target;
        _ = SmartRoutingPolicy.SelectorTag(target);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_shutdown && Desired().State == SmartState.Running && NormalSelection(target) is null
                && _net is not null && SmartRoutingPolicy.WantedExit(_net) == request.Main
                && _host.IsRunning && _runningKey == KeyFor(_net)
                && _rules.Entries.Any(entry => entry.Target == target)
                && request.Generation == _targets[target].ActionGeneration)
                _targets[target].KeepWaiting = true;
        }
        finally { _gate.Release(); }
    }

    async Task ApplyRoutingAsync(CancellationToken ct)
    {
        if (_net is null) return;
        if (!_host.IsRunning || _runningKey != KeyFor(_net)) await StartAsync(ct).ConfigureAwait(false);
        else await SyncSelectorsAsync(ct).ConfigureAwait(false);
    }

    /// A copy of the usage history and the live rates, for the Usage tab.
    public async Task<UsageSnapshot> GetUsageAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _usage.Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// The history, once per change since the last call; null when nothing changed. The caller saves it to usage.json.
    public async Task<UsageSnapshot?> TakeUsageIfChangedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return _usage.TakeDirty() ? _usage.Snapshot() : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Wipes the usage history (and so today's kept-off-4G figure).
    public async Task ClearUsageAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _usage.Clear();
            _todayTotals = _usage.TodayTotals(Today());
            SafePublish();
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
            if (_shutdown) return;
            if (_api is null)
            {
                MeasureWithoutSingBox();
                return;
            }
            // sing-box's own packets cross the physical adapters as well: counting them too would double the traffic.
            DiscardMeasured();
            if (!_host.IsRunning)
            {
                _liveSpeed = NoSpeed; // crashed: the exit event is queued behind the gate, a stale figure must not linger meanwhile
                return;
            }
            try
            {
                if (await _api.GetConnectionsAsync(ct).ConfigureAwait(false) is not { } snapshot)
                {
                    _usage.ClearRates(); // a poll that fails must not leave rows looking busy
                    _liveSpeed = NoSpeed;
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
                _usage.Update(
                    new UsagePoll(snapshot.Connections, snapshot.UploadTotal, snapshot.DownloadTotal, PhoneAdapterBytes(),
                        _appliedExit == ConnectionId.Phone, _time.GetLocalNow()),
                    _attribution);
                _liveSpeed = _usage.Live;
                _todayTotals = _usage.TodayTotals(Today());
                Publish();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _liveSpeed = NoSpeed;
                _log($"Smart routing: stats poll failed: {ex.Message}");
                SafePublish();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Smart routing is not running: count per-application traffic from Windows tracing instead. Under the gate.
    void MeasureWithoutSingBox()
    {
        if (_traffic is null) return;
        if (!_usageWatched)
        {
            // Nobody is looking at the numbers: no per-packet tracing. Ending it marks a gap, since the bytes after this are not counted.
            StopMeasuring(noteGap: true);
            _traffic.Stop();
            return;
        }
        try
        {
            if (_measuring && !_traffic.IsRunning) StopMeasuring(noteGap: true); // the trace ended by itself
            if (_traffic.EnsureRunning() is { } error)
            {
                FailMeasuring(error);
                return;
            }
            _trafficError = null;

            var before = _liveSpeed;
            var wasMeasuring = _measuring;
            _measuring = _isMeasuring = true;
            var drained = _traffic.Drain();
            _usage.AddMeasured(drained, _attribution, PhoneAdapterBytes(), _time.GetLocalNow());
            _liveSpeed = _usage.Live;
            _todayTotals = _usage.TodayTotals(Today());
            if (drained.Count > 0 || _liveSpeed != before || !wasMeasuring) SafePublish(); // a quiet poll changes nothing to show
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailMeasuring(ex.Message);
        }
    }

    void FailMeasuring(string message)
    {
        if (message != _trafficError) _log($"Usage tracking without Smart routing is unavailable: {message}"); // once per distinct reason: this runs every second
        _trafficError = message;
        StopMeasuring(noteGap: true);
    }

    void StopMeasuring(bool noteGap)
    {
        if (!_measuring) return;
        _measuring = _isMeasuring = false;
        if (noteGap) _usage.NoteGap(Today()); // the bytes since the last poll were not recorded
        _usage.ClearRates();
        _liveSpeed = NoSpeed;
        SafePublish();
    }

    void DiscardMeasured()
    {
        if (_traffic is null) return;
        _measuring = _isMeasuring = false;
        try
        {
            _traffic.Drain();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Smart routing: dropping measured traffic failed: {ex.Message}");
        }
        _traffic.Stop(); // sing-box counts the usage now: the trace would only cost CPU
    }

    /// The phone adapter's Windows byte counter, or null when it cannot be read (missed bytes are then split by the default exit).
    long? PhoneAdapterBytes()
    {
        if (_counters is null || _net?.Adapters.Phone?.Name is not { } name) return null;
        try
        {
            var bytes = _counters.TotalBytes(name);
            _adapterReadFailed = false;
            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_adapterReadFailed) _log($"Smart routing: reading the phone adapter's byte counter failed: {ex.Message}");
            _adapterReadFailed = true; // logged once per failure streak: this runs every second
            return null;
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
            _liveSpeed = NoSpeed;
            _measuring = _isMeasuring = false;
            _traffic?.Dispose();
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
                // The crash reason is a FATAL line; later ERROR lines are usually just LAN-only dial timeouts from an outage.
                var lastError = _recentLines.LastOrDefault(l => l.Contains("FATAL")) ?? _recentLines.LastOrDefault(l => l.Contains("ERROR"));
                RecordCrash($"sing-box exited unexpectedly (code {code}){(lastError is null ? "" : ": " + lastError)}",
                    lastError ?? $"sing-box exited with code {code}");
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
        if (net.Mode == RoutingMode.Auto) return (SmartState.Unavailable, "Paused in Auto mode"); // the widget steps aside
        if (SmartRoutingPolicy.WantedExit(net) is null) return (SmartState.Unavailable, "No usable internet connection");
        return (SmartState.Running, null);
    }

    void RefreshBindings(NetworkStatus net, AppSettings settings)
    {
        foreach (var id in Connections.All)
        {
            var identity = id switch
            {
                ConnectionId.Phone => settings.Overrides.PhoneDescription,
                ConnectionId.Lan => settings.Overrides.LanMac,
                ConnectionId.Wifi => settings.Overrides.WifiMac,
                _ => throw new ArgumentOutOfRangeException(nameof(id)),
            };
            if (_bindingIdentities.TryGetValue(id, out var old) && old != identity)
            {
                _lastNames.Remove(id);
                if (id == ConnectionId.Lan) _lastLanDns = null;
            }
            _bindingIdentities[id] = identity;
            if (!net.Adapters.IsUsed(id)) continue;
            if (net.Adapters.Adapter(id) is { } adapter)
            {
                _lastNames[id] = adapter.Name;
                if (id == ConnectionId.Lan) _lastLanDns = adapter.DnsServer ?? _lastLanDns;
            }
            // The persisted name has no saved choice identity. It is safe only for legacy automatic selection;
            // an explicit missing choice must wait until its own live adapter establishes the cache.
            else if (id == ConnectionId.Lan && identity is null && !_lastNames.ContainsKey(id)
                && settings.SmartRouting.LastLanInterface is { } saved) _lastNames[id] = saved;
        }
        var names = new Dictionary<ConnectionId, string>();
        foreach (var id in Connections.All.Where(net.Adapters.IsUsed))
        {
            var live = net.Adapters.Adapter(id)?.Name;
            var cached = _lastNames.GetValueOrDefault(id);
            // A stale name must not bind a different current connection (especially the Phone).
            if (live is null && net.Adapters.Present
                .Concat(net.Adapters.OffAdapters.Select(pair => (Id: pair.Key, Adapter: pair.Value)))
                .Any(pair => pair.Id != id && string.Equals(pair.Adapter.Name, cached, StringComparison.OrdinalIgnoreCase))) cached = null;
            names[id] = live ?? cached ?? (id == ConnectionId.Lan ? LanPlaceholder : $"NetRoute-{SmartRoutingPolicy.Tag(id)}-not-connected");
        }
        _interfaces = names;
    }

    string? LanDns() => _interfaces.ContainsKey(ConnectionId.Lan) ? _lastLanDns : null;
    HashSet<ConnectionId> Answering() => _net is null ? [] : Connections.All.Where(id => _net.Adapters.IsUsed(id)
        && _net.Adapters.Adapter(id) is { HasGateway: true, IPv4: not null }
        && _net.LatencyMs.GetValueOrDefault(id) is not null).ToHashSet();
    ConnectionId? NormalSelection(RuleTarget target) => _net is null ? null : SmartRoutingPolicy.Select(
        target, SmartRoutingPolicy.ProfileOf(_net), _wantedExit, _net.Adapters, Answering(), false);
    bool UsingMain(RuleTarget target) => _targets[target].PermittedMain == _wantedExit;
    IReadOnlyDictionary<RuleTarget, IReadOnlyList<ConnectionId>> CandidateMap() => Enum.GetValues<RuleTarget>().ToDictionary(target => target,
        target => SmartRoutingPolicy.Candidates(target, SmartRoutingPolicy.ProfileOf(_net), _wantedExit, _net!.Adapters, UsingMain(target)));
    IReadOnlyDictionary<RuleTarget, ConnectionId?> SelectedMap() => Enum.GetValues<RuleTarget>().ToDictionary(target => target,
        target => SmartRoutingPolicy.Select(target, SmartRoutingPolicy.ProfileOf(_net), _wantedExit, _net!.Adapters, Answering(), UsingMain(target)));

    string KeyFor(NetworkStatus net) => System.Text.Json.JsonSerializer.Serialize(new
    {
        Profile = SmartRoutingPolicy.ProfileOf(net), Interfaces = _interfaces, Dns = LanDns(), _rules.Fingerprint,
        Candidates = CandidateMap(), Overrides = _targets.Select(pair => new { pair.Key, pair.Value.PermittedMain }),
    });

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
        _usage.ResetBaselines(); // a new sing-box starts from zero totals and new connection ids (DropApi only resets when one was running)
        _measuring = _isMeasuring = false;
        var net = _net!;
        try
        {
            var port = _freePort();
            var secret = Guid.NewGuid().ToString("N");
            var config = SingBoxConfigBuilder.Build(new SingBoxConfigInput(
                _rules, _interfaces, LanDns(), _wantedExit, CandidateMap(), SelectedMap(), port, secret));
            _tracker = new RuleWaitTracker(config.RuleIndexToEntryId, config.RuleIndexToTarget, _time);
            _api = _createApi(port, secret);
            _log($"Smart routing: starting sing-box ({_rules.Entries.Count} rules, exit {_wantedExit})");
            await _host.StartAsync(config.Json, ct).ConfigureAwait(false);
            _apiGraceUntil = _time.GetUtcNow() + ApiStartGrace;
            _runningKey = KeyFor(net);
            _appliedExit = _wantedExit;
            _appliedTargets.Clear();
            foreach (var (target, selected) in SelectedMap())
            {
                _appliedTargets[target] = selected;
                _targets[target].AppliedUseMain = UsingMain(target);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runningKey = null;
            DropApi();
            RecordCrash($"sing-box could not start: {ex.Message}", ex.Message);
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
        if (_api is not null)
        {
            _usage.NoteGap(Today()); // a running sing-box went away: the bytes just before that were not recorded
            _usage.ResetBaselines(); // the next sing-box starts from zero totals and new connection ids
            _liveSpeed = NoSpeed;
            DiscardMeasured(); // sing-box's own packets since the last poll: the API counted them under their applications
        }
        // Without a running sing-box nothing needs resetting: ApplyAsync calls this every 5-10 s while Smart is off, and
        // resetting then would drop the Windows adapter baseline and the live speed that tracing keeps.
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
        if (_appliedExit != _wantedExit && await _api.SelectAsync(SingBoxConfigBuilder.DefaultTag, SmartRoutingPolicy.Tag(_wantedExit), ct).ConfigureAwait(false))
            _appliedExit = _wantedExit;
        foreach (var (target, selected) in SelectedMap())
        {
            if (_appliedTargets.TryGetValue(target, out var applied) && applied == selected) continue;
            if (await _api.SelectAsync(SmartRoutingPolicy.SelectorTag(target), selected is { } id ? SmartRoutingPolicy.Tag(id) : SingBoxConfigBuilder.UnavailableTag, ct).ConfigureAwait(false))
            {
                _appliedTargets[target] = selected;
                _targets[target].AppliedUseMain = UsingMain(target);
            }
        }
    }

    void ResetPermissions()
    {
        foreach (var target in Enum.GetValues<RuleTarget>())
            _targets[target] = new TargetState { ActionGeneration = ++_nextActionGeneration };
        foreach (var target in Enum.GetValues<RuleTarget>()) _tracker?.Clear(target);
    }

    void UpdateTargetHealth(NetworkStatus net)
    {
        var answering = Answering();
        foreach (var (target, state) in _targets)
        {
            var blocked = NormalSelection(target) is null;
            if (blocked && !state.Blocked)
            {
                state.ActionGeneration = ++_nextActionGeneration;
                state.PopupRaised = state.KeepWaiting = false;
                state.OfflineSince = _time.GetUtcNow();
                _tracker?.Clear(target);
            }
            state.Blocked = blocked;
            var healthy = target switch
            {
                RuleTarget.Home => SmartRoutingPolicy.Candidates(target, SmartRoutingPolicy.ProfileOf(net), _wantedExit, net.Adapters, false).Any(answering.Contains),
                RuleTarget.Lan => answering.Contains(ConnectionId.Lan),
                RuleTarget.Wifi => answering.Contains(ConnectionId.Wifi),
                RuleTarget.Phone => answering.Contains(ConnectionId.Phone),
                _ => throw new ArgumentOutOfRangeException(nameof(target)),
            };
            if (!healthy)
            {
                state.HealthyChecks = 0;
                state.OfflineSince ??= _time.GetUtcNow();
                continue;
            }
            if (++state.HealthyChecks < LanHealthyChecksToRevert) continue;
            state.OfflineSince = null;
            state.PopupRaised = state.KeepWaiting = false;
            _tracker?.Clear(target);
            if (state.PermittedMain is null) continue;
            state.PermittedMain = null;
            state.PendingBack = true;
        }
    }

    void NotifyAppliedRecovery()
    {
        if (!_host.IsRunning || _net is null || _runningKey != KeyFor(_net)) return;
        foreach (var (target, selected) in SelectedMap())
        {
            var state = _targets[target];
            if (!state.PendingBack || state.AppliedUseMain || !_appliedTargets.TryGetValue(target, out var applied) || applied != selected) continue;
            state.PendingBack = false;
            Raise(Notify, $"{TargetLabel(target)} back — exception traffic restored");
        }
    }

    public static string TargetLabel(RuleTarget target) => target switch
    {
        RuleTarget.Home => "Home", RuleTarget.Lan => "LAN", RuleTarget.Wifi => "Wi-Fi", RuleTarget.Phone => "Phone",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
    };

    void RecordCrash(string message, string? detail = null)
    {
        _lastCrashDetail = CleanDetail(detail ?? message);
        var now = _time.GetUtcNow();
        _crashes.Add(now);
        _crashes.RemoveAll(t => now - t > CrashWindow);
        _log("Smart routing: " + message);
        if (_crashes.Count < CrashLimit || _faulted) return;
        _faulted = true;
        _log("Smart routing: " + FaultMessage);
        Raise(Notify, FaultMessage);
    }

    /// One short line for the card: no colour codes, no log timestamp or connection id, at most DetailMaxLength long.
    static string CleanDetail(string raw)
    {
        var text = AnsiCodes.Replace(raw, "");
        text = TimestampPrefix.Replace(text, "");
        text = ConnectionIdPattern.Replace(text, "");
        text = LevelCounter.Replace(text, " ");
        text = Whitespace.Replace(text, " ").Trim();
        return text.Length <= DetailMaxLength ? text : text[..(DetailMaxLength - 1)] + "…";
    }

    void Publish()
    {
        var (desired, message) = Desired();
        // Not wanted but still alive: a stop failed. Say so instead of claiming the stop worked.
        if (desired != SmartState.Running && _host.IsRunning) message = "sing-box could not be stopped (see log)";
        var applied = _host.IsRunning && _runningKey is not null && (_net is null || _runningKey == KeyFor(_net));
        var state = desired == SmartState.Running && !applied ? SmartState.Starting : desired;
        var targets = new List<WaitingTarget>();
        foreach (var target in _rules.Entries.Select(entry => entry.Target).Distinct().Order())
        {
            var pending = _targets[target];
            var usingMain = state == SmartState.Running && pending.AppliedUseMain
                && _appliedTargets.GetValueOrDefault(target) == _wantedExit;
            var waiting = NormalSelection(target) is null;
            IReadOnlyList<string> names = state == SmartState.Running && waiting && !usingMain && _tracker is not null
                ? _tracker.RecentFailures(target, WaitWindow).Select(id => _rules.Entries.FirstOrDefault(entry => entry.Id == id)?.Name ?? id).ToList() : [];
            targets.Add(new WaitingTarget(target, _wantedExit, names, usingMain,
                state == SmartState.Running && waiting && !usingMain, pending.ActionGeneration));
        }
        Status = new SmartRoutingStatus(state, message, _appliedExit, targets, _rules.Entries.Count,
            _usage.TodayKept(Today()), state == SmartState.Faulted ? _lastCrashDetail : null, SmartRoutingPolicy.ProfileOf(_net));
        Raise(StatusChanged, Status);
        foreach (var target in targets)
        {
            var pending = _targets[target.Target];
            if (target.Names.Count == 0 || pending.PopupRaised || pending.KeepWaiting
                || pending.OfflineSince is not { } since || _time.GetUtcNow() - since < LanOfflinePopupDelay) continue;
            pending.PopupRaised = true;
            Raise(WaitingDetected, target);
        }
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
