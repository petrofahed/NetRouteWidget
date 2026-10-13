using System.Net;

namespace NetRoute.Core.PathMap;

/// What one check observed. Unavailable means the measurement itself failed on this PC (or was cancelled):
/// it says nothing about the network and is never counted as packet loss.
public enum ProbeOutcome { Reply, NoReply, Unavailable }

public readonly record struct Sample(DateTimeOffset At, ProbeOutcome Outcome, int? RttMs = null);

/// One ICMP echo with a chosen TTL. From is the device that answered: the hop for "TTL expired", the target for a real reply.
public sealed record EchoResult(ProbeOutcome Outcome, IPAddress? From = null, int? RttMs = null, bool TargetReached = false);

public sealed record EndpointResult(ProbeOutcome Outcome, int? RttMs = null);

public interface IPathProbe
{
    /// Never throws for a failed measurement: that is an Unavailable result. A cancelled token gives Unavailable too.
    Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct);
}

public interface IEndpointProbe
{
    /// The "does this connection reach the internet" check, sent from the given local address.
    Task<EndpointResult> CheckAsync(IPAddress source, CancellationToken ct);
}

public enum TraceStatus { Complete, Incomplete, Unavailable }

public sealed record PathHop(int Ttl, IPAddress? Address)
{
    /// A hop that never answered the trace (many carriers and some routers do not).
    public bool Hidden => Address is null;
}

public sealed record PathTrace(IPAddress Source, IPAddress Target, IReadOnlyList<PathHop> Hops, TraceStatus Status);
