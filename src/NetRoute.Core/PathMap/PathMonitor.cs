using System.Net;
using System.Net.Sockets;

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
    /// Rounds are started on a fixed rate even while earlier ones wait for a timeout; this many of one kind may be outstanding.
    public const int MaxOutstandingRounds = 3;
    const int MaxLoggedMessages = 20;

    readonly IPathProbe _hopProbe;
    readonly IEndpointProbe _endpointProbe;
    readonly PathTracer _tracer;
    readonly TimeProvider _time;
    readonly PathThresholds _t;
    readonly Action<string> _log;
    readonly Lock _gate = new();
    readonly Lane _lan, _phone;
    DateTimeOffset? _lastSampleAt;
    readonly HashSet<string> _logged = [];

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
        /// Consecutive retraces that came back worse than the current trace (see IsDegraded).
        public int DegradedStreak;
        /// Per hop of Trace: consecutive replies from another address than the traced one. Replaced together with Trace.
        public int[] AddressStreaks = [];
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
        var source = adapter?.IPv4 is { } text && IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null;
        var unchanged = adapter?.Index == lane.Adapter?.Index && Equals(source, lane.Source);
        lane.Adapter = adapter; // the details (name, gateway, link state) stay fresh even when the lane keeps its samples
        if (unchanged) return;
        lane.Source = source;
        lane.Generation++;
        lane.Trace = null;
        lane.Hops = [];
        lane.AddressStreaks = [];
        lane.DegradedStreak = 0;
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
                if (lane.Source is { } source && (force || RetraceDue(lane, now)))
                {
                    lane.LastTrace = now; // stamped at the start, so a tracer that throws is retried after the minimum gap, not every second
                    due.Add((lane, lane.Generation, source));
                }
        }

        await Task.WhenAll(due.Select(async d =>
        {
            var trace = await Guard<PathTrace?>(async () => await _tracer.TraceAsync(d.Source, PathTracer.DefaultTarget, PathTracer.DefaultTimeout, ct), null);
            if (trace is null || ct.IsCancellationRequested) return;
            lock (_gate)
            {
                var lane = d.Lane;
                if (d.Generation != lane.Generation) return;
                // A trace made during a drop (probes past the break time out) must not replace a better one at once: it is kept
                // out, with its samples, until a second retrace in a row agrees, so a real path change still lands ~10 s later.
                if (lane.Trace is { } current && IsDegraded(current, trace) && ++lane.DegradedStreak < 2)
                {
                    lane.NeedsRetrace = true;
                    return;
                }
                lane.DegradedStreak = 0;
                Adopt(lane, trace);
            }
        }));
    }

    /// Worse than the current trace: fewer visible devices, a Complete path that is now Incomplete, or no trace at all
    /// (Unavailable) where there was one.
    static bool IsDegraded(PathTrace current, PathTrace next)
    {
        if (next.Status == TraceStatus.Unavailable) return current.Status != TraceStatus.Unavailable;
        return Visible(next) < Visible(current)
            || (current.Status == TraceStatus.Complete && next.Status == TraceStatus.Incomplete);
    }

    static int Visible(PathTrace trace) => trace.Hops.Count(h => !h.Hidden);

    /// A device keeps its samples when the same TTL answered from the same address in the old trace; the others start empty.
    void Adopt(Lane lane, PathTrace trace)
    {
        var old = lane.Trace;
        var oldHops = lane.Hops;
        lane.Hops = trace.Hops.Select((hop, i) =>
            old is not null && i < old.Hops.Count && i < oldHops.Count && hop.Address is { } address && Equals(old.Hops[i].Address, address)
                ? oldHops[i]
                : new PathSamples(_t.Window)).ToList();
        lane.AddressStreaks = new int[trace.Hops.Count];
        lane.Trace = trace;
        lane.NeedsRetrace = trace.Status == TraceStatus.Unavailable;
    }

    bool RetraceDue(Lane lane, DateTimeOffset now) =>
        now - lane.LastTrace >= (lane.NeedsRetrace ? RetraceMinGap : RetraceEvery);

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
                    work.Add(ProbeHop(lane, generation, trace, source, hop, i, store, ct));
                }
            }
        }
        await Task.WhenAll(work);
    }

    async Task ProbeHop(Lane lane, int generation, PathTrace trace, IPAddress source, PathHop hop, int index, PathSamples store, CancellationToken ct)
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
            if (result.Outcome == ProbeOutcome.Reply) NoteReply(lane, hop, index, result);
        }
    }

    /// The target at a TTL that used to be a device means the path got shorter: retrace at once. A reply from another address
    /// has to repeat before it counts (a load-balanced hop answers from several); a reply from the traced address ends the run.
    /// A lost or unavailable probe says nothing about the address, so it neither extends nor ends it.
    /// (Hidden hops are not probed, so a silent hop that starts answering is picked up by the periodic retrace.)
    void NoteReply(Lane lane, PathHop hop, int index, EchoResult result)
    {
        if (result.TargetReached) lane.NeedsRetrace = true;
        else if (Equals(result.From, hop.Address)) lane.AddressStreaks[index] = 0;
        else if (++lane.AddressStreaks[index] >= _t.HopAddressChangeReplies) lane.NeedsRetrace = true;
    }

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
            LogOnce(ex.Message);
            return onError;
        }
    }

    /// Logs each distinct message once (and at most a few distinct ones), outside the lock.
    void LogOnce(string message)
    {
        bool fresh;
        lock (_gate) fresh = _logged.Count < MaxLoggedMessages && _logged.Add(message);
        if (fresh) _log($"Path map: a check failed to run: {message}");
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

    /// Runs both loops until cancelled, then waits for the rounds still out (they watch the same token). The hop loop also retraces
    /// when due; the internet check has its own loop because it can take longer than a second.
    public async Task RunAsync(CancellationToken ct)
    {
        try { await Task.WhenAll(HopLoop(ct), EndpointLoop(ct)); }
        catch (OperationCanceledException) { }
    }

    /// Fixed rate: a new round starts every period whether or not the previous one has finished (during an outage a round waits
    /// for its timeouts, and the 30 s window must still fill at the stated rate). The retrace runs beside the rounds, never in front of them.
    async Task HopLoop(CancellationToken ct)
    {
        var rounds = new List<Task>();
        Task? retrace = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tick = Task.Delay(HopPeriod, _time, ct); // started first, so the rate does not drift with the work
                if (retrace is not { IsCompleted: false }) retrace = Launched(() => RetraceAsync(ct));
                rounds.RemoveAll(t => t.IsCompleted);
                if (rounds.Count < MaxOutstandingRounds) rounds.Add(Launched(() => HopRoundAsync(ct)));
                await tick;
            }
        }
        finally { await Task.WhenAll(retrace is null ? rounds : rounds.Append(retrace)); }
    }

    async Task EndpointLoop(CancellationToken ct)
    {
        var rounds = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tick = Task.Delay(EndpointPeriod, _time, ct);
                rounds.RemoveAll(t => t.IsCompleted);
                if (rounds.Count < MaxOutstandingRounds) rounds.Add(Launched(() => EndpointRoundAsync(ct)));
                await tick;
            }
        }
        finally { await Task.WhenAll(rounds); }
    }

    /// Starts work without awaiting it. The returned task never faults: an exception is logged (once per message) and the loop carries on.
    async Task Launched(Func<Task> work)
    {
        try { await work(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LogOnce(ex.Message); }
    }
}
