using System.Net;
using System.Net.Sockets;

namespace NetRoute.Core.PathMap;

public sealed record DeviceSnapshot(
    string Key, string Name, string Address, IPAddress? Resolved, bool IsRouter, bool NameNotFound, IReadOnlyList<Sample> Samples);

public sealed record LocalSnapshot(DateTimeOffset At, AdapterInfo? Lan, IPAddress? Source, IReadOnlyList<DeviceSnapshot> Devices);

/// Pings the router and the user's devices once a second from the LAN adapter's address while it runs, and keeps the last 30 s of
/// samples per device. Hands out immutable snapshots; it never touches the UI. The round is public so tests can drive it without timers.
public sealed class LocalNetworkMonitor
{
    public const string RouterKey = "router";
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(1);
    /// A hostname that did not resolve is asked again after this long; one that did is asked again after ResolveRefresh.
    public static readonly TimeSpan ResolveRetry = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ResolveRefresh = TimeSpan.FromMinutes(1);
    /// Rounds start on a fixed rate even while earlier ones wait for a timeout; this many may be outstanding.
    public const int MaxOutstandingRounds = 3;
    const int MaxLoggedMessages = 20;

    readonly IPathProbe _probe;
    readonly Func<string, Task<IReadOnlyList<IPAddress>>> _resolve;
    readonly TimeProvider _time;
    readonly PathThresholds _t;
    readonly Action<string> _log;
    readonly Lock _gate = new();
    readonly HashSet<string> _logged = [];
    AdapterInfo? _lan;
    IPAddress? _source;
    List<Dev> _devices = [];
    /// Bumped when the LAN adapter or its address changes, so a probe that was in flight for the old state can be recognised and dropped.
    int _generation;

    public LocalNetworkMonitor(
        IPathProbe probe, Func<string, Task<IReadOnlyList<IPAddress>>> resolve, TimeProvider time, PathThresholds thresholds, Action<string> log)
    {
        _probe = probe;
        _resolve = resolve;
        _time = time;
        _t = thresholds;
        _log = log;
    }

    sealed class Dev(string key, string name, string address, bool isRouter, PathThresholds t)
    {
        public string Key { get; } = key;
        public string Name { get; set; } = name;
        public string Address { get; } = address;
        public bool IsRouter { get; } = isRouter;
        public PathSamples Samples { get; } = new(t.Window);
        public IPAddress? Resolved;
        /// When the name was last asked about (successfully or not).
        public DateTimeOffset ResolvedAt = DateTimeOffset.MinValue;
        public bool NameNotFound;
    }

    public static string KeyOf(string address) => "dev:" + address.Trim().ToLowerInvariant();

    /// The network to watch: the LAN adapter, its gateway (the router row) and the user's devices. A different adapter or address clears
    /// everything; otherwise devices keep their samples, new ones start empty and removed ones go.
    public void SetNetwork(AdapterInfo? lan, IPAddress? gateway, IReadOnlyList<LocalDevice> devices)
    {
        lock (_gate)
        {
            var source = lan?.IPv4 is { } text && IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
            var unchanged = lan?.Index == _lan?.Index && Equals(source, _source);
            _lan = lan;
            if (!unchanged)
            {
                _source = source;
                _generation++;
                _devices = [];
            }

            var next = new List<Dev>();
            if (gateway is not null && source is not null)
            {
                var existing = _devices.FirstOrDefault(d => d.IsRouter && d.Address == gateway.ToString());
                next.Add(existing ?? new Dev(RouterKey, "Router", gateway.ToString(), true, _t));
            }
            if (source is not null)
            {
                foreach (var device in devices)
                {
                    var key = KeyOf(device.Address);
                    var existing = _devices.FirstOrDefault(d => d.Key == key);
                    if (existing is not null)
                    {
                        existing.Name = device.Name;
                        next.Add(existing);
                    }
                    else next.Add(new Dev(key, device.Name, device.Address.Trim(), false, _t));
                }
            }
            _devices = next;
        }
    }

    /// One echo per device, in parallel.
    public async Task RoundAsync(CancellationToken ct)
    {
        var work = new List<Task>();
        lock (_gate)
        {
            if (_source is not { } source) return;
            var generation = _generation;
            foreach (var dev in _devices) work.Add(ProbeAsync(dev, generation, source, ct));
        }
        await Task.WhenAll(work);
    }

    async Task ProbeAsync(Dev dev, int generation, IPAddress source, CancellationToken ct)
    {
        // A sample belongs to the moment its probe was sent, not the moment it finished.
        var sentAt = _time.GetUtcNow();
        var target = await TargetOf(dev, generation);
        if (ct.IsCancellationRequested) return; // the window is closing: a cancelled check is discarded, not stored
        if (target is null)
        {
            Store(dev, generation, new Sample(sentAt, ProbeOutcome.Unavailable)); // a name that does not resolve says nothing about the network
            return;
        }
        var result = await Guard(() => _probe.EchoAsync(source, target, _t.DirectTtl, EchoTimeout, ct), new EchoResult(ProbeOutcome.Unavailable));
        if (ct.IsCancellationRequested) return;
        Store(dev, generation, new Sample(sentAt, result.Outcome, result.RttMs));
    }

    void Store(Dev dev, int generation, Sample sample)
    {
        lock (_gate)
        {
            if (generation != _generation || !_devices.Contains(dev)) return; // the network changed or the device was removed meanwhile
            dev.Samples.Add(sample);
        }
    }

    /// The address to ping: an IPv4 literal as typed, or the name resolved (cached; a failed name is retried after ResolveRetry).
    async Task<IPAddress?> TargetOf(Dev dev, int generation)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (IPAddress.TryParse(dev.Address, out var literal) && literal.AddressFamily == AddressFamily.InterNetwork)
            {
                dev.Resolved = literal;
                dev.NameNotFound = false;
                return literal;
            }
            if (dev.Resolved is not null && now - dev.ResolvedAt < ResolveRefresh) return dev.Resolved;
            if (dev.Resolved is null && now - dev.ResolvedAt < ResolveRetry) return null;
        }

        IReadOnlyList<IPAddress> found;
        try
        {
            found = await _resolve(dev.Address);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogOnce(ex.Message);
            found = [];
        }
        var address = found.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        lock (_gate)
        {
            if (generation == _generation)
            {
                dev.ResolvedAt = now;
                dev.Resolved = address;
                dev.NameNotFound = address is null;
            }
        }
        return address;
    }

    public LocalSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return new LocalSnapshot(now, _lan, _source, _devices
                .Select(d => new DeviceSnapshot(d.Key, d.Name, d.Address, d.Resolved, d.IsRouter, d.NameNotFound, d.Samples.Snapshot(now)))
                .ToList());
        }
    }

    /// Runs until cancelled, then waits for the rounds still out (they watch the same token). Fixed rate: a new round starts every
    /// period whether or not the previous one has finished, so the 30 s window fills at the stated rate during an outage.
    public async Task RunAsync(CancellationToken ct)
    {
        var rounds = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tick = Task.Delay(Period, _time, ct);
                rounds.RemoveAll(t => t.IsCompleted);
                if (rounds.Count < MaxOutstandingRounds) rounds.Add(Launched(() => RoundAsync(ct)));
                await tick;
            }
        }
        catch (OperationCanceledException) { }
        finally { await Task.WhenAll(rounds); }
    }

    /// Starts work without awaiting it. The returned task never faults: an exception is logged (once per message) and the loop carries on.
    async Task Launched(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogOnce(ex.Message); }
    }

    /// A probe must not throw for a failed measurement; if one does, the sample is Unavailable (never loss) and the problem is logged once.
    async Task<T> Guard<T>(Func<Task<T>> probe, T onError)
    {
        try { return await probe(); }
        catch (OperationCanceledException) { return onError; }
        catch (Exception ex)
        {
            LogOnce(ex.Message);
            return onError;
        }
    }

    void LogOnce(string message)
    {
        bool fresh;
        lock (_gate) fresh = _logged.Count < MaxLoggedMessages && _logged.Add(message);
        if (fresh) _log($"Local network: a check failed to run: {message}");
    }
}
