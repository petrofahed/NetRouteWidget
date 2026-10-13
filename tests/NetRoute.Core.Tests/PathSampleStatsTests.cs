using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathSampleStatsTests
{
    [Fact]
    public void Unavailable_samples_are_never_counted_as_loss()
    {
        var samples = Every(1, sec => sec % 3 == 0 ? ProbeOutcome.Unavailable : ProbeOutcome.Reply);

        var s = SampleStats.Of(samples);

        Assert.Equal(0, s.Lost);
        Assert.Equal(0, s.LossPercent);
        Assert.Equal(10, s.Unavailable);
        Assert.Equal(20, s.Valid);
        Assert.Equal(30, s.Total);
    }

    [Fact]
    public void Loss_percent_counts_only_valid_samples()
    {
        // 2 lost, 8 replies, plus 10 unavailable that must not dilute the percentage
        var samples = new List<Sample>();
        for (var i = 0; i < 10; i++) samples.Add(new(Start.AddSeconds(i), i < 2 ? ProbeOutcome.NoReply : ProbeOutcome.Reply, 10));
        for (var i = 10; i < 20; i++) samples.Add(new(Start.AddSeconds(i), ProbeOutcome.Unavailable));

        Assert.Equal(20, SampleStats.Of(samples).LossPercent);
    }

    [Fact]
    public void Consecutive_runs_skip_unavailable_samples()
    {
        var samples = new List<Sample>
        {
            new(Start, ProbeOutcome.Reply, 5),
            new(Start.AddSeconds(1), ProbeOutcome.NoReply),
            new(Start.AddSeconds(2), ProbeOutcome.Unavailable),
            new(Start.AddSeconds(3), ProbeOutcome.NoReply),
            new(Start.AddSeconds(4), ProbeOutcome.NoReply),
        };

        var s = SampleStats.Of(samples);

        Assert.Equal(3, s.ConsecutiveLost);
        Assert.Equal(0, s.ConsecutiveReplies);
        Assert.Equal(0, s.ConsecutiveUnavailable);
    }

    [Fact]
    public void Consecutive_unavailable_counts_only_the_newest_run()
    {
        var samples = new List<Sample>
        {
            new(Start, ProbeOutcome.Unavailable),
            new(Start.AddSeconds(1), ProbeOutcome.Reply, 5),
            new(Start.AddSeconds(2), ProbeOutcome.Unavailable),
            new(Start.AddSeconds(3), ProbeOutcome.Unavailable),
        };

        Assert.Equal(2, SampleStats.Of(samples).ConsecutiveUnavailable);
    }

    [Fact]
    public void Latency_statistics_come_from_replies_only()
    {
        var samples = new List<Sample>
        {
            new(Start, ProbeOutcome.Reply, 10),
            new(Start.AddSeconds(1), ProbeOutcome.NoReply),
            new(Start.AddSeconds(2), ProbeOutcome.Reply, 30),
        };

        var s = SampleStats.Of(samples);

        Assert.Equal((20, 10, 30), (s.AvgMs, s.BestMs, s.WorstMs));
    }

    [Fact]
    public void Empty_window_has_no_statistics()
    {
        var s = SampleStats.Of([]);

        Assert.Equal(0, s.Valid);
        Assert.Null(s.AvgMs);
        Assert.Equal(0, s.LossPercent);
    }

    [Fact]
    public void Window_drops_old_samples()
    {
        var samples = new PathSamples(TimeSpan.FromSeconds(30));
        samples.Add(new Sample(Start, ProbeOutcome.Reply, 5));
        samples.Add(new Sample(Start.AddSeconds(20), ProbeOutcome.Reply, 5));

        Assert.Equal(2, samples.Snapshot(Start.AddSeconds(25)).Count);
        Assert.Single(samples.Snapshot(Start.AddSeconds(40)));
        Assert.Empty(samples.Snapshot(Start.AddSeconds(60)));
    }
}
