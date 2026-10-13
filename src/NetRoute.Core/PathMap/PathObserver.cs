namespace NetRoute.Core.PathMap;

/// What a device's own samples say, before any conclusion about the network is drawn from them.
public enum Observation { Measuring, Answering, Lossy, Silent, Hidden, Unavailable }

public static class PathObserver
{
    /// First match wins. 'endpoint' selects the internet check's sample minimum and recovery count.
    public static Observation Observe(SampleStats s, bool hidden, bool endpoint, PathThresholds t)
    {
        if (hidden) return Observation.Hidden;

        if (s.ConsecutiveUnavailable >= t.UnavailableConsecutive
            || (s.Total >= t.UnavailableConsecutive && s.UnavailablePercent >= t.UnavailableSharePercent))
            return Observation.Unavailable;

        if (s.ConsecutiveLost >= t.SilentConsecutive) return Observation.Silent;

        var minimum = endpoint ? t.EndpointMinSamples : t.HopMinSamples;
        if (s.Valid < minimum) return Observation.Measuring;

        // Recovery: enough consecutive replies since the last lost check end "losing packets",
        // even while older losses are still inside the window.
        var recover = endpoint ? t.EndpointRecoverReplies : t.HopRecoverReplies;
        if (s.LossPercent >= t.LossyPercent && s.ConsecutiveReplies < recover) return Observation.Lossy;

        return Observation.Answering;
    }
}
