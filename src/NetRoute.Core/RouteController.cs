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
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Lock _settingsLock = new();
    AppSettings _settings;
    bool _hasRefreshed;
    bool _userChangePending;

    public RouteController(
        IAdapterSource adapters, IInterfaceMetrics metrics, IRouteQuery routes, ILatencyProbe probe,
        AppSettings settings, bool canModify, Action<AppSettings> persist, Action<string> log)
    {
        _adapters = adapters;
        _routes = routes;
        _probe = probe;
        _engine = new RoutingEngine(metrics);
        _settings = settings;
        _persist = persist;
        _log = log;
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

    public async Task RefreshAsync(bool measureLatency, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var before = Status;
            var mode = Settings.Mode;
            var detection = AdapterDetector.Detect(_adapters.GetAdapters(), Settings.Overrides);

            string? error = null;
            if (before.CanModify && !_engine.IsInSync(mode, detection))
            {
                _log($"Metrics differ from {mode} mode; applying");
                var result = _engine.Apply(mode, detection);
                if (!result.Success)
                {
                    error = result.Error;
                    _log($"Apply {mode} failed: {result.Error}");
                }
            }

            var (phoneMs, lanMs) = measureLatency
                ? await MeasureAsync(detection, ct)
                : (detection.Phone is null ? null : before.PhoneLatencyMs, detection.Lan is null ? null : before.LanLatencyMs);

            var after = before with
            {
                Mode = mode,
                Adapters = detection,
                ActivePath = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV4), detection),
                Ipv6Path = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV6), detection),
                PhoneLatencyMs = phoneMs,
                LanLatencyMs = lanMs,
                Error = error,
            };

            // Right after a user mode change the route table may settle a moment later:
            // that move to the preferred adapter is the user's doing, not worth a toast.
            var settlingAfterUserChange = _userChangePending && !after.IsFallback;
            _userChangePending = false;
            Publish(after, _hasRefreshed && !settlingAfterUserChange ? StatusPresenter.ToastFor(before, after) : null);
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
        if (!Status.CanModify)
            return new ApplyResult(false, "Administrator rights are needed to change routing");

        await _gate.WaitAsync(ct);
        try
        {
            var detection = AdapterDetector.Detect(_adapters.GetAdapters(), Settings.Overrides);
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

            _userChangePending = true;
            Publish(Status with
            {
                Mode = Settings.Mode,
                Adapters = detection,
                ActivePath = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV4), detection),
                Ipv6Path = NetworkStatus.ResolvePath(_routes.GetBestInterfaceIndex(ProbeTargetV6), detection),
                Error = result.Error,
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

    /// Saves the user's adapter choice. An adapter that stops being phone/LAN is reset to the
    /// automatic metric so a stale preferred metric cannot keep winning.
    public async Task SetOverridesAsync(AdapterOverrides overrides, CancellationToken ct = default)
    {
        void SaveOverrides()
        {
            UpdateSettings(s => s with { PhoneOverride = overrides.PhoneDescription, LanOverrideMac = overrides.LanMac });
            _log($"Adapter overrides: phone={overrides.PhoneDescription ?? "auto"}, lan={overrides.LanMac ?? "auto"}");
        }

        if (Status.CanModify)
        {
            await _gate.WaitAsync(ct);
            try
            {
                // Inside the gate: a refresh already queued must still see the old overrides,
                // so Status.Adapters below is the pair we are replacing, not the new one.
                var previous = Status.Adapters;
                SaveOverrides();
                var next = AdapterDetector.Detect(_adapters.GetAdapters(), overrides);
                foreach (var old in new[] { previous.Phone, previous.Lan })
                {
                    if (old is null || old.Index == next.Phone?.Index || old.Index == next.Lan?.Index) continue;
                    var reset = _engine.Reset(old);
                    _log(reset.Success ? $"Reset {old.Name} to automatic metric" : $"Reset {old.Name} failed: {reset.Error}");
                }
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
            SaveOverrides();
        }

        await RefreshAsync(measureLatency: true, ct);
    }

    async Task<(int?, int?)> MeasureAsync(DetectionResult adapters, CancellationToken ct)
    {
        var phone = Probe(adapters.Phone, ct);
        var lan = Probe(adapters.Lan, ct);
        return (await phone, await lan);
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
