using System.Net;

namespace NetRoute.Core;

/// Orchestrates detect → apply metrics → resolve path → publish status.
/// Work is serialised by a gate, so it is safe to call from timers, network events and the UI.
/// Events are raised on the calling thread; UI subscribers must marshal to their dispatcher.
public sealed class RouteController
{
    public static readonly IPAddress ProbeTargetV4 = IPAddress.Parse("1.1.1.1");
    public static readonly IPAddress ProbeTargetV6 = IPAddress.Parse("2606:4700:4700::1111");

    readonly IAdapterSource _adapters;
    readonly IRouteQuery _routes;
    readonly ILatencyProbe _probe;
    readonly RoutingEngine _engine;
    readonly Action<AppSettings> _persist;
    readonly Action<string> _log;
    readonly TimeProvider _time;
    readonly RoutePin? _pin;
    readonly INetworkNameSource? _names;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Lock _settingsLock = new();
    AppSettings _settings;
    bool _hasRefreshed;
    bool _userChangePending;

    // Wi-Fi network name lookup is cached by (Index, IPv4) so the WMI query only runs again after a
    // reconnect (a changed IPv4), not on every ~5 s poll tick while nothing about the connection changed.
    (int Index, string? IPv4)? _lastWifiKey;
    string? _wifiNetworkName;

    /// The preferred adapter must fail this long without a single good check (while a candidate answers) before
    /// healing. Short blips on a flaky link never move traffic onto the metered phone connection.
    public static readonly TimeSpan HealAfterFailingFor = TimeSpan.FromSeconds(60);

    /// Consecutive failed checks of the preferred adapter that must also have happened, so a long gap between two
    /// checks (a sleeping PC) is not read as a long outage.
    public const int HealAfterFailedChecks = 3;

    /// A longer pause between two measured checks (the PC slept) restarts the failure window: misses before the pause
    /// say nothing about the link after it.
    public static readonly TimeSpan MaxGapBetweenChecks = TimeSpan.FromSeconds(30);

    /// Consecutive good checks before healing ends (recovery to main) or a higher-ranked candidate is stepped up to.
    public const int RecoverAfterGoodChecks = 3;

    /// Upper limit for the good checks needed before healing ends, however often healing repeats.
    public const int MaxRecoverAfterGoodChecks = 48;

    /// Measured checks without healing, after a heal ended, that return the backoff to the base rule.
    public const int CalmChecksToResetBackoff = 60;

    /// Consecutive missed latency checks before the card shows "no reply"; a single miss keeps the last latency.
    public const int NoReplyAfterMissedChecks = 2;

    readonly Dictionary<ConnectionId, int> _misses = new();

    // Heal state is read and written only while holding _gate.
    bool _healing;
    ConnectionId? _healTarget;
    int _healCount;
    DateTimeOffset? _failingSince;
    DateTimeOffset? _lastCheckAt;
    int _recoverThreshold = RecoverAfterGoodChecks;
    int _checksSinceHealEnded;
    bool _hadHeal;
    bool _healSticky;

    /// Consecutive good checks per candidate ranked above the current heal target; cleared whenever the target changes.
    readonly Dictionary<ConnectionId, int> _stepUpCounts = new();

    /// Good checks that end healing now (recovery to main); grows while healing keeps repeating.
    internal int RecoverThreshold => _recoverThreshold;

    public RouteController(
        IAdapterSource adapters, IInterfaceMetrics metrics, IRouteQuery routes, ILatencyProbe probe,
        AppSettings settings, bool canModify, Action<AppSettings> persist, Action<string> log, TimeProvider? time = null,
        ISubnetRoutes? subnetRoutes = null, INetworkNameSource? names = null)
    {
        _time = time ?? TimeProvider.System;
        _adapters = adapters;
        _routes = routes;
        _probe = probe;
        _engine = new RoutingEngine(metrics);
        _settings = settings;
        _persist = persist;
        _log = log;
        _pin = subnetRoutes is not null ? new RoutePin(subnetRoutes, log) : null;
        _names = names;
        Status = NetworkStatus.Initial(settings.Mode, canModify);
    }

    public NetworkStatus Status { get; private set; }

    public AppSettings Settings
    {
        get { lock (_settingsLock) return _settings; }
    }

    public event Action<NetworkStatus>? StatusChanged;

    /// Raised for internet-path changes the user did not cause (unplug, replug, lost route).
    public event Action<string>? AutoSwitched;

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        AppSettings updated;
        lock (_settingsLock) updated = _settings = change(_settings);
        _persist(updated);
    }

    /// Lets Smart routing report its own TUN as the phone/LAN path it currently uses, so the card and toasts
    /// don't show "other adapter (VPN?)" while sing-box carries traffic. Null result = not ours, use the default.
    public Func<int, InternetPath?>? ExternalPathResolver { get; set; }

    InternetPath PathFor(int? bestIndex, DetectionResult adapters) =>
        bestIndex is { } index && ExternalPathResolver?.Invoke(index) is { } external
            ? external
            : NetworkStatus.ResolvePath(bestIndex, adapters);

    public async Task RefreshAsync(bool measureLatency, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = Status;
            var detection = AdapterDetector.Detect(_adapters.GetAdapters(), Settings.Overrides, Settings.TurnedOff);
            // Safety net: a mode saved on a connection that is now unused (turned off without admin rights, or a
            // hand-edited file) must not leave every metric at backup level with nothing preferred.
            if (FallBackIfUnused(detection)) ResetHealing();
            var mode = Settings.Mode;
            UpdateWifiNetworkName(detection);

            var rawLatencies = measureLatency
                ? await MeasureAsync(detection, ct)
                : Connections.All.ToDictionary(id => id, id => detection.Adapter(id) is null ? (int?)null : before.LatencyMs.GetValueOrDefault(id));

            var stickyHealStarted = UpdateHealing(mode, detection, rawLatencies, before.CanModify, measureLatency);

            // Healing above used the raw results; the card only reports "no reply" once a miss repeats.
            var shown = measureLatency
                // An adapter that left its slot (unplugged, Not used, turned off) has no latency to keep.
                ? Connections.All.ToDictionary(id => id, id => detection.Adapter(id) is null
                    ? null
                    : Smooth(id, rawLatencies.GetValueOrDefault(id), before.LatencyMs.GetValueOrDefault(id)))
                : rawLatencies;

            var carrier = NetworkStatus.CarrierFor(mode, _healTarget, detection);

            string? error = null;
            if (before.CanModify && !_engine.IsInSync(mode, detection, carrier: _healTarget))
            {
                _log($"Metrics differ from {CarrierName(mode, detection)} metrics; applying");
                var result = _engine.Apply(mode, detection, carrier: _healTarget);
                if (!result.Success)
                {
                    error = result.Error;
                    _log($"Apply {CarrierName(mode, detection)} failed: {result.Error}");
                }
            }

            if (before.CanModify)
            {
                _pin?.Sync(RoutePin.Wanted(mode, _healTarget, detection));
                ResetOffAdapters(detection);
            }

            var after = before with
            {
                Mode = mode,
                IsHealing = _healing,
                IsHealSticky = _healSticky,
                Adapters = detection,
                ActivePath = PathFor(_routes.GetBestInterfaceIndex(ProbeTargetV4), detection),
                Ipv6Path = PathFor(_routes.GetBestInterfaceIndex(ProbeTargetV6), detection),
                LatencyMs = shown,
                Error = error,
                HealTarget = _healTarget,
                Carrier = carrier,
                WifiNetwork = _wifiNetworkName,
            };

            // Right after a user mode change the route table may settle a moment later:
            // that move to the preferred adapter is the user's doing, not worth a toast.
            var settlingAfterUserChange = _userChangePending && !after.IsFallback;
            _userChangePending = false;
            // A heal turning sticky gets its own toast in place of the path-change one: one toast, not two.
            var toast = stickyHealStarted ? StatusPresenter.StickyHealToast(mode, _healTarget!.Value)
                : _hasRefreshed && !settlingAfterUserChange ? StatusPresenter.ToastFor(before, after)
                : null;
            Publish(after, toast);
            _hasRefreshed = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Refresh failed: {ex}");
            Publish(Status with { Error = $"Refresh failed: {ex.Message}" }, toast: null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyResult> SetModeAsync(RoutingMode mode, CancellationToken ct = default)
    {
        if (Connections.Of(mode) is { } wanted && Settings.TurnedOff.Contains(wanted))
            return new ApplyResult(false, $"{Connections.Label(wanted)} is turned off");
        if (!Status.CanModify)
            return new ApplyResult(false, "Administrator rights are needed to change routing");

        await _gate.WaitAsync(ct);
        try
        {
            // Checked under the gate, against a fresh detection: a turn-off or an adapter change queued ahead of this
            // one may have landed meanwhile. A mode with nothing behind it would leave no adapter preferred.
            var detection = AdapterDetector.Detect(_adapters.GetAdapters(), Settings.Overrides, Settings.TurnedOff);
            if (Connections.Of(mode) is { } wantedNow && !detection.IsUsed(wantedNow))
                return new ApplyResult(false, detection.IsTurnedOff(wantedNow)
                    ? $"{Connections.Label(wantedNow)} is turned off"
                    : $"{Connections.Label(wantedNow)} is set to Not used");

            ResetHealing();
            var result = _engine.Apply(mode, detection);
            if (result.Success)
            {
                UpdateSettings(s => s with { Mode = mode });
                _log($"Mode set to {mode}");
            }
            else
            {
                _log($"Mode {mode} failed: {result.Error}");
            }

            // Pin against the mode that actually stuck, not the requested one: if Apply failed, the
            // mode is unchanged, and a pin for the requested-but-rejected mode would be wrong until
            // the next refresh corrected it anyway.
            if (Status.CanModify)
                _pin?.Sync(RoutePin.Wanted(Settings.Mode, _healTarget, detection));

            _userChangePending = true;
            Publish(Status with
            {
                Mode = Settings.Mode,
                IsHealing = _healing,
                IsHealSticky = _healSticky,
                Adapters = detection,
                ActivePath = PathFor(_routes.GetBestInterfaceIndex(ProbeTargetV4), detection),
                Ipv6Path = PathFor(_routes.GetBestInterfaceIndex(ProbeTargetV6), detection),
                Error = result.Error,
                HealTarget = _healTarget,
                // Settings.Mode, not the requested mode: if Apply failed, Mode above is already Settings.Mode
                // (unchanged), and Carrier must agree with it rather than with a rejected mode nothing applied.
                Carrier = NetworkStatus.CarrierFor(Settings.Mode, _healTarget, detection),
            }, toast: null);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log($"Mode {mode} failed: {ex}");
            Publish(Status with { Error = ex.Message }, toast: null);
            return new ApplyResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// Saves the user's adapter choice. An adapter that stops being present in its slot is reset to the
    /// automatic metric so a stale preferred metric cannot keep winning.
    public Task SetOverridesAsync(AdapterOverrides overrides, CancellationToken ct = default) =>
        ChangeAdaptersAsync(
            s => s with
            {
                PhoneOverride = overrides.PhoneDescription, LanOverrideMac = overrides.LanMac, WifiOverrideMac = overrides.WifiMac,
                // A slot set to Not used has no row to turn back on: forget that it was off, so picking it later starts on.
                TurnedOff = AppSettings.OffList(s.TurnedOff.Where(id => !overrides.IsNotUsed(id))),
            },
            $"Adapter overrides: phone={overrides.PhoneDescription ?? "auto"}, lan={overrides.LanMac ?? "auto"}, wifi={overrides.WifiMac ?? "auto"}",
            ct);

    /// Turns a connection off (it then counts as Not used, but keeps its adapter choice and its card row) or back on.
    public Task SetTurnedOffAsync(ConnectionId id, bool off, CancellationToken ct = default) =>
        ChangeAdaptersAsync(
            s => s with
            {
                TurnedOff = AppSettings.OffList(off ? s.TurnedOff.Append(id) : s.TurnedOff.Where(x => x != id)),
            },
            $"{Connections.Label(id)} turned {(off ? "off" : "on")}",
            ct);

    /// Applies a change to which adapters fill the slots (overrides or the off list), then re-detects. An adapter that
    /// leaves its slot is reset to the automatic metric, and a mode left with nothing behind it falls back.
    async Task ChangeAdaptersAsync(Func<AppSettings, AppSettings> change, string logLine, CancellationToken ct)
    {
        void Save()
        {
            UpdateSettings(change);
            _log(logLine);
        }

        if (Status.CanModify)
        {
            await _gate.WaitAsync(ct);
            try
            {
                // Inside the gate: a refresh already queued must still see the old settings,
                // so Status.Adapters below is the set we are replacing, not the new one.
                var previous = Status.Adapters;
                ResetHealing();
                Save();
                var next = AdapterDetector.Detect(_adapters.GetAdapters(), Settings.Overrides, Settings.TurnedOff);

                // E.g. Wi-Fi set to Not used, or the LAN turned off, while in that mode. Done before the reset loop
                // below, so a failure in that loop cannot skip this. (Without admin rights the refresh below does it.)
                FallBackIfUnused(next);

                foreach (var (_, old) in previous.Present)
                {
                    if (next.Present.Any(p => p.Adapter.Index == old.Index)) continue;
                    var reset = _engine.Reset(old);
                    _log(reset.Success ? $"Reset {old.Name} to automatic metric" : $"Reset {old.Name} failed: {reset.Error}");
                }

                // The path change this causes is the user's doing: the refresh below must not toast it.
                _userChangePending = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"Override reset failed: {ex}");
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            Save();
        }

        await RefreshAsync(measureLatency: true, ct);
    }

    /// The connection the saved Mode points at may be unused (Not used, or turned off): fall back to the first used
    /// connection in rank order, or Auto if none is, so the user is never stranded on a mode with nothing behind it.
    /// Auto has no connection (Connections.Of(Auto) is null), so this is a no-op there. Returns whether the mode changed.
    bool FallBackIfUnused(DetectionResult detection)
    {
        if (Connections.Of(Settings.Mode) is not { } main || detection.IsUsed(main)) return false;
        var oldMode = Settings.Mode;
        // A used connection that is present first (in rank order), so the fallback does not land on, say, a chosen
        // but disconnected Wi-Fi and leave the user in a fallback state they did not cause; else any used one.
        var fallback = Connections.FallbackOrder
            .Where(detection.IsUsed)
            .OrderBy(id => detection.Adapter(id) is null ? 1 : 0)
            .Select(id => (ConnectionId?)id)
            .FirstOrDefault();
        var newMode = fallback is { } f ? Connections.ModeOf(f) : RoutingMode.Auto;
        if (newMode == oldMode) return false;
        UpdateSettings(s => s with { Mode = newMode });
        _log($"{oldMode} is no longer used; switching to {newMode}");
        return true;
    }

    /// A turned-off adapter must sit on the automatic metric. Its reset can fail when it is turned off (or a hand-edited
    /// file can name one that still holds a manual metric), and refreshes no longer manage it, so retry here. Writes
    /// only when the adapter is not automatic yet.
    void ResetOffAdapters(DetectionResult detection)
    {
        foreach (var adapter in detection.OffAdapters.Values)
        {
            if (_engine.IsAutomatic(adapter)) continue;
            var reset = _engine.Reset(adapter);
            _log(reset.Success ? $"Reset turned-off {adapter.Name} to automatic metric" : $"Reset turned-off {adapter.Name} failed: {reset.Error}");
        }
    }

    /// How long ReleasePin waits for _gate before giving up and releasing unsynchronized. Short, so a
    /// stuck refresh never hangs shutdown, but long enough to cover an in-flight RefreshAsync/SetModeAsync
    /// (which can hold the gate for the full duration of detection, metric apply and latency probing).
    static readonly TimeSpan ReleasePinGateTimeout = TimeSpan.FromSeconds(2);

    /// Reverts whatever subnet route is currently pinned back to RoutePin.DefaultMetric. Call on shutdown
    /// so a pin never outlives the widget.
    ///
    /// RoutePin._pinned is mutated by Sync, which RefreshAsync/SetModeAsync call while HOLDING _gate; this
    /// must not touch the same RoutePin instance unsynchronized while one of those is in flight, or the two
    /// writes can race. Acquires _gate synchronously (Wait, not WaitAsync - shutdown call sites are not
    /// async) before releasing the pin; if the gate cannot be acquired within ReleasePinGateTimeout, falls
    /// back to a best-effort unsynchronized release rather than hanging shutdown indefinitely, and logs it.
    public void ReleasePin()
    {
        if (_pin is null) return;

        if (_gate.Wait(ReleasePinGateTimeout))
        {
            try { _pin.Release(); }
            finally { _gate.Release(); }
        }
        else
        {
            _log("ReleasePin: could not acquire the controller gate in time; releasing the route pin unsynchronized");
            _pin.Release();
        }
    }

    /// Refreshes `_wifiNetworkName` from `_names`, but only re-queries when the Wi-Fi adapter's
    /// (Index, IPv4) pair has changed since the last lookup - a reconnect gets a fresh name, an
    /// unchanged connection does not re-run the WMI query on every poll tick. No Wi-Fi adapter at
    /// all clears the cache, so a later reconnect (even to the same Index/IPv4 as a past session)
    /// is not served a stale name left over from before the disconnect.
    ///
    /// A null result is NOT cached against the key: the app auto-starts with Windows, so "Wi-Fi
    /// associates while the app is also starting up" is a plausible race where DHCP (which gives
    /// IPv4) finishes slightly before Windows has actually named the connection profile. Settling
    /// for null here forever (until the next reconnect) would defeat the feature for the rest of
    /// the session; instead, a null CLEARS the key (rather than leaving it at whatever it was),
    /// so the very next poll tries again - including a poll that lands back on a key that
    /// previously resolved to a real name (e.g. a DHCP renewal or reconnect briefly changes the
    /// IPv4 and then reverts): leaving the old key in place there would make that returning key
    /// match the stale pre-null cache entry and short-circuit forever on a cached null.
    void UpdateWifiNetworkName(DetectionResult detection)
    {
        if (detection.Wifi is not { } wifi)
        {
            _lastWifiKey = null;
            _wifiNetworkName = null;
            return;
        }

        var key = (wifi.Index, wifi.IPv4);
        if (_lastWifiKey == key) return;

        var name = _names?.NameOf(wifi);
        _wifiNetworkName = name;
        _lastWifiKey = name is null ? null : key;
    }

    static string CarrierName(RoutingMode mode, DetectionResult adapters) =>
        Connections.Of(mode) is { } main ? Name(adapters, main) : mode.ToString();

    static string Name(DetectionResult adapters, ConnectionId id) => adapters.Adapter(id)?.Name ?? Connections.Label(id);

    /// Drops all heal state, including the backoff: an explicit user action or scope loss starts fresh.
    void ResetHealing()
    {
        _healing = false;
        _healTarget = null;
        _healCount = 0;
        _failingSince = null;
        _recoverThreshold = RecoverAfterGoodChecks;
        _checksSinceHealEnded = 0;
        _hadHeal = false;
        _healSticky = false;
        _stepUpCounts.Clear();
    }

    /// Returns true when this check started a sticky heal.
    bool UpdateHealing(RoutingMode mode, DetectionResult adapters, Dictionary<ConnectionId, int?> latencies, bool canModify, bool measured)
    {
        var main = Connections.Of(mode);
        if (!canModify || !Settings.AutoHeal || main is null)
        {
            if (_healing)
                _log(Settings.AutoHeal ? "Auto-heal ended: switching mode or losing control privilege" : "Auto-heal ended: switched off");
            ResetHealing();
            return false;
        }

        bool Answering(ConnectionId id) =>
            adapters.Adapter(id) is { IPv4: not null } && latencies.GetValueOrDefault(id) is not null;

        var candidates = Connections.FallbackOrder.Where(id => id != main.Value && adapters.Adapter(id) is { IPv4: not null }).ToList();

        // No IPv4 yet on the main adapter (e.g. DHCP pending after a replug): nothing was measured for it,
        // so this check is no evidence either way. An absent main adapter is simply "not answering" below.
        if (!measured || adapters.Adapter(main.Value) is { IPv4: null }) return false;

        var checkedAt = _time.GetUtcNow();
        var afterPause = _lastCheckAt is { } previous && checkedAt - previous > MaxGapBetweenChecks;
        _lastCheckAt = checkedAt;

        if (!_healing)
        {
            if (afterPause)
            {
                _healCount = 0;
                _failingSince = null;
            }
            if (_hadHeal && _checksSinceHealEnded < CalmChecksToResetBackoff)
            {
                _checksSinceHealEnded++;
                if (_checksSinceHealEnded == CalmChecksToResetBackoff) _recoverThreshold = RecoverAfterGoodChecks;
            }

            var now = _time.GetUtcNow();
            // Whether to start counting a fresh failure is anchored to the connection the OS is
            // already naturally routing through by metric alone (no heal override): when main's own
            // adapter is entirely absent, that is the next present+routable connection in fallback
            // order, not literal "main". This keeps a plain unplug of main (backup present and
            // healthy) from spuriously starting a heal just because main itself is gone — the natural
            // carrier already IS the backup, and it is answering. Once actively healing, recovery still
            // checks literal main (below), since recovery means "the user's chosen connection is back".
            var naturalCarrier = NetworkStatus.CarrierFor(mode, null, adapters);
            var failing = naturalCarrier is { } nc && !Answering(nc) && candidates.Any(Answering);
            if (failing)
            {
                _healCount++;
                _failingSince ??= now;
            }
            else
            {
                _healCount = 0;
                _failingSince = null;
            }
            if (_healCount < HealAfterFailedChecks || now - _failingSince < HealAfterFailingFor) return false;

            var target = candidates.First(Answering);
            _healing = true;
            _healTarget = target;
            _healCount = 0;
            _failingSince = null;
            _stepUpCounts.Clear();
            if (_hadHeal && _checksSinceHealEnded < CalmChecksToResetBackoff)
            {
                if (_recoverThreshold == MaxRecoverAfterGoodChecks)
                {
                    // Switching back keeps failing even after the longest wait: stop cycling until the user retries.
                    _healSticky = true;
                    _log($"Auto-heal: {Name(adapters, main.Value)} keeps losing internet; staying on {Name(adapters, target)} until the user retries");
                }
                else
                {
                    _recoverThreshold = Math.Min(_recoverThreshold * 2, MaxRecoverAfterGoodChecks);
                    _log($"Auto-heal: repeated failure; waiting for {_recoverThreshold} good checks before switching back");
                }
            }
            _log($"Auto-heal: {Name(adapters, main.Value)} has no internet; preferring {Name(adapters, target)}");
            return _healSticky;
        }

        var currentTarget = _healTarget!.Value;

        // Step 1: has main recovered? Threshold-counted, skipped while sticky. Only ends the heal once the
        // threshold is reached; otherwise it falls through to steps 2/3 on this same tick.
        if (!_healSticky)
        {
            _healCount = Answering(main.Value) ? _healCount + 1 : 0;
            if (_healCount >= _recoverThreshold)
            {
                var mainName = Name(adapters, main.Value);
                EndHeal();
                _log($"Auto-heal ended: {mainName} has internet again");
                return false;
            }
        }

        // Step 2: has the current target stopped answering? Immediate, not threshold-counted, and not gated by sticky.
        if (!Answering(currentTarget))
        {
            ConnectionId? next = null;
            foreach (var id in candidates)
            {
                if (id == currentTarget || !Answering(id)) continue;
                next = id;
                break;
            }
            if (next is { } n)
            {
                _log($"Auto-heal: {Name(adapters, currentTarget)} lost internet; stepping down to {Name(adapters, n)}");
                _healTarget = n;
                _stepUpCounts.Clear();
            }
            else
            {
                var targetName = Name(adapters, currentTarget);
                EndHeal();
                _log($"Auto-heal ended: {targetName} has no internet and nothing else answers");
            }
            return false;
        }

        // Step 3: has a candidate ranked above the current target answered RecoverAfterGoodChecks times in a row?
        var targetRank = Connections.FallbackOrder.ToList().IndexOf(currentTarget);
        ConnectionId? promoted = null;
        foreach (var c in Connections.FallbackOrder.Take(targetRank).Where(id => id != main.Value))
        {
            if (Answering(c))
            {
                var count = _stepUpCounts.GetValueOrDefault(c) + 1;
                _stepUpCounts[c] = count;
                if (promoted is null && count >= RecoverAfterGoodChecks) promoted = c;
            }
            else
            {
                _stepUpCounts[c] = 0;
            }
        }
        if (promoted is { } p)
        {
            _log($"Auto-heal: {Name(adapters, p)} is back; stepping up from {Name(adapters, currentTarget)}");
            _healTarget = p;
            _stepUpCounts.Clear();
        }
        return false;
    }

    /// A heal that ends on its own keeps the backoff, so the next heal soon after waits longer.
    void EndHeal()
    {
        _healing = false;
        _healTarget = null;
        _healCount = 0;
        _hadHeal = true;
        _checksSinceHealEnded = 0;
        _healSticky = false;
        _stepUpCounts.Clear();
    }

    int? Smooth(ConnectionId id, int? measured, int? previous)
    {
        if (measured is not null)
        {
            _misses[id] = 0;
            return measured;
        }
        var misses = _misses.GetValueOrDefault(id) + 1;
        _misses[id] = misses;
        return misses >= NoReplyAfterMissedChecks ? null : previous;
    }

    async Task<Dictionary<ConnectionId, int?>> MeasureAsync(DetectionResult adapters, CancellationToken ct)
    {
        var pending = adapters.Present.Select(p => (p.Id, Task: Probe(p.Adapter, ct))).ToList();
        var result = new Dictionary<ConnectionId, int?>();
        foreach (var (id, task) in pending) result[id] = await task;
        return result;
    }

    Task<int?> Probe(AdapterInfo? adapter, CancellationToken ct) =>
        adapter?.IPv4 is { } ip ? _probe.MeasureAsync(IPAddress.Parse(ip), ct) : Task.FromResult<int?>(null);

    void Publish(NetworkStatus status, string? toast)
    {
        Status = status;
        StatusChanged?.Invoke(status);
        if (toast is null) return;
        _log($"Auto-switch: {toast}");
        AutoSwitched?.Invoke(toast);
    }
}
