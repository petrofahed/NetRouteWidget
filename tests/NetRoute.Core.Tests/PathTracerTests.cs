using NetRoute.Core.PathMap;
using static NetRoute.Core.Tests.FakePathProbe;
using static NetRoute.Core.Tests.PathKit;

namespace NetRoute.Core.Tests;

public class PathTracerTests
{
    static Task<PathTrace> Trace(FakePathProbe probe, CancellationToken ct = default) =>
        new PathTracer(probe, T).TraceAsync(Ip("192.168.86.42"), Ip("1.1.1.1"), TimeSpan.FromSeconds(1), ct);

    [Fact]
    public async Task Complete_trace_lists_the_devices_before_the_target()
    {
        var probe = new FakePathProbe { Default = Target() };
        probe.ByTtl[1] = Hop("192.168.86.1");
        probe.ByTtl[2] = Hop("192.168.1.1");
        probe.ByTtl[3] = Hop("172.17.18.1");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Complete, trace.Status);
        Assert.Equal(["192.168.86.1", "192.168.1.1", "172.17.18.1"], trace.Hops.Select(h => h.Address!.ToString()));
        Assert.Equal([1, 2, 3], trace.Hops.Select(h => h.Ttl));
    }

    [Fact]
    public async Task A_silent_ttl_before_the_target_is_a_hidden_hop()
    {
        var probe = new FakePathProbe { Default = Target() };
        probe.ByTtl[1] = Hop("192.168.42.129");
        probe.ByTtl[2] = new EchoResult(ProbeOutcome.NoReply);
        probe.ByTtl[3] = Hop("172.26.242.30");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Complete, trace.Status);
        Assert.Equal([false, true, false], trace.Hops.Select(h => h.Hidden));
    }

    [Fact]
    public async Task Reaching_the_hop_limit_is_an_incomplete_trace_not_a_failure()
    {
        // every TTL answers as a router; the target never does
        var probe = new FakePathProbe();
        for (var ttl = 1; ttl <= 12; ttl++) probe.ByTtl[ttl] = Hop($"10.0.0.{ttl}");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Incomplete, trace.Status);
        Assert.Equal(12, trace.Hops.Count);
    }

    [Fact]
    public async Task Incomplete_trace_trims_trailing_hidden_hops()
    {
        var probe = new FakePathProbe();
        probe.ByTtl[1] = Hop("192.168.86.1");
        probe.ByTtl[2] = Hop("192.168.1.1");

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Incomplete, trace.Status);
        Assert.Equal(2, trace.Hops.Count);
    }

    [Fact]
    public async Task Local_errors_make_the_trace_unavailable_not_incomplete()
    {
        var probe = new FakePathProbe { Default = new EchoResult(ProbeOutcome.Unavailable) };

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Unavailable, trace.Status);
        Assert.Empty(trace.Hops);
    }

    [Fact]
    public async Task Target_answering_at_the_first_ttl_gives_an_empty_complete_path()
    {
        var probe = new FakePathProbe { Default = Target() };

        var trace = await Trace(probe);

        Assert.Equal(TraceStatus.Complete, trace.Status);
        Assert.Empty(trace.Hops);
    }

    [Fact]
    public async Task Sends_every_ttl_up_to_the_hop_limit()
    {
        var probe = new FakePathProbe();

        await Trace(probe);

        Assert.Equal(Enumerable.Range(1, 12), probe.Calls.OrderBy(x => x));
    }

    [Fact]
    public async Task A_cancelled_trace_throws_instead_of_reporting_a_path()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Trace(new FakePathProbe(), cts.Token));
    }

    [Fact]
    public async Task A_reply_that_names_no_address_is_a_hidden_hop()
    {
        var probe = new FakePathProbe { Default = Target() };
        probe.ByTtl[1] = Hop("192.168.86.1");
        probe.ByTtl[2] = new EchoResult(ProbeOutcome.Reply, From: null);

        var trace = await Trace(probe);

        Assert.Equal([false, true], trace.Hops.Select(h => h.Hidden));
    }
}
