namespace NetRoute.Core.PathMap;

/// Every tunable number of the path map in one place. These are initial defaults, to be tuned after real use.
public sealed record PathThresholds
{
    public static PathThresholds Default { get; } = new();

    /// Samples older than this are dropped.
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(30);

    /// No judgement of a hop before this many valid (Reply or NoReply) samples.
    public int HopMinSamples { get; init; } = 10;
    public int EndpointMinSamples { get; init; } = 5;

    /// Loss at or above this percentage is "losing packets".
    public double LossyPercent { get; init; } = 5;

    /// This many consecutive lost checks is "no reply".
    public int SilentConsecutive { get; init; } = 3;

    /// Consecutive replies after the last lost check that end "losing packets".
    public int HopRecoverReplies { get; init; } = 10;
    public int EndpointRecoverReplies { get; init; } = 5;

    /// Measurement unavailable: this many latest samples Unavailable, or this share of the window.
    public int UnavailableConsecutive { get; init; } = 3;
    public double UnavailableSharePercent { get; init; } = 50;

    /// Failed internet checks in the window needed before a fault (not just a blip) is declared.
    public int MinFailedChecks { get; init; } = 3;

    /// Hop probes needed in the failing buckets before a hop can be cleared or implicated.
    public int MinEvidenceProbes { get; init; } = 5;
    public double ImplicatedLossPercent { get; init; } = 50;
    public double ImplicatedMarginPoints { get; init; } = 30;
    public double ClearedLossPercent { get; init; } = 10;

    /// Hop probes are matched to internet checks in buckets of this size.
    public TimeSpan Bucket { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxHops { get; init; } = 12;

    /// A device answering from another address than the traced one this many times in a row asks for a new trace
    /// (a single odd reply is usually a load-balanced hop).
    public int HopAddressChangeReplies { get; init; } = 3;

    /// Boxes drawn between the PC and the Internet card; the rest collapse into "+N more".
    public int MaxDrawnHops { get; init; } = 4;
}
