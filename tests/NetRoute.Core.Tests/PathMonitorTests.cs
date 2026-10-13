using Microsoft.Extensions.Time.Testing;
using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.FakePathProbe;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathMonitorTests
{
    sealed class Rig
    {
        public FakePathProbe Hops { get; } = new();
        public FakeEndpointProbe Endpoint { get; } = new();
        public FakeTimeProvider Time { get; } = new(Start);
        public List<string> Log { get; } = new();
        public PathMonitor Monitor { get; }

        public Rig()
        {
            // two devices, then the target
            Hops.ByTtl[1] = Hop("192.168.86.1");
            Hops.ByTtl[2] = Hop("192.168.1.1");
            Hops.Default = Target();
            Monitor = new PathMonitor(Hops, Endpoint, Time, T, Log.Add);
            Monitor.SetAdapters(TestAdapters.Lan(), null);
        }

        public async Task Ready()
        {
            await Monitor.RetraceAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_round_stores_one_sample_per_device_and_one_internet_sample()
    {
        var rig = new Rig();
        await rig.Ready();

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        var lan = rig.Monitor.Snapshot().Lan;

        Assert.Equal(TraceStatus.Complete, lan.Trace!.Status);
        Assert.Equal(2, lan.Trace.Hops.Count);
        Assert.All(lan.HopSamples, s => Assert.Equal(ProbeOutcome.Reply, Assert.Single(s).Outcome));
        Assert.Equal(ProbeOutcome.Reply, Assert.Single(lan.EndpointSamples).Outcome);
        Assert.NotNull(rig.Monitor.Snapshot().LastSampleAt);
    }

    [Fact]
    public async Task A_lane_without_an_adapter_is_not_probed()
    {
        var rig = new Rig();
        await rig.Ready();

        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);

        Assert.Null(rig.Monitor.Snapshot().Phone.Trace);
        Assert.Empty(rig.Monitor.Snapshot().Phone.EndpointSamples);
    }

    [Fact]
    public async Task A_probe_that_could_not_run_is_stored_as_unavailable_not_as_loss()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.ByTtl[1] = new EchoResult(ProbeOutcome.Unavailable);
        rig.Endpoint.Result = new EndpointResult(ProbeOutcome.Unavailable);

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        var lan = rig.Monitor.Snapshot().Lan;

        Assert.Equal(ProbeOutcome.Unavailable, lan.HopSamples[0][0].Outcome);
        Assert.Equal(ProbeOutcome.Unavailable, lan.EndpointSamples[0].Outcome);
        Assert.Equal(0, SampleStats.Of(lan.EndpointSamples).Lost);
    }

    [Fact]
    public async Task A_probe_that_throws_is_unavailable_and_logged_once()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Endpoint.Throw = new InvalidOperationException("boom");

        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);

        var samples = rig.Monitor.Snapshot().Lan.EndpointSamples;
        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
        Assert.Single(rig.Log);
        Assert.Contains("boom", rig.Log[0]);
    }

    [Fact]
    public async Task A_cancelled_round_stores_nothing()
    {
        var rig = new Rig();
        await rig.Ready();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await rig.Monitor.HopRoundAsync(cts.Token);
        await rig.Monitor.EndpointRoundAsync(cts.Token);
        var lan = rig.Monitor.Snapshot().Lan;

        Assert.All(lan.HopSamples, s => Assert.Empty(s));
        Assert.Empty(lan.EndpointSamples);
    }

    [Fact]
    public async Task A_probe_in_flight_when_the_adapter_changes_is_dropped()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.Gate = new TaskCompletionSource();

        var round = rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Monitor.SetAdapters(TestAdapters.Lan(index: 11), null); // a different adapter: the lane is reset
        rig.Hops.Gate.SetResult();
        await round;

        var lan = rig.Monitor.Snapshot().Lan;
        Assert.Null(lan.Trace);
        Assert.Empty(lan.HopSamples);
        Assert.Null(rig.Monitor.Snapshot().LastSampleAt); // the stale sample was not stored anywhere
    }

    [Fact]
    public async Task A_check_in_flight_when_the_adapter_changes_is_dropped()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Endpoint.Gate = new TaskCompletionSource();

        var round = rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        rig.Monitor.SetAdapters(TestAdapters.Lan(index: 11), null);
        rig.Endpoint.Gate.SetResult();
        await round;

        Assert.Empty(rig.Monitor.Snapshot().Lan.EndpointSamples);
        Assert.Null(rig.Monitor.Snapshot().LastSampleAt);
    }

    [Fact]
    public async Task A_probe_in_flight_while_an_unchanged_path_is_retraced_is_dropped()
    {
        var rig = new Rig();
        await rig.Ready();
        var held = 0;
        rig.Hops.Gate = new TaskCompletionSource();
        rig.Hops.GateWhen = ttl => ttl == 1 && Interlocked.Increment(ref held) == 1; // hold only the hop probe, not the tracer's echo

        var round = rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.RetraceAsync(CancellationToken.None, force: true); // same addresses: the history stays, but the trace is a new object
        rig.Hops.Gate.SetResult();
        await round;

        var lan = rig.Monitor.Snapshot().Lan;
        Assert.Empty(lan.HopSamples[0]); // answered for the old trace: dropped
        Assert.Single(lan.HopSamples[1]); // not held: stored before the retrace, kept by it
    }

    [Fact]
    public async Task The_same_adapter_keeps_its_samples()
    {
        var rig = new Rig();
        await rig.Ready();
        await rig.Monitor.HopRoundAsync(CancellationToken.None);

        rig.Monitor.SetAdapters(TestAdapters.Lan(), null);

        Assert.Single(rig.Monitor.Snapshot().Lan.HopSamples[0]);
    }

    [Fact]
    public async Task Samples_expire_with_the_window()
    {
        var rig = new Rig();
        await rig.Ready();
        await rig.Monitor.HopRoundAsync(CancellationToken.None);

        rig.Time.Advance(TimeSpan.FromSeconds(31));

        Assert.All(rig.Monitor.Snapshot().Lan.HopSamples, s => Assert.Empty(s));
    }

    [Fact]
    public async Task A_reply_from_another_address_triggers_a_retrace_after_the_minimum_gap()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.ByTtl[2] = Hop("10.1.1.1"); // the second device changed

        for (var i = 0; i < T.HopAddressChangeReplies; i++) await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.RetraceAsync(CancellationToken.None); // too soon: nothing happens
        Assert.Equal("192.168.1.1", rig.Monitor.Snapshot().Lan.Trace!.Hops[1].Address!.ToString());

        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.Equal("10.1.1.1", rig.Monitor.Snapshot().Lan.Trace!.Hops[1].Address!.ToString());
    }

    [Fact]
    public async Task An_unchanged_path_is_retraced_only_every_thirty_seconds_and_keeps_its_samples()
    {
        var rig = new Rig();
        await rig.Ready();

        rig.Time.Advance(TimeSpan.FromSeconds(20));
        await rig.Monitor.HopRoundAsync(CancellationToken.None); // a sample at 20 s, still inside the window at 31 s
        var callsBefore = rig.Hops.Calls.Count;
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.Equal(callsBefore, rig.Hops.Calls.Count); // 20 s since the trace: not due

        rig.Time.Advance(TimeSpan.FromSeconds(11));
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.True(rig.Hops.Calls.Count > callsBefore); // 31 s: due
        Assert.Single(rig.Monitor.Snapshot().Lan.HopSamples[0]); // same addresses: the history stays
    }

    [Fact]
    public async Task An_unavailable_trace_is_retried_after_the_minimum_gap()
    {
        var rig = new Rig();
        rig.Hops.Default = new EchoResult(ProbeOutcome.Unavailable);
        rig.Hops.ByTtl.Clear();
        await rig.Ready();
        Assert.Equal(TraceStatus.Unavailable, rig.Monitor.Snapshot().Lan.Trace!.Status);

        rig.Hops.Default = Target();
        rig.Hops.ByTtl[1] = Hop("192.168.86.1");
        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None);

        Assert.Equal(TraceStatus.Complete, rig.Monitor.Snapshot().Lan.Trace!.Status);
    }

    [Fact]
    public async Task Run_stops_when_cancelled()
    {
        var rig = new Rig();
        using var cts = new CancellationTokenSource();

        var run = rig.Monitor.RunAsync(cts.Token);
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task An_adapter_that_has_no_address_yet_is_not_probed()
    {
        var rig = new Rig();
        rig.Monitor.SetAdapters(TestAdapters.Lan() with { IPv4 = null }, null);

        await rig.Monitor.RetraceAsync(CancellationToken.None);
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);

        Assert.Empty(rig.Hops.Calls);
        Assert.Null(rig.Monitor.Snapshot().Lan.Trace);
    }

    [Fact]
    public async Task The_target_answering_at_a_device_ttl_means_the_path_got_shorter_and_is_retraced()
    {
        var rig = new Rig();
        await rig.Ready();
        Assert.Equal(2, rig.Monitor.Snapshot().Lan.Trace!.Hops.Count);
        rig.Hops.ByTtl[2] = Target(); // the second device is gone: the target now answers at TTL 2

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        rig.Time.Advance(PathMonitor.RetraceMinGap); // a shorter path is adopted once a second retrace agrees
        await rig.Monitor.RetraceAsync(CancellationToken.None);

        Assert.Single(rig.Monitor.Snapshot().Lan.Trace!.Hops);
    }

    [Fact]
    public async Task A_round_cancelled_while_probes_are_in_flight_stores_nothing()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var round = rig.Monitor.HopRoundAsync(cts.Token);
        cts.Cancel(); // the window closed while the probes were out
        rig.Hops.Gate.SetResult();
        await round;

        Assert.All(rig.Monitor.Snapshot().Lan.HopSamples, s => Assert.Empty(s));
    }

    [Fact]
    public async Task A_sample_is_stamped_with_the_time_the_probe_was_sent()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.Gate = new TaskCompletionSource();
        var sentAt = rig.Time.GetUtcNow();

        var round = rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromSeconds(1.5)); // the probe is held for 1.5 s
        var completedAt = rig.Time.GetUtcNow();
        rig.Hops.Gate.SetResult();
        await round;

        var snapshot = rig.Monitor.Snapshot();
        Assert.Equal(sentAt, Assert.Single(snapshot.Lan.HopSamples[0]).At);
        Assert.Equal(completedAt, snapshot.LastSampleAt);
    }

    [Fact]
    public async Task A_check_is_stamped_with_the_time_it_was_sent()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Endpoint.Gate = new TaskCompletionSource();
        var sentAt = rig.Time.GetUtcNow();

        var round = rig.Monitor.EndpointRoundAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromSeconds(3)); // a slow check
        var completedAt = rig.Time.GetUtcNow();
        rig.Endpoint.Gate.SetResult();
        await round;

        var snapshot = rig.Monitor.Snapshot();
        Assert.Equal(sentAt, Assert.Single(snapshot.Lan.EndpointSamples).At);
        Assert.Equal(completedAt, snapshot.LastSampleAt);
    }

    [Fact]
    public async Task The_adapter_details_stay_fresh_while_the_samples_are_kept()
    {
        var rig = new Rig();
        await rig.Ready();
        await rig.Monitor.HopRoundAsync(CancellationToken.None);

        rig.Monitor.SetAdapters(TestAdapters.Lan() with { Name = "Ethernet (renamed)" }, null);

        var lan = rig.Monitor.Snapshot().Lan;
        Assert.Equal("Ethernet (renamed)", lan.Adapter!.Name);
        Assert.Single(lan.HopSamples[0]);
    }

    [Fact]
    public async Task An_adapter_with_only_an_ipv6_address_is_not_probed()
    {
        var rig = new Rig();
        rig.Monitor.SetAdapters(TestAdapters.Lan() with { IPv4 = "fe80::1" }, null);

        await rig.Monitor.RetraceAsync(CancellationToken.None);
        await rig.Monitor.EndpointRoundAsync(CancellationToken.None);

        Assert.Empty(rig.Hops.Calls);
        Assert.Null(rig.Monitor.Snapshot().Lan.Trace);
    }

    [Fact]
    public async Task A_tracer_that_throws_is_retried_after_the_minimum_gap_not_every_round()
    {
        var rig = new Rig();
        rig.Hops.Throw = new InvalidOperationException("no tracer");
        await rig.Ready();
        var calls = rig.Hops.Calls.Count;

        await rig.Monitor.RetraceAsync(CancellationToken.None); // right away: not due
        Assert.Equal(calls, rig.Hops.Calls.Count);

        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.True(rig.Hops.Calls.Count > calls);
        Assert.Single(rig.Log);
    }

    [Fact]
    public async Task Rounds_start_every_period_even_while_a_probe_is_still_in_flight()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.Gate = new TaskCompletionSource(); // every probe stays out until released
        using var cts = new CancellationTokenSource();
        int CallsAtTtl1() { lock (rig.Hops.Calls) return rig.Hops.Calls.Count(c => c == 1); }
        var baseline = CallsAtTtl1();

        var run = rig.Monitor.RunAsync(cts.Token);
        await WaitUntil(() => CallsAtTtl1() == baseline + 1);
        for (var step = 2; step <= 3; step++)
        {
            rig.Time.Advance(PathMonitor.HopPeriod);
            await WaitUntil(() => CallsAtTtl1() == baseline + step); // a new round while the earlier ones are still blocked
        }

        rig.Time.Advance(PathMonitor.HopPeriod); // a fourth tick: three rounds are out, so none is added
        await Task.Delay(100);
        Assert.Equal(baseline + PathMonitor.MaxOutstandingRounds, CallsAtTtl1());

        rig.Hops.Gate.SetResult();
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.IsCompletedSuccessfully);
    }

    // ----- a retrace must not truncate the path or wipe evidence (F1) -----

    [Fact]
    public async Task A_shorter_trace_is_kept_out_until_a_second_retrace_agrees_and_the_samples_survive()
    {
        var rig = new Rig();
        await rig.Ready();
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Hops.ByTtl[2] = Target(); // the retrace now finds one device only, e.g. the probes past the router time out in a drop

        await rig.Monitor.RetraceAsync(CancellationToken.None, force: true);
        var first = rig.Monitor.Snapshot().Lan;
        Assert.Equal(2, first.Trace!.Hops.Count); // not adopted on first sight
        Assert.All(first.HopSamples, s => Assert.Single(s)); // and the evidence is still there

        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None); // due after the minimum gap, not the thirty seconds
        var second = rig.Monitor.Snapshot().Lan;
        Assert.Single(second.Trace!.Hops);
        Assert.Single(Assert.Single(second.HopSamples)); // the router kept its history
    }

    [Fact]
    public async Task An_incomplete_trace_for_a_complete_path_is_not_adopted_on_first_sight()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.ByTtl[2] = Hop("192.168.1.1");
        rig.Hops.Default = new EchoResult(ProbeOutcome.NoReply); // both devices still answer, the target does not

        await rig.Monitor.RetraceAsync(CancellationToken.None, force: true);

        Assert.Equal(TraceStatus.Complete, rig.Monitor.Snapshot().Lan.Trace!.Status);
    }

    [Fact]
    public async Task A_good_trace_between_two_degraded_ones_resets_the_count()
    {
        var rig = new Rig();
        await rig.Ready();
        var original = rig.Monitor.Snapshot().Lan.Trace!;
        Assert.Equal(TraceStatus.Complete, original.Status);
        var good = rig.Hops.Default;
        var degraded = new EchoResult(ProbeOutcome.NoReply); // both devices still answer, the target does not: Complete becomes Incomplete

        rig.Hops.Default = degraded;
        await rig.Monitor.RetraceAsync(CancellationToken.None, force: true); // degraded, first in a row: kept out
        Assert.Same(original, rig.Monitor.Snapshot().Lan.Trace);

        rig.Hops.Default = good;
        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None); // a good trace is adopted: the count starts over
        var recovered = rig.Monitor.Snapshot().Lan.Trace!;
        Assert.NotSame(original, recovered);
        Assert.Equal(TraceStatus.Complete, recovered.Status);

        rig.Hops.Default = degraded;
        rig.Time.Advance(PathMonitor.RetraceEvery); // a retrace is due again
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        var afterFirst = rig.Monitor.Snapshot().Lan.Trace!;
        Assert.Same(recovered, afterFirst); // only the FIRST degraded one since the reset: still kept out
        Assert.Equal(TraceStatus.Complete, afterFirst.Status);
        Assert.Equal(original.Hops.Count, afterFirst.Hops.Count);

        rig.Time.Advance(PathMonitor.RetraceMinGap);
        await rig.Monitor.RetraceAsync(CancellationToken.None); // the second degraded one in a row is adopted
        var afterSecond = rig.Monitor.Snapshot().Lan.Trace!;
        Assert.Equal(TraceStatus.Incomplete, afterSecond.Status);
        Assert.NotSame(recovered, afterSecond);
    }

    [Fact]
    public async Task An_unavailable_trace_for_a_known_path_is_kept_and_retried()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.Default = new EchoResult(ProbeOutcome.Unavailable);
        rig.Hops.ByTtl.Clear();

        await rig.Monitor.RetraceAsync(CancellationToken.None, force: true);
        Assert.Equal(TraceStatus.Complete, rig.Monitor.Snapshot().Lan.Trace!.Status);

        rig.Hops.Default = Target();
        rig.Hops.ByTtl[1] = Hop("192.168.86.1");
        rig.Hops.ByTtl[2] = Hop("192.168.1.1");
        var calls = rig.Hops.Calls.Count;
        rig.Time.Advance(PathMonitor.RetraceMinGap); // retried after the minimum gap
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.True(rig.Hops.Calls.Count > calls);
        Assert.Equal(2, rig.Monitor.Snapshot().Lan.Trace!.Hops.Count);
    }

    [Fact]
    public async Task A_path_that_really_lost_a_hop_is_adopted_after_two_retraces_ten_seconds_apart()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.ByTtl[2] = Target();

        await rig.Monitor.RetraceAsync(CancellationToken.None, force: true);
        rig.Time.Advance(PathMonitor.RetraceMinGap - TimeSpan.FromSeconds(1));
        await rig.Monitor.RetraceAsync(CancellationToken.None); // 9 s: still inside the minimum gap
        Assert.Equal(2, rig.Monitor.Snapshot().Lan.Trace!.Hops.Count);

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.Single(rig.Monitor.Snapshot().Lan.Trace!.Hops);
    }

    // ----- per-hop retention and flapping addresses (F2) -----

    [Fact]
    public async Task A_retrace_where_only_one_address_changed_keeps_the_other_devices_samples()
    {
        var rig = new Rig();
        rig.Hops.ByTtl[3] = Hop("172.17.18.1");
        await rig.Ready();
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Hops.ByTtl[3] = Hop("10.5.5.5"); // only the third device changed

        await rig.Monitor.RetraceAsync(CancellationToken.None, force: true);
        var lan = rig.Monitor.Snapshot().Lan;

        Assert.Equal("10.5.5.5", lan.Trace!.Hops[2].Address!.ToString());
        Assert.Single(lan.HopSamples[0]);
        Assert.Single(lan.HopSamples[1]);
        Assert.Empty(lan.HopSamples[2]); // a different device starts its own history
    }

    [Fact]
    public async Task Fewer_than_three_replies_from_another_address_do_not_ask_for_a_retrace_the_third_does()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Hops.ByTtl[2] = Hop("10.1.1.1");
        rig.Time.Advance(PathMonitor.RetraceMinGap);

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        var calls = rig.Hops.Calls.Count;
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.Equal(calls, rig.Hops.Calls.Count); // two odd replies: not a path change

        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        calls = rig.Hops.Calls.Count;
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.True(rig.Hops.Calls.Count > calls); // the third in a row: retrace
    }

    [Fact]
    public async Task A_reply_from_the_traced_address_resets_the_count_of_odd_replies()
    {
        var rig = new Rig();
        await rig.Ready();
        rig.Time.Advance(PathMonitor.RetraceMinGap);
        var traced = rig.Hops.ByTtl[2];

        rig.Hops.ByTtl[2] = Hop("10.1.1.1");
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Hops.ByTtl[2] = traced;
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        rig.Hops.ByTtl[2] = Hop("10.1.1.1");
        await rig.Monitor.HopRoundAsync(CancellationToken.None);
        await rig.Monitor.HopRoundAsync(CancellationToken.None);

        var calls = rig.Hops.Calls.Count;
        await rig.Monitor.RetraceAsync(CancellationToken.None);
        Assert.Equal(calls, rig.Hops.Calls.Count); // 2 + 2 odd replies, never 3 in a row
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10); // bounded: at most about 2 s
        Assert.True(condition(), "the condition was not met in time");
    }
}
