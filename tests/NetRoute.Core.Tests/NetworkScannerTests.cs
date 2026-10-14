using System.Net;
using NetRoute.Core.PathMap;

namespace NetRoute.Core.Tests;

public class NetworkScannerTests
{
    static IPAddress Ip(string s) => IPAddress.Parse(s);
    static readonly IPAddress Source = Ip("192.168.86.42");

    sealed class FakeSweepProbe : IPathProbe
    {
        public HashSet<IPAddress> Answering { get; } = new();
        public List<(IPAddress Source, IPAddress Target, TimeSpan Timeout)> Sent { get; } = new();
        public int MaxInFlight { get; private set; }
        int _inFlight;

        public async Task<EchoResult> EchoAsync(IPAddress source, IPAddress target, int ttl, TimeSpan timeout, CancellationToken ct)
        {
            lock (Sent)
            {
                Sent.Add((source, target, timeout));
                MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
            }
            await Task.Yield();
            lock (Sent) _inFlight--;
            return Answering.Contains(target) ? new EchoResult(ProbeOutcome.Reply, target, 1, TargetReached: true) : new EchoResult(ProbeOutcome.NoReply);
        }
    }

    sealed class FakeNeighbors(params (string Address, string Mac)[] entries) : INeighborTable
    {
        public IReadOnlyList<(IPAddress Address, string Mac)> Read() => entries.Select(e => (Ip(e.Address), e.Mac)).ToList();
    }

    static NetworkScanner Scanner(FakeSweepProbe probe, INeighborTable? neighbors = null, Func<IPAddress, CancellationToken, Task<string?>>? names = null) =>
        new(probe, neighbors ?? new FakeNeighbors(), names ?? ((_, _) => Task.FromResult<string?>(null)));

    [Fact]
    public void A_slash_24_has_every_host_but_the_network_the_broadcast_and_this_pc()
    {
        var hosts = NetworkScanner.HostsOf(Source, 24);

        Assert.Equal(253, hosts.Count);
        Assert.Equal(Ip("192.168.86.1"), hosts[0]);
        Assert.Equal(Ip("192.168.86.254"), hosts[^1]);
        Assert.DoesNotContain(Source, hosts);
        Assert.DoesNotContain(Ip("192.168.86.0"), hosts);
        Assert.DoesNotContain(Ip("192.168.86.255"), hosts);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(8)]
    [InlineData(0)]
    public void A_subnet_larger_than_a_slash_24_scans_the_slash_24_around_the_address(int prefix)
    {
        var hosts = NetworkScanner.HostsOf(Source, prefix);

        Assert.Equal(253, hosts.Count);
        Assert.All(hosts, h => Assert.StartsWith("192.168.86.", h.ToString()));
    }

    [Fact]
    public void A_slash_28_scans_its_own_fourteen_hosts()
    {
        var hosts = NetworkScanner.HostsOf(Ip("192.168.86.37"), 28); // 192.168.86.32 - .47

        Assert.Equal(13, hosts.Count); // 14 hosts minus this PC
        Assert.Equal(Ip("192.168.86.33"), hosts[0]);
        Assert.Equal(Ip("192.168.86.46"), hosts[^1]);
        Assert.DoesNotContain(Ip("192.168.86.37"), hosts);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public void A_subnet_with_no_room_for_hosts_scans_nothing(int prefix)
    {
        Assert.Empty(NetworkScanner.HostsOf(Source, prefix));
    }

    [Fact]
    public async Task Devices_that_answer_are_listed_in_address_order_with_their_names_and_macs()
    {
        var probe = new FakeSweepProbe();
        probe.Answering.Add(Ip("192.168.86.44"));
        probe.Answering.Add(Ip("192.168.86.1"));
        var neighbors = new FakeNeighbors(("192.168.86.1", "AA-BB-CC-00-00-01"), ("192.168.86.44", "AA-BB-CC-00-00-44"));
        var scanner = Scanner(probe, neighbors, (ip, _) => Task.FromResult<string?>(ip.ToString().EndsWith(".44") ? "wdmycloud" : null));

        var found = await scanner.ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Equal(
            [new ScanResult(Ip("192.168.86.1"), "AA-BB-CC-00-00-01", null, true), new ScanResult(Ip("192.168.86.44"), "AA-BB-CC-00-00-44", "wdmycloud", true)],
            found);
        Assert.Equal(253, probe.Sent.Count);
        Assert.All(probe.Sent, s => Assert.Equal((Source, NetworkScanner.PingTimeout), (s.Source, s.Timeout)));
    }

    [Fact]
    public async Task A_device_that_ignores_pings_but_is_in_the_neighbour_table_is_listed()
    {
        var neighbors = new FakeNeighbors(("192.168.86.60", "11-22-33-44-55-66"));

        var found = await Scanner(new FakeSweepProbe(), neighbors).ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Equal([new ScanResult(Ip("192.168.86.60"), "11-22-33-44-55-66", null, false)], found);
    }

    [Theory]
    [InlineData("00-00-00-00-00-00")]
    [InlineData("FF-FF-FF-FF-FF-FF")]
    [InlineData("")]
    public async Task Neighbour_entries_without_a_real_mac_are_ignored(string mac)
    {
        var found = await Scanner(new FakeSweepProbe(), new FakeNeighbors(("192.168.86.60", mac))).ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Neighbours_outside_the_scanned_subnet_and_this_pc_are_ignored()
    {
        var neighbors = new FakeNeighbors(("10.0.0.5", "11-22-33-44-55-66"), ("192.168.86.42", "AA-AA-AA-AA-AA-AA"), ("192.168.86.0", "AA-AA-AA-AA-AA-AB"));

        var found = await Scanner(new FakeSweepProbe(), neighbors).ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Empty(found);
    }

    [Fact]
    public async Task A_name_lookup_that_throws_leaves_the_name_empty()
    {
        var probe = new FakeSweepProbe();
        probe.Answering.Add(Ip("192.168.86.44"));
        var scanner = Scanner(probe, names: (_, _) => throw new InvalidOperationException("dns broke"));

        var found = await scanner.ScanAsync(Source, 24, null, CancellationToken.None);

        Assert.Null(Assert.Single(found).Name);
    }

    [Fact]
    public async Task The_sweep_never_has_more_than_the_allowed_number_of_pings_out_and_reports_progress()
    {
        var probe = new FakeSweepProbe();
        var reports = new List<int>();

        await Scanner(probe).ScanAsync(Source, 24, new SyncProgress(reports), CancellationToken.None);

        Assert.True(probe.MaxInFlight <= NetworkScanner.PingConcurrency, $"{probe.MaxInFlight} pings were out at once");
        Assert.Equal(253, reports.Max());
        Assert.Equal(253, reports.Count);
    }

    sealed class SyncProgress(List<int> reports) : IProgress<int>
    {
        public void Report(int value) { lock (reports) reports.Add(value); }
    }

    [Fact]
    public async Task A_cancelled_scan_throws_cancellation_and_does_not_return_a_partial_list()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scanner(new FakeSweepProbe()).ScanAsync(Source, 24, null, cts.Token));
    }
}
