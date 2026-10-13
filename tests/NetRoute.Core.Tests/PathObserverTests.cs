using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathObserverTests
{
    static Observation Hop(IReadOnlyList<Sample> samples, bool hidden = false) =>
        PathObserver.Observe(SampleStats.Of(samples), hidden, endpoint: false, T);

    static Observation Endpoint(IReadOnlyList<Sample> samples) =>
        PathObserver.Observe(SampleStats.Of(samples), hidden: false, endpoint: true, T);

    [Fact]
    public void Hidden_hops_stay_hidden()
    {
        Assert.Equal(Observation.Hidden, Hop(Every(1, AllReply), hidden: true));
    }

    [Fact]
    public void Too_few_samples_is_measuring_not_a_verdict()
    {
        // 9 valid hop samples: one short of the minimum
        Assert.Equal(Observation.Measuring, Hop(Every(1, AllReply).Take(9).ToList()));
        Assert.Equal(Observation.Answering, Hop(Every(1, AllReply).Take(10).ToList()));
    }

    [Fact]
    public void Internet_check_needs_only_five_samples()
    {
        Assert.Equal(Observation.Measuring, Endpoint(Every(2, AllReply).Take(4).ToList()));
        Assert.Equal(Observation.Answering, Endpoint(Every(2, AllReply).Take(5).ToList()));
    }

    [Fact]
    public void Three_consecutive_lost_checks_are_silent_even_during_startup()
    {
        var samples = Every(1, AllLost).Take(3).ToList();

        Assert.Equal(Observation.Silent, Hop(samples));
    }

    [Fact]
    public void Two_lost_checks_in_a_row_are_not_silent()
    {
        var samples = Every(1, AllLost).Take(2).ToList();

        Assert.Equal(Observation.Measuring, Hop(samples));
    }

    [Fact]
    public void Loss_at_the_threshold_is_lossy_while_the_last_check_was_lost()
    {
        // 20 samples, the last one lost: 5% loss, no replies since
        var samples = Every(1, sec => sec == 19 ? ProbeOutcome.NoReply : ProbeOutcome.Reply).Take(20).ToList();

        Assert.Equal(Observation.Lossy, Hop(samples));
    }

    [Fact]
    public void Loss_below_the_threshold_is_not_lossy()
    {
        // 1 lost in 25 valid samples is 4%, below the 5% default
        var samples = Every(1, sec => sec == 3 ? ProbeOutcome.NoReply : ProbeOutcome.Reply).Take(25).ToList();

        Assert.Equal(Observation.Answering, Hop(samples));
    }

    [Fact]
    public void Ten_consecutive_replies_end_lossy_even_with_old_losses_in_the_window()
    {
        // lost at seconds 2 and 18, then replies for seconds 19..29 (11 in a row)
        var samples = Every(1, sec => sec is 2 or 18 ? ProbeOutcome.NoReply : ProbeOutcome.Reply);

        Assert.Equal(Observation.Answering, Hop(samples));
    }

    [Fact]
    public void Fewer_than_ten_replies_since_the_last_loss_stay_lossy()
    {
        // lost at seconds 2 and 24, then 5 replies
        var samples = Every(1, sec => sec is 2 or 24 ? ProbeOutcome.NoReply : ProbeOutcome.Reply);

        Assert.Equal(Observation.Lossy, Hop(samples));
    }

    [Fact]
    public void Internet_check_recovers_after_five_replies()
    {
        // checks on even seconds: lost at 8 and 10, then replies at 12..28 (9 in a row)
        var samples = Every(2, sec => sec is 8 or 10 ? ProbeOutcome.NoReply : ProbeOutcome.Reply);

        Assert.Equal(Observation.Answering, Endpoint(samples));
    }

    [Fact]
    public void Local_errors_are_unavailable_not_silent()
    {
        var samples = Every(1, AllUnavailable);

        Assert.Equal(Observation.Unavailable, Hop(samples));
    }

    [Fact]
    public void Half_the_window_unavailable_is_unavailable()
    {
        var samples = Every(1, sec => sec % 2 == 0 ? ProbeOutcome.Unavailable : ProbeOutcome.Reply);

        Assert.Equal(Observation.Unavailable, Hop(samples));
    }

    [Fact]
    public void Few_unavailable_samples_among_replies_change_nothing()
    {
        var samples = Every(1, sec => sec is 7 or 15 ? ProbeOutcome.Unavailable : ProbeOutcome.Reply);

        Assert.Equal(Observation.Answering, Hop(samples));
    }
}
