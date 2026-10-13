using System.Net;
using System.Net.Sockets;
using NetRoute.Core.PathMap;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class TcpEndpointProbeTests
{
    [Theory]
    [InlineData(SocketError.TimedOut)]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.ConnectionRefused)]
    [InlineData(SocketError.ConnectionReset)]
    [InlineData(SocketError.ConnectionAborted)]
    [InlineData(SocketError.HostDown)]
    public void The_network_not_answering_is_a_lost_check(SocketError error)
    {
        Assert.Equal(ProbeOutcome.NoReply, TcpEndpointProbe.Classify(error));
    }

    [Theory]
    [InlineData(SocketError.AddressAlreadyInUse)]
    [InlineData(SocketError.NoBufferSpaceAvailable)]
    [InlineData(SocketError.TooManyOpenSockets)]
    [InlineData(SocketError.AddressNotAvailable)]
    [InlineData(SocketError.AccessDenied)]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.SocketError)]
    public void A_problem_on_this_pc_is_unavailable_not_loss(SocketError error)
    {
        Assert.Equal(ProbeOutcome.Unavailable, TcpEndpointProbe.Classify(error));
    }

    [Fact]
    public async Task Connecting_to_a_listener_is_a_reply_with_a_latency()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var probe = new TcpEndpointProbe([(IPEndPoint)listener.LocalEndpoint], TimeSpan.FromSeconds(2));

            var r = await probe.CheckAsync(IPAddress.Loopback, CancellationToken.None);

            Assert.Equal(ProbeOutcome.Reply, r.Outcome);
            Assert.InRange(r.RttMs!.Value, 1, 2000);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task A_refused_connection_is_a_lost_check()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop(); // nothing listens there any more

        var r = await new TcpEndpointProbe([endpoint], TimeSpan.FromSeconds(2)).CheckAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Equal(ProbeOutcome.NoReply, r.Outcome);
    }

    [Fact]
    public async Task A_source_address_this_pc_does_not_have_is_unavailable()
    {
        var probe = new TcpEndpointProbe([new IPEndPoint(IPAddress.Loopback, 9)], TimeSpan.FromSeconds(2));

        var r = await probe.CheckAsync(IPAddress.Parse("203.0.113.7"), CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task A_cancelled_check_is_unavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var probe = new TcpEndpointProbe([new IPEndPoint(IPAddress.Loopback, 9)], TimeSpan.FromSeconds(2));

        var r = await probe.CheckAsync(IPAddress.Loopback, cts.Token);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }

    [Fact]
    public async Task One_unavailable_attempt_without_a_reply_keeps_the_whole_check_unavailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var refused = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        // the first target is refused (a real non-answer); the check still cannot be called lost if a later attempt could not run,
        // so use a target on a family the source address cannot reach
        var probe = new TcpEndpointProbe([refused, new IPEndPoint(IPAddress.IPv6Loopback, 9)], TimeSpan.FromSeconds(2));

        var r = await probe.CheckAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Unavailable, r.Outcome);
    }
}
