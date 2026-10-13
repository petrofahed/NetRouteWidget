namespace NetRoute.Core.PathMap;

/// Statistics over the samples of one device in the window. Unavailable samples are counted on their own:
/// they never count as loss, and they are skipped when counting runs of lost checks or replies.
public readonly record struct SampleStats(
    int Total, int Replies, int Lost, int Unavailable,
    int ConsecutiveLost, int ConsecutiveReplies, int ConsecutiveUnavailable,
    int? AvgMs, int? BestMs, int? WorstMs)
{
    /// Samples that say something about the network.
    public int Valid => Replies + Lost;

    public double LossPercent => Valid == 0 ? 0 : 100.0 * Lost / Valid;

    public double UnavailablePercent => Total == 0 ? 0 : 100.0 * Unavailable / Total;

    /// samples: oldest first.
    public static SampleStats Of(IReadOnlyList<Sample> samples)
    {
        int replies = 0, lost = 0, unavailable = 0, rttCount = 0;
        long rttSum = 0;
        int? best = null, worst = null;
        foreach (var s in samples)
        {
            switch (s.Outcome)
            {
                case ProbeOutcome.Reply:
                    replies++;
                    if (s.RttMs is { } ms)
                    {
                        rttCount++;
                        rttSum += ms;
                        best = best is null ? ms : Math.Min(best.Value, ms);
                        worst = worst is null ? ms : Math.Max(worst.Value, ms);
                    }
                    break;
                case ProbeOutcome.NoReply:
                    lost++;
                    break;
                default:
                    unavailable++;
                    break;
            }
        }

        int consecutiveLost = 0, consecutiveReplies = 0, consecutiveUnavailable = 0;
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            if (samples[i].Outcome == ProbeOutcome.Unavailable) continue;
            if (samples[i].Outcome != ProbeOutcome.NoReply) break;
            consecutiveLost++;
        }
        for (var i = samples.Count - 1; i >= 0; i--)
        {
            if (samples[i].Outcome == ProbeOutcome.Unavailable) continue;
            if (samples[i].Outcome != ProbeOutcome.Reply) break;
            consecutiveReplies++;
        }
        for (var i = samples.Count - 1; i >= 0 && samples[i].Outcome == ProbeOutcome.Unavailable; i--) consecutiveUnavailable++;

        return new SampleStats(
            samples.Count, replies, lost, unavailable, consecutiveLost, consecutiveReplies, consecutiveUnavailable,
            rttCount == 0 ? null : (int)Math.Round(rttSum / (double)rttCount), best, worst);
    }
}
