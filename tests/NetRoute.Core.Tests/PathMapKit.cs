using System.Net;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

/// Builders for path-map tests. The window is 30 s long and ends at Now; second 0 is its start. Hop probes run every second
/// and internet checks every 2 s (on even seconds), so second 2k and 2k+1 share one 2-second bucket.
static class PathKit
{
    public static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Now = Start.AddSeconds(30);
    public static readonly PathThresholds T = PathThresholds.Default;

    public static IPAddress Ip(string s) => IPAddress.Parse(s);

    /// A complete four-device LAN path: router, the box behind it, the provider gateway and one more hop.
    public static readonly PathTrace Four = Trace("192.168.86.1", "192.168.1.1", "172.17.18.1", "10.9.8.249");

    public static List<Sample> Every(int stepSeconds, Func<int, ProbeOutcome> outcome, int rtt = 20, int fromSecond = 0)
    {
        var list = new List<Sample>();
        for (var sec = fromSecond; sec < 30; sec += stepSeconds)
        {
            var o = outcome(sec);
            list.Add(new Sample(Start.AddSeconds(sec), o, o == ProbeOutcome.Reply ? rtt : null));
        }
        return list;
    }

    public static Func<int, ProbeOutcome> AllReply => _ => ProbeOutcome.Reply;
    public static Func<int, ProbeOutcome> AllLost => _ => ProbeOutcome.NoReply;
    public static Func<int, ProbeOutcome> AllUnavailable => _ => ProbeOutcome.Unavailable;

    /// NoReply from 'from' (inclusive) to 'to' (exclusive), Reply elsewhere.
    public static Func<int, ProbeOutcome> LostDuring(int from, int to = 30) =>
        sec => sec >= from && sec < to ? ProbeOutcome.NoReply : ProbeOutcome.Reply;

    /// A complete trace from the LAN adapter; null means a hidden hop.
    public static PathTrace Trace(params string?[] hops) => new(
        Ip("192.168.86.42"), Ip("1.1.1.1"),
        hops.Select((a, i) => new PathHop(i + 1, a is null ? null : Ip(a))).ToList(), TraceStatus.Complete);

    public static LaneInput Lane(PathTrace? trace, List<Sample> endpoint, params List<Sample>[] hops) =>
        new("LAN", true, trace, hops.Select(h => (IReadOnlyList<Sample>)h).ToList(), endpoint, Now);

    /// The same, with a direct ping to every device that fares exactly like its route probes (a device that is really unreachable).
    public static LaneInput LaneM(PathTrace? trace, List<Sample> endpoint, params List<Sample>[] hops) =>
        Lane(trace, endpoint, hops) with { DirectSamples = hops.Select(h => (IReadOnlyList<Sample>)h).ToList() };

    /// Route probes as given, direct pings as given: the two kinds of evidence told apart.
    public static LaneInput LaneWithDirect(PathTrace? trace, List<Sample> endpoint, List<Sample>[] hops, List<Sample>[] direct) =>
        Lane(trace, endpoint, hops) with { DirectSamples = direct.Select(h => (IReadOnlyList<Sample>)h).ToList() };
}

/// Answers each TTL from a script; unknown TTLs time out.
sealed class FakePathProbe : IPathProbe
{
    public Dictionary<int, EchoResult> ByTtl { get; } = new();
    public List<int> Calls { get; } = new();
    /// Every echo asked for, with the address it was aimed at.
    public List<(int Ttl, IPAddress Target)> Sent { get; } = new();
    /// The local address every echo was sent from.
    public List<IPAddress> Sources { get; } = new();
    public EchoResult Default { get; set; } = new(ProbeOutcome.NoReply);

    /// When set, every echo waits for it: holds a probe in flight.
    public TaskCompletionSource? Gate { get; set; }
    /// When set together with Gate, only the echoes it accepts (by TTL) wait. It may be stateful, e.g. to hold only the first call.
    public Func<int, bool>? GateWhen { get; set; }
    public Exception? Throw { get; set; }

    public async Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
    {
        lock (Calls) { Calls.Add(ttl); Sent.Add((ttl, target)); Sources.Add(source); }
        if (Gate is { } gate && (GateWhen?.Invoke(ttl) ?? true)) await gate.Task;
        if (Throw is { } ex) throw ex;
        return ByTtl.TryGetValue(ttl, out var r) ? r : Default;
    }

    public static EchoResult Hop(string address, int ms = 5) => new(ProbeOutcome.Reply, IPAddress.Parse(address), ms);
    public static EchoResult Target(int ms = 20) => new(ProbeOutcome.Reply, IPAddress.Parse("1.1.1.1"), ms, TargetReached: true);
}

sealed class FakeEndpointProbe : IEndpointProbe
{
    public EndpointResult Result { get; set; } = new(ProbeOutcome.Reply, 20);
    /// The local address every check was sent from.
    public List<IPAddress> Sources { get; } = new();
    public Exception? Throw { get; set; }

    /// When set, every check waits for it: holds a check in flight.
    public TaskCompletionSource? Gate { get; set; }

    public async Task<EndpointResult> CheckAsync(IPAddress source, CancellationToken ct)
    {
        lock (Sources) Sources.Add(source);
        if (Gate is { } gate) await gate.Task;
        return Throw is { } ex ? throw ex : Result;
    }
}
