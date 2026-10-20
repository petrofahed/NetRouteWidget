using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class TcpLatencyProbeTests
{
    // No loopback endpoint hangs on Windows: a closed port or a full accept backlog is refused after ~2 s of SYN
    // retries. These stand-ins never answer (until cancelled) or are refused at once; other targets connect for real.
    static readonly IPEndPoint Hanging = new(IPAddress.Loopback, 1);
    static readonly IPEndPoint Hanging2 = new(IPAddress.Loopback, 2);
    static readonly IPEndPoint Hanging3 = new(IPAddress.Loopback, 4);
    static readonly IPEndPoint Hanging4 = new(IPAddress.Loopback, 5);
    static readonly IPEndPoint Refused = new(IPAddress.Loopback, 3);

    static bool IsHanging(IPEndPoint target) =>
        target.Equals(Hanging) || target.Equals(Hanging2) || target.Equals(Hanging3) || target.Equals(Hanging4);

    static async ValueTask FakeConnect(Socket socket, IPEndPoint target, CancellationToken ct)
    {
        if (IsHanging(target)) await Task.Delay(Timeout.Infinite, ct);
        else if (target.Equals(Refused)) throw new SocketException((int)SocketError.ConnectionRefused);
        else await socket.ConnectAsync(target, ct);
    }

    [Fact]
    public async Task Measures_connect_time_to_a_listening_port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var probe = new TcpLatencyProbe((IPEndPoint)listener.LocalEndpoint, TimeSpan.FromSeconds(2));

            var ms = await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None);

            Assert.NotNull(ms);
            Assert.InRange(ms.Value, 1, 2000);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Returns_null_when_nothing_answers()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var closed = (IPEndPoint)listener.LocalEndpoint;
        listener.Stop();
        var probe = new TcpLatencyProbe(closed, TimeSpan.FromMilliseconds(500));

        Assert.Null(await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None));
    }

    [Fact]
    public async Task Tries_the_next_target_when_the_first_fails()
    {
        var gone = new TcpListener(IPAddress.Loopback, 0);
        gone.Start();
        var closed = (IPEndPoint)gone.LocalEndpoint;
        gone.Stop();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var probe = new TcpLatencyProbe([closed, (IPEndPoint)listener.LocalEndpoint], TimeSpan.FromMilliseconds(500));

            var ms = await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None);

            Assert.NotNull(ms);
            Assert.InRange(ms.Value, 1, 400); // the second target's own connect time, not the wait on the first
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_fast_second_target_wins_while_the_first_hangs()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var probe = new TcpLatencyProbe([Hanging, (IPEndPoint)listener.LocalEndpoint], TimeSpan.FromSeconds(2))
            {
                Connect = FakeConnect,
            };
            var stopwatch = Stopwatch.StartNew();

            var ms = await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None);

            Assert.NotNull(ms);
            Assert.InRange(ms.Value, 1, 400);
            Assert.InRange(stopwatch.ElapsedMilliseconds, 0, 1000); // no wait on the hanging first target
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_target_refused_at_once_does_not_end_the_race()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var probe = new TcpLatencyProbe([Refused, (IPEndPoint)listener.LocalEndpoint], TimeSpan.FromSeconds(2))
            {
                Connect = FakeConnect,
            };

            Assert.NotNull(await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Returns_null_within_one_timeout_when_every_target_hangs()
    {
        var probe = new TcpLatencyProbe([Hanging, Hanging2, Hanging3, Hanging4], TimeSpan.FromMilliseconds(500))
        {
            Connect = FakeConnect,
        };
        var stopwatch = Stopwatch.StartNew();

        var ms = await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Null(ms);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 400, 1500); // one deadline for the whole check, not 4 x 500 ms
    }

    [Fact]
    public async Task Caller_cancellation_still_throws()
    {
        var probe = new TcpLatencyProbe([Hanging, Hanging2], TimeSpan.FromSeconds(5)) { Connect = FakeConnect };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.MeasureAsync(IPAddress.Loopback, cts.Token));
    }

    [Fact]
    public async Task Losing_targets_are_cancelled_and_their_sockets_closed_before_the_result_returns()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var losers = new List<Socket>();
        try
        {
            var probe = new TcpLatencyProbe([Hanging, Hanging2, (IPEndPoint)listener.LocalEndpoint], TimeSpan.FromSeconds(2))
            {
                Connect = async (socket, target, ct) =>
                {
                    if (!IsHanging(target))
                    {
                        await socket.ConnectAsync(target, ct);
                        return;
                    }
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    finally
                    {
                        lock (losers) losers.Add(socket);
                    }
                },
            };

            Assert.NotNull(await probe.MeasureAsync(IPAddress.Loopback, CancellationToken.None));

            lock (losers)
            {
                Assert.Equal(2, losers.Count);
                Assert.All(losers, s => Assert.True(s.SafeHandle.IsClosed));
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}
