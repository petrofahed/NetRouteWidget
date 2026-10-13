namespace NetRoute.Core.PathMap;

/// How a device is drawn. Quiet: not answering probes well, but nothing points at it. Silent: no answer past a located break.
public enum HopDisplay { Measuring, Answering, Quiet, Hidden, Unavailable, Suspect, Broken, Silent }

public enum LaneStatus { NotConnected, Measuring, Unavailable, Healthy, Blip, Fault }

/// How far the evidence goes in a Fault. Located: a break between two devices. Narrowed: the fault is at or beyond a device that
/// kept answering, but no device is named. NotEnoughData: no claim, the evidence is too thin. Uncertain: no claim, because the
/// only sign against a device is that it stopped returning route probes, which alone does not show it is broken.
/// None: the lane is not in a Fault.
public enum Localization { None, Located, Narrowed, NotEnoughData, Uncertain }

public sealed record LaneInput(
    string Name,
    bool Connected,
    PathTrace? Trace,
    // One list per trace hop, same order, oldest first, already limited to the window. A hidden hop's list is empty.
    IReadOnlyList<IReadOnlyList<Sample>> HopSamples,
    IReadOnlyList<Sample> EndpointSamples,
    DateTimeOffset Now,
    // Direct pings to each hop's own address, same shape as HopSamples. Null = none were sent. A device that answers a direct ping
    // is reachable; that is not proof it forwards traffic, and a device that ignores route probes may still answer one.
    IReadOnlyList<IReadOnlyList<Sample>>? DirectSamples = null);

public sealed record HopDiagnosis(
    PathHop Hop, SampleStats Stats, Observation Observed, HopDisplay Display, double? LossDuringFailuresPercent,
    SampleStats? DirectStats = null, Observation? DirectObserved = null);

public sealed record LaneDiagnosis(
    string Name,
    LaneStatus Status,
    Localization Where,
    IReadOnlyList<HopDiagnosis> Hops,
    Observation Endpoint,
    SampleStats EndpointStats,
    // Index into Hops of the furthest device that answered while the internet check was failing; null = this PC.
    int? LastAnsweringHop,
    // Index into Hops of the first device after the break; set only when Where is Located.
    int? FirstBadHop,
    TraceStatus? TraceStatus);

/// Turns raw observations into a conclusion. The internet check decides whether a lane is failing at all; a device is
/// blamed only when its probes were lost in the same 2-second buckets as the failed internet checks.
public static class PathDiagnoser
{
    public static LaneDiagnosis Diagnose(LaneInput lane, PathThresholds t)
    {
        var endpointStats = SampleStats.Of(lane.EndpointSamples);
        var endpoint = PathObserver.Observe(endpointStats, hidden: false, endpoint: true, t);

        if (!lane.Connected)
            return new LaneDiagnosis(lane.Name, LaneStatus.NotConnected, Localization.None, [], endpoint, endpointStats, null, null, null);
        if (lane.Trace is not { } trace)
            return new LaneDiagnosis(lane.Name, LaneStatus.Measuring, Localization.None, [], endpoint, endpointStats, null, null, null);

        var stats = trace.Hops.Select((_, i) => SampleStats.Of(i < lane.HopSamples.Count ? lane.HopSamples[i] : [])).ToArray();
        var observed = trace.Hops.Select((h, i) => PathObserver.Observe(stats[i], h.Hidden, endpoint: false, t)).ToArray();
        var directStats = trace.Hops.Select((h, i) =>
            lane.DirectSamples is { } d && !h.Hidden && i < d.Count && d[i].Count > 0 ? SampleStats.Of(d[i]) : (SampleStats?)null).ToArray();
        var directObserved = trace.Hops.Select((h, i) =>
            directStats[i] is { } s ? PathObserver.Observe(s, h.Hidden, endpoint: false, t) : (Observation?)null).ToArray();

        LaneDiagnosis Build(LaneStatus status, Localization where, Func<int, HopDisplay> display,
            double?[]? failureLoss = null, int? lastAnswering = null, int? firstBad = null) =>
            new(lane.Name, status, where,
                trace.Hops.Select((h, i) => new HopDiagnosis(h, stats[i], observed[i], display(i), failureLoss?[i], directStats[i], directObserved[i])).ToList(),
                endpoint, endpointStats, lastAnswering, firstBad, trace.Status);

        HopDisplay Calm(int i) => observed[i] switch
        {
            Observation.Hidden => HopDisplay.Hidden,
            Observation.Unavailable => HopDisplay.Unavailable,
            Observation.Measuring => HopDisplay.Measuring,
            Observation.Answering => HopDisplay.Answering,
            // Silent or lossy route probes with nothing pointing at the device. If it answers a direct ping it is drawn as answering:
            // it only declines to return the route probes, which says nothing against it.
            _ => directObserved[i] == Observation.Answering ? HopDisplay.Answering : HopDisplay.Quiet,
        };

        // The lane's health is the internet check's verdict. An unavailable trace (ICMP cannot run here) only means there are no
        // devices to examine; it never hides a working or a failing internet check.
        if (endpoint == Observation.Unavailable)
            return Build(LaneStatus.Unavailable, Localization.None, Calm);
        if (endpoint == Observation.Measuring)
            return Build(LaneStatus.Measuring, Localization.None, i => observed[i] is Observation.Hidden or Observation.Unavailable ? Calm(i) : HopDisplay.Measuring);
        if (endpoint == Observation.Answering)
            return Build(LaneStatus.Healthy, Localization.None, Calm);
        if (endpointStats.Lost < t.MinFailedChecks)
            return Build(LaneStatus.Blip, Localization.None, Calm);
        if (trace.Status == TraceStatus.Unavailable)
            return Build(LaneStatus.Fault, Localization.NotEnoughData, Calm);

        return Localize(lane, trace, observed, t, Build, Calm);
    }

    static LaneDiagnosis Localize(
        LaneInput lane, PathTrace trace, Observation[] observed, PathThresholds t,
        Func<LaneStatus, Localization, Func<int, HopDisplay>, double?[]?, int?, int?, LaneDiagnosis> build,
        Func<int, HopDisplay> calm)
    {
        var failing = FailingBuckets(lane.EndpointSamples, t.Bucket);
        var n = trace.Hops.Count;
        var cleared = new bool[n];
        var implicated = new bool[n];
        var routeProbesOnly = new bool[n]; // route probes lost in step with the failure, but nothing else points at the device
        var failureLoss = new double?[n];

        for (var i = 0; i < n; i++)
        {
            if (trace.Hops[i].Hidden || i >= lane.HopSamples.Count) continue;
            var route = Gather(lane.HopSamples[i], failing, t);
            var direct = lane.DirectSamples is { } d && i < d.Count ? Gather(d[i], failing, t) : default;
            if (route.Failing > 0) failureLoss[i] = route.LossFailing;

            // A device is on the working path when it answered while the internet check failed, to a route probe or a direct ping.
            // That says traffic reaches it; whether it passes traffic on is not shown, which is why the fault stays "at or beyond" it.
            cleared[i] = route.Cleared(t) || direct.Cleared(t);

            // Missing route probes alone name no device: the direct ping to the same device must have been lost in the same moments
            // too. Without direct evidence (never sent, unavailable or too few) a device can be cleared, never implicated.
            implicated[i] = route.Implicated(t) && direct.Implicated(t);
            routeProbesOnly[i] = route.Implicated(t) && !implicated[i] && !cleared[i];
        }

        int? furthestCleared = null;
        for (var i = 0; i < n; i++) if (cleared[i]) furthestCleared = i;

        var candidates = Enumerable.Range((furthestCleared ?? -1) + 1, n - ((furthestCleared ?? -1) + 1))
            .Where(i => !trace.Hops[i].Hidden).ToList();

        if (candidates.Count > 0 && candidates.All(i => implicated[i]))
        {
            var first = candidates[0];
            var after = candidates.ToHashSet();
            return build(LaneStatus.Fault, Localization.Located,
                i => i == first ? (observed[i] == Observation.Silent ? HopDisplay.Broken : HopDisplay.Suspect)
                    : after.Contains(i) ? (observed[i] == Observation.Silent ? HopDisplay.Silent : calm(i))
                    : cleared[i] ? HopDisplay.Answering : calm(i),
                failureLoss, furthestCleared, first);
        }

        if (furthestCleared is { } lastAnswering)
            // Only devices after the furthest one that answered can be suspect: traffic demonstrably gets past the earlier ones.
            return build(LaneStatus.Fault, Localization.Narrowed,
                i => cleared[i] ? HopDisplay.Answering : implicated[i] && i > lastAnswering ? HopDisplay.Suspect : calm(i),
                failureLoss, lastAnswering, null);

        // Nothing answered during the failures and no device is both route-silent and unreachable by direct ping. If devices went
        // quiet to route probes only, say the location is uncertain; with too little evidence either way, say there is not enough data.
        return build(LaneStatus.Fault, routeProbesOnly.Any(x => x) ? Localization.Uncertain : Localization.NotEnoughData,
            calm, failureLoss, null, null);
    }

    /// What one series says about the moments the internet check failed, compared with the moments it worked.
    readonly record struct Evidence(int Failing, double LossFailing, int Good, double LossGood)
    {
        public bool Cleared(PathThresholds t) => Failing >= t.MinEvidenceProbes && LossFailing <= t.ClearedLossPercent;

        // Needs a baseline: with too few probes from good periods a device that always ignores probes cannot be told from a broken one.
        public bool Implicated(PathThresholds t) =>
            Failing >= t.MinEvidenceProbes && Good >= t.MinEvidenceProbes
            && LossFailing >= t.ImplicatedLossPercent && LossFailing - LossGood >= t.ImplicatedMarginPoints;
    }

    static Evidence Gather(IReadOnlyList<Sample> samples, Dictionary<long, bool> failing, PathThresholds t)
    {
        int nFailing = 0, lostFailing = 0, nGood = 0, lostGood = 0;
        foreach (var s in samples)
        {
            if (s.Outcome == ProbeOutcome.Unavailable || !failing.TryGetValue(BucketOf(s.At, t.Bucket), out var isFailing)) continue;
            var lost = s.Outcome == ProbeOutcome.NoReply ? 1 : 0;
            if (isFailing) { nFailing++; lostFailing += lost; }
            else { nGood++; lostGood += lost; }
        }
        return new Evidence(nFailing, nFailing == 0 ? 0 : 100.0 * lostFailing / nFailing, nGood, nGood == 0 ? 0 : 100.0 * lostGood / nGood);
    }

    static long BucketOf(DateTimeOffset at, TimeSpan bucket) => at.ToUnixTimeMilliseconds() / (long)bucket.TotalMilliseconds;

    /// bucket → true when the internet check lost in it, false when it replied. Buckets with only Unavailable checks are left out.
    static Dictionary<long, bool> FailingBuckets(IReadOnlyList<Sample> endpointSamples, TimeSpan bucket)
    {
        var result = new Dictionary<long, bool>();
        foreach (var s in endpointSamples)
        {
            if (s.Outcome == ProbeOutcome.Unavailable) continue;
            var key = BucketOf(s.At, bucket);
            result[key] = s.Outcome == ProbeOutcome.NoReply || (result.TryGetValue(key, out var was) && was);
        }
        return result;
    }
}
