using System.Net;

namespace NetRoute.Core.PathMap;

/// Finds the devices between one adapter and the target by sending TTL-limited echoes from the adapter's own address.
public sealed class PathTracer(IPathProbe probe, PathThresholds thresholds)
{
    public static readonly IPAddress DefaultTarget = IPAddress.Parse("1.1.1.1");
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    /// All TTLs are sent in parallel. Complete: the target answered at some TTL. Incomplete: it did not within the hop
    /// limit (a long path, or ICMP to the target is filtered): never a fault by itself. Unavailable: the checks could not run.
    public async Task<PathTrace> TraceAsync(IPAddress source, IPAddress target, TimeSpan timeout, CancellationToken ct)
    {
        var results = await Task.WhenAll(
            Enumerable.Range(1, thresholds.MaxHops).Select(ttl => probe.EchoAsync(source, target, ttl, timeout, ct)));
        ct.ThrowIfCancellationRequested();

        var unavailable = results.Count(r => r.Outcome == ProbeOutcome.Unavailable);
        if (results[0].Outcome == ProbeOutcome.Unavailable || unavailable * 2 >= results.Length)
            return new PathTrace(source, target, [], TraceStatus.Unavailable);

        var reached = Array.FindIndex(results, r => r.TargetReached);
        if (reached >= 0)
            return new PathTrace(source, target, HopsOf(results, reached), TraceStatus.Complete);

        var hops = HopsOf(results, results.Length);
        while (hops.Count > 0 && hops[^1].Hidden) hops.RemoveAt(hops.Count - 1);
        return new PathTrace(source, target, hops, TraceStatus.Incomplete);
    }

    static List<PathHop> HopsOf(EchoResult[] results, int count) =>
        results.Take(count)
            .Select((r, i) => new PathHop(i + 1, r.Outcome == ProbeOutcome.Reply && !r.TargetReached ? r.From : null))
            .ToList();
}
