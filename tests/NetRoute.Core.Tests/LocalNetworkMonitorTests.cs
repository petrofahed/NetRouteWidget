using System.Net;
using Microsoft.Extensions.Time.Testing;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public class LocalNetworkMonitorTests
{
    static IPAddress Ip(string s) => IPAddress.Parse(s);
    static readonly IPAddress Router = Ip("192.168.86.1");
    static readonly IPAddress Nas = Ip("192.168.86.44");
    static readonly LocalDevice NasDevice = new("NAS", "192.168.86.44");

    sealed class FakeLocalProbe : IPathProbe
    {
        public Dictionary<IPAddress, EchoResult> ByTarget { get; } = new();
        public EchoResult Default { get; set; } = new(ProbeOutcome.NoReply);
        public List<(IPAddress Source, IPAddress Target, int Ttl, TimeSpan Timeout)> Sent { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public Exception? Throw { get; set; }

        public async Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
        {
            lock (Sent) Sent.Add((source, target, ttl, timeout));
            if (Gate is { } gate) await gate.Task;
            if (Throw is { } ex) throw ex;
            return ByTarget.TryGetValue(target, out var r) ? r : Default;
        }
    }

    sealed class Rig
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        public FakeLocalProbe Probe { get; } = new();
        public Dictionary<string, IReadOnlyList<IPAddress>> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Resolved { get; } = new();
        public List<string> Logs { get; } = new();
        public LocalNetworkMonitor Monitor { get; }

        public Rig()
        {
            Probe.ByTarget[Router] = new EchoResult(ProbeOutcome.Reply, Router, 1);
            Probe.ByTarget[Nas] = new EchoResult(ProbeOutcome.Reply, Nas, 2);
            Monitor = new LocalNetworkMonitor(Probe,
                name =>
                {
                    lock (Resolved) Resolved.Add(name);
                    return Task.FromResult<IReadOnlyList<IPAddress>>(Names.TryGetValue(name, out var found) ? found : []);
                },
                Time, PathThresholds.Default, Logs.Add);
        }

        public async Task Rounds(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Monitor.RoundAsync(CancellationToken.None);
                Time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        public DeviceSnapshot Device(string key) => Monitor.Snapshot().Devices.Single(d => d.Key == key);
    }

    [Fact]
    public async Task The_router_and_each_device_are_pinged_once_per_round_from_the_lan_address()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);

        await rig.Rounds(3);

        Assert.Equal(6, rig.Probe.Sent.Count);
        Assert.All(rig.Probe.Sent, s =>
        {
            Assert.Equal(Ip("192.168.86.42"), s.Source);
            Assert.Equal(PathThresholds.Default.DirectTtl, s.Ttl);
            Assert.Equal(LocalNetworkMonitor.EchoTimeout, s.Timeout);
        });
        Assert.Equal(3, rig.Device(LocalNetworkMonitor.RouterKey).Samples.Count);
        var nas = rig.Device(LocalNetworkMonitor.KeyOf("192.168.86.44"));
        Assert.Equal("NAS", nas.Name);
        Assert.All(nas.Samples, s => Assert.Equal((ProbeOutcome.Reply, 2), (s.Outcome, s.RttMs ?? 0)));
    }

    [Fact]
    public async Task A_lost_ping_is_no_reply_and_a_probe_that_could_not_run_is_unavailable()
    {
        var rig = new Rig();
        rig.Probe.ByTarget[Nas] = new EchoResult(ProbeOutcome.NoReply);
        rig.Probe.ByTarget[Router] = new EchoResult(ProbeOutcome.Unavailable);
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);

        await rig.Rounds(2);

        Assert.All(rig.Device(LocalNetworkMonitor.KeyOf("192.168.86.44")).Samples, s => Assert.Equal(ProbeOutcome.NoReply, s.Outcome));
        Assert.All(rig.Device(LocalNetworkMonitor.RouterKey).Samples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
    }

    [Fact]
    public async Task Without_a_lan_adapter_nothing_is_probed_and_there_are_no_devices()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(null, null, [NasDevice]);

        await rig.Rounds(2);

        Assert.Empty(rig.Probe.Sent);
        Assert.Empty(rig.Monitor.Snapshot().Devices);
        Assert.Null(rig.Monitor.Snapshot().Lan);
    }

    [Fact]
    public async Task A_lan_adapter_that_has_no_address_yet_is_not_probed()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan() with { IPv4 = null }, Router, [NasDevice]);

        await rig.Rounds(2);

        Assert.Empty(rig.Probe.Sent);
    }

    [Fact]
    public async Task A_replugged_lan_with_a_new_address_starts_clean_from_the_new_source()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);
        await rig.Rounds(3);

        rig.Monitor.SetNetwork(null, null, [NasDevice]); // cable out
        await rig.Rounds(2);
        Assert.Equal(6, rig.Probe.Sent.Count); // nothing was sent while it was out

        rig.Monitor.SetNetwork(TestAdapters.Lan() with { IPv4 = "192.168.86.77" }, Router, [NasDevice]); // cable back, new lease
        await rig.Rounds(1);

        Assert.Equal(Ip("192.168.86.77"), rig.Probe.Sent[^1].Source);
        Assert.Single(rig.Device(LocalNetworkMonitor.RouterKey).Samples); // the old samples are gone
    }

    [Fact]
    public async Task Adding_a_device_keeps_the_others_samples_and_removing_it_drops_only_its_own()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        await rig.Rounds(3);

        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);
        await rig.Rounds(1);
        Assert.Equal(4, rig.Device(LocalNetworkMonitor.RouterKey).Samples.Count);
        Assert.Single(rig.Device(LocalNetworkMonitor.KeyOf("192.168.86.44")).Samples);

        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        Assert.DoesNotContain(rig.Monitor.Snapshot().Devices, d => d.Name == "NAS");
        Assert.Equal(4, rig.Device(LocalNetworkMonitor.RouterKey).Samples.Count);
    }

    [Fact]
    public async Task A_new_gateway_gives_the_router_row_fresh_samples()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        await rig.Rounds(3);

        rig.Monitor.SetNetwork(TestAdapters.Lan(), Ip("192.168.86.2"), []);
        await rig.Rounds(1);

        var router = rig.Device(LocalNetworkMonitor.RouterKey);
        Assert.Equal("192.168.86.2", router.Address);
        Assert.Single(router.Samples);
    }

    [Fact]
    public async Task A_hostname_is_resolved_once_and_the_address_it_gave_is_pinged()
    {
        var rig = new Rig();
        rig.Names["nas.local"] = [Nas];
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [new LocalDevice("NAS", "nas.local")]);

        await rig.Rounds(3);

        Assert.Equal(["nas.local"], rig.Resolved);
        var nas = rig.Device(LocalNetworkMonitor.KeyOf("nas.local"));
        Assert.Equal(Nas, nas.Resolved);
        Assert.False(nas.NameNotFound);
        Assert.Contains(rig.Probe.Sent, s => s.Target.Equals(Nas));
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_unavailable_not_lost_and_is_retried_later()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [new LocalDevice("Ghost", "ghost.local")]);

        await rig.Rounds(3);

        var ghost = rig.Device(LocalNetworkMonitor.KeyOf("ghost.local"));
        Assert.True(ghost.NameNotFound);
        Assert.All(ghost.Samples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
        Assert.Single(rig.Resolved); // not asked again inside the retry delay

        rig.Names["ghost.local"] = [Ip("192.168.86.99")];
        rig.Time.Advance(LocalNetworkMonitor.ResolveRetry);
        await rig.Rounds(1);

        Assert.Equal(2, rig.Resolved.Count);
        Assert.False(rig.Device(LocalNetworkMonitor.KeyOf("ghost.local")).NameNotFound);
    }

    [Fact]
    public async Task A_probe_that_was_in_flight_when_the_network_changed_is_dropped()
    {
        var rig = new Rig();
        rig.Probe.Gate = new TaskCompletionSource();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, []);
        var round = rig.Monitor.RoundAsync(CancellationToken.None);

        rig.Monitor.SetNetwork(TestAdapters.Lan() with { IPv4 = "192.168.86.77" }, Router, []); // the lane was reset meanwhile
        rig.Probe.Gate.SetResult();
        await round;

        Assert.Empty(rig.Device(LocalNetworkMonitor.RouterKey).Samples);
    }

    [Fact]
    public async Task A_probe_that_throws_is_unavailable_and_logged_once()
    {
        var rig = new Rig();
        rig.Probe.Throw = new InvalidOperationException("icmp is blocked");
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);

        await rig.Rounds(3);

        Assert.All(rig.Device(LocalNetworkMonitor.RouterKey).Samples, s => Assert.Equal(ProbeOutcome.Unavailable, s.Outcome));
        Assert.Equal(1, rig.Logs.Count(l => l.Contains("icmp is blocked")));
    }

    [Fact]
    public async Task A_cancelled_run_ends_without_throwing()
    {
        var rig = new Rig();
        rig.Monitor.SetNetwork(TestAdapters.Lan(), Router, [NasDevice]);
        using var cts = new CancellationTokenSource();
        var run = rig.Monitor.RunAsync(cts.Token);

        cts.Cancel();
        await run; // would throw if cancellation leaked out

        Assert.True(run.IsCompletedSuccessfully);
    }
}
