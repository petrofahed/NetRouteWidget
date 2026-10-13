using System.Net;

namespace NetRoute.Core.PathMap;

/// Probes both connections while it runs: a trace now and then, one echo per device per second, one internet check per lane
/// every two seconds. Holds only the last 30 s of samples and hands out immutable snapshots; it never touches the UI.
/// The rounds are public so tests can drive them without timers.
public sealed class PathMonitor
{
    public static readonly TimeSpan HopPeriod = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan EndpointPeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetraceEvery = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan RetraceMinGap = TimeSpan.FromSeconds(10);

    readonly IPathProbe _hopProbe;
    readonly IEndpointProbe _endpointProbe;
    readonly PathTracer _tracer;
    readonly TimeProvider _time;
    readonly PathThresholds _t;
    readonly Action<string> _log;
    readonly Lock _gate = new();
    readonly Lane _lan, _phone;
    DateTimeOffset? _lastSampleAt;
    string? _lastError;

    public PathMonitor(IPathProbe hopProbe, IEndpointProbe endpointProbe, TimeProvider time, PathThresholds thresholds, Action<string> log)
    {
        _hopProbe = hopProbe;
        _endpointProbe = endpointProbe;
        _tracer = new PathTracer(hopProbe, thresholds);
        _time = time;
        _t = thresholds;
        _log = log;
        _lan = new Lane("LAN", thresholds);
        _phone = new Lane("Phone", thresholds);
    }

    sealed class Lane(string name, PathThresholds t)
    {
        public string Name { get; } = name;
        public AdapterInfo? Adapter;
        public IPAddress? Source;
        public PathTrace? Trace;
        public List<PathSamples> Hops = [];
        public PathSamples Endpoint = new(t.Window);
        /// Bumped whenever the lane is reset, so a probe that was in flight for the old state can be recognised and dropped.
        public int Generation;
        public bool NeedsRetrace = true;
        public DateTimeOffset LastTrace = DateTimeOffset.MinValue;
    }

    /// The adapters to watch. A different adapter or address clears that lane and schedules a new trace.
    public void SetAdapters(AdapterInfo? lan, AdapterInfo? phone)
    {
        lock (_gate)
        {
            Apply(_lan, lan);
            Apply(_phone, phone);
        }
    }

    void Apply(Lane lane, AdapterInfo? adapter)
    {
        var source = adapter?.IPv4 is { } text && IPAddress.TryParse(text, out var ip) ? ip : null;
        if (adapter?.Index == lane.Adapter?.Index && Equals(source, lane.Source)) return;
        lane.Adapter = adapter;
        lane.Source = source;
        lane.Generation++;
        lane.Trace = null;
        lane.Hops = [];
        lane.Endpoint = new PathSamples(_t.Window);
        lane.NeedsRetrace = true;
        lane.LastTrace = DateTimeOffset.MinValue;
    }

    /// Traces every lane that has an address and is due. Pass force to trace regardless.
    public async Task RetraceAsync(CancellationToken ct, bool force = false)
    {
        var due = new List<(Lane Lane, int Generation, IPAddress Source)>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var lane in new[] { _lan, _phone })
                if (lane.Source is { } source && (force || RetraceDue(lane, now))) due.Add((lane, lane.Generation, source));
        }

        await Task.WhenAll(due.Select(async d =>
        {
            var trace = await Guard<PathTrace?>(async () => await _tracer.TraceAsync(d.Source, PathTracer.DefaultTarget, PathTracer.DefaultTimeout, ct), null);
            if (trace is null || ct.IsCancellationRequested) return;
            lock (_gate)
            {
                if (d.Generation != d.Lane.Generation) return;
                var sameHops = d.Lane.Trace is { } old && old.Hops.Select(h => h.Address?.ToString()).SequenceEqual(trace.Hops.Select(h => h.Address?.ToString()));
                if (!sameHops) d.Lane.Hops = trace.Hops.Select(_ => new PathSamples(_t.Window)).ToList();
                d.Lane.Trace = trace;
                d.Lane.NeedsRetrace = trace.Status == TraceStatus.Unavailable;
                d.Lane.LastTrace = _time.GetUtcNow();
            }
        }));
    }

    bool RetraceDue(Lane lane, DateTimeOffset now) =>
        lane.Trace is null || now - lane.LastTrace >= (lane.NeedsRetrace ? RetraceMinGap : RetraceEvery);

    /// One echo per non-hidden device of each traced lane, in parallel.
    public async Task HopRoundAsync(CancellationToken ct)
    {
        var work = new List<Task>();
        lock (_gate)
        {
            foreach (var lane in new[] { _lan, _phone })
            {
                if (lane.Trace is not { } trace || lane.Source is not { } source) continue;
                var generation = lane.Generation;
                var samples = lane.Hops;
                for (var i = 0; i < trace.Hops.Count && i < samples.Count; i++)
                {
                    var hop = trace.Hops[i];
                    if (hop.Hidden) continue;
                    var store = samples[i];
                    work.Add(ProbeHop(lane, generation, trace, source, hop, store, ct));
                }
            }
        }
        await Task.WhenAll(work);
    }

    async Task ProbeHop(Lane lane, int generation, PathTrace trace, IPAddress source, PathHop hop, PathSamples store, CancellationToken ct)
    {
        // A sample belongs to the moment its probe was sent, not the moment it finished: a failed check can take seconds,
        // and the hop and internet series are lined up by time.
        var sentAt = _time.GetUtcNow();
        var result = await Guard(() => _hopProbe.EchoAsync(source, trace.Target, hop.Ttl, PathTracer.DefaultTimeout, ct),
            new EchoResult(ProbeOutcome.Unavailable));
        if (ct.IsCancellationRequested) return; // the window is closing: a cancelled check is discarded, not stored
        lock (_gate)
        {
            if (generation != lane.Generation || !ReferenceEquals(lane.Trace, trace)) return; // the lane was reset or re-traced meanwhile
            store.Add(new Sample(sentAt, result.Outcome, result.RttMs));
            _lastSampleAt = _time.GetUtcNow(); // when data last arrived, not when it was sent
            if (result.Outcome == ProbeOutcome.Reply && PathChanged(trace, hop, result)) lane.NeedsRetrace = true;
        }
    }

    /// A reply from another address, from the target at a TTL that used to be a device, or from a hop that used to be silent.
    static bool PathChanged(PathTrace trace, PathHop hop, EchoResult result) =>
        result.TargetReached || hop.Address is null || !Equals(result.From, hop.Address);

    /// One internet check per connected lane.
    public async Task EndpointRoundAsync(CancellationToken ct)
    {
        var work = new List<Task>();
        lock (_gate)
        {
            foreach (var lane in new[] { _lan, _phone })
            {
                if (lane.Source is not { } source) continue;
                var generation = lane.Generation;
                var store = lane.Endpoint;
                work.Add(CheckEndpoint(lane, generation, source, store, ct));
            }
        }
        await Task.WhenAll(work);
    }

    async Task CheckEndpoint(Lane lane, int generation, IPAddress source, PathSamples store, CancellationToken ct)
    {
        // Stamped when sent: a failed TCP check can take up to 4 s, and must still land in the 2 s bucket it belongs to.
        var sentAt = _time.GetUtcNow();
        var result = await Guard(() => _endpointProbe.CheckAsync(source, ct), new EndpointResult(ProbeOutcome.Unavailable));
        if (ct.IsCancellationRequested) return;
        lock (_gate)
        {
            if (generation != lane.Generation) return;
            store.Add(new Sample(sentAt, result.Outcome, result.RttMs));
            _lastSampleAt = _time.GetUtcNow(); // when data last arrived, not when it was sent
        }
    }

    /// A probe must not throw for a failed measurement; if one does, the sample is Unavailable (never loss) and the problem is logged once.
    async Task<T> Guard<T>(Func<Task<T>> probe, T onError)
    {
        try { return await probe(); }
        catch (OperationCanceledException) { return onError; }
        catch (Exception ex)
        {
            if (_lastError != ex.Message)
            {
                _lastError = ex.Message;
                _log($"Path map: a check failed to run: {ex.Message}");
            }
            return onError;
        }
    }

    public PathSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            return new PathSnapshot(now, Of(_lan, now), Of(_phone, now), _lastSampleAt);
        }
    }

    LaneSnapshot Of(Lane lane, DateTimeOffset now)
    {
        var hopSamples = lane.Trace is { } trace
            ? trace.Hops.Select((_, i) => i < lane.Hops.Count ? lane.Hops[i].Snapshot(now) : []).ToList()
            : [];
        return new LaneSnapshot(lane.Name, lane.Adapter, lane.Trace, hopSamples, lane.Endpoint.Snapshot(now));
    }

    /// Runs both loops until cancelled. The hop loop also retraces when due; the internet check has its own loop because it can take longer than a second.
    public async Task RunAsync(CancellationToken ct)
    {
        try { await Task.WhenAll(HopLoop(ct), EndpointLoop(ct)); }
        catch (OperationCanceledException) { }
    }

    async Task HopLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RetraceAsync(ct);
            await HopRoundAsync(ct);
            await Task.Delay(HopPeriod, _time, ct);
        }
    }

    async Task EndpointLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await EndpointRoundAsync(ct);
            await Task.Delay(EndpointPeriod, _time, ct);
        }
    }
}
