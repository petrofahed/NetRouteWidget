namespace NetRoute.Core.PathMap;

/// How a device is drawn. Quiet: not answering probes well, but nothing points at it. Silent: no answer past a located break.
public enum HopDisplay { Measuring, Answering, Quiet, Hidden, Unavailable, Suspect, Broken, Silent }

public enum LaneStatus { NotConnected, Measuring, Unavailable, Healthy, Blip, Fault }

/// How far the evidence goes in a Fault. Located: a break between two devices. Narrowed: the fault is after a device that kept
/// answering, but no device is named. NotEnoughData: no claim. None: the lane is not in a Fault.
public enum Localization { None, Located, Narrowed, NotEnoughData }

public sealed record LaneInput(
    string Name,
    bool Connected,
    PathTrace? Trace,
    // One list per trace hop, same order, oldest first, already limited to the window. A hidden hop's list is empty.
    IReadOnlyList<IReadOnlyList<Sample>> HopSamples,
    IReadOnlyList<Sample> EndpointSamples,
    DateTimeOffset Now);

public sealed record HopDiagnosis(
    PathHop Hop, SampleStats Stats, Observation Observed, HopDisplay Display, double? LossDuringFailuresPercent);

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

        LaneDiagnosis Build(LaneStatus status, Localization where, Func<int, HopDisplay> display,
            double?[]? failureLoss = null, int? lastAnswering = null, int? firstBad = null) =>
            new(lane.Name, status, where,
                trace.Hops.Select((h, i) => new HopDiagnosis(h, stats[i], observed[i], display(i), failureLoss?[i])).ToList(),
                endpoint, endpointStats, lastAnswering, firstBad, trace.Status);

        HopDisplay Calm(int i) => observed[i] switch
        {
            Observation.Hidden => HopDisplay.Hidden,
            Observation.Unavailable => HopDisplay.Unavailable,
            Observation.Measuring => HopDisplay.Measuring,
            Observation.Answering => HopDisplay.Answering,
            _ => HopDisplay.Quiet, // silent or lossy, with nothing pointing at it
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
        var failureLoss = new double?[n];

        for (var i = 0; i < n; i++)
        {
            if (trace.Hops[i].Hidden || i >= lane.HopSamples.Count) continue;
            int nFailing = 0, lostFailing = 0, nGood = 0, lostGood = 0;
            foreach (var s in lane.HopSamples[i])
            {
                if (s.Outcome == ProbeOutcome.Unavailable || !failing.TryGetValue(BucketOf(s.At, t.Bucket), out var isFailing)) continue;
                var lost = s.Outcome == ProbeOutcome.NoReply ? 1 : 0;
                if (isFailing) { nFailing++; lostFailing += lost; }
                else { nGood++; lostGood += lost; }
            }
            if (nFailing == 0) continue;

            var lossFailing = 100.0 * lostFailing / nFailing;
            failureLoss[i] = lossFailing;
            if (nFailing < t.MinEvidenceProbes) continue;
            cleared[i] = lossFailing <= t.ClearedLossPercent;
            // Blaming a device needs a baseline: without enough probes from good periods (e.g. the window is all outage) a device
            // that simply ignores probes cannot be told from a broken one, so it can be cleared but never implicated.
            if (nGood < t.MinEvidenceProbes) continue;
            var lossGood = 100.0 * lostGood / nGood;
            implicated[i] = lossFailing >= t.ImplicatedLossPercent && lossFailing - lossGood >= t.ImplicatedMarginPoints;
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

        return build(LaneStatus.Fault, Localization.NotEnoughData, calm, failureLoss, null, null);
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
