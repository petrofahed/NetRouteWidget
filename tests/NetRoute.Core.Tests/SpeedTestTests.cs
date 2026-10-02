using System.Net;
using System.Net.Sockets;
using System.Text;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class SpeedTestTests
{
    sealed class FixedProbe : ISpeedProbe
    {
        public List<IPAddress> Sources { get; } = new();
        public Task<double?> MeasureMbpsAsync(IPAddress source, CancellationToken ct)
        {
            Sources.Add(source);
            return Task.FromResult<double?>(source.ToString() == "192.168.42.11" ? 20.24 : null);
        }
    }

    [Fact]
    public async Task Runs_both_adapters_from_their_own_addresses()
    {
        var probe = new FixedProbe();
        var adapters = new DetectionResult(TestAdapters.Phone(), DetectionIssue.None, TestAdapters.Lan(), DetectionIssue.None);

        var result = await SpeedTest.RunAsync(probe, adapters);

        Assert.Equal(new SpeedTestResult(20.24, null), result);
        Assert.Equal(new[] { "192.168.42.11", "192.168.86.42" }, probe.Sources.Select(s => s.ToString()).Order());
        Assert.Equal("⚡ Phone 20.2 Mbit/s · LAN —", SpeedTest.Describe(result));
    }

    [Fact]
    public async Task Missing_adapter_is_not_measured()
    {
        var probe = new FixedProbe();

        var result = await SpeedTest.RunAsync(probe, DetectionResult.Empty);

        Assert.Equal(new SpeedTestResult(null, null), result);
        Assert.Empty(probe.Sources);
    }

    [Fact]
    public async Task Http_probe_measures_a_local_download_from_a_bound_source()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            Assert.True(await stream.ReadAsync(buffer) > 0); // request headers (small)
            var body = new byte[2 * 1024 * 1024];
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
        });

        var mbps = await new HttpSpeedProbe(new Uri($"http://127.0.0.1:{port}/down"), TimeSpan.FromSeconds(10))
            .MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None);

        await server;
        Assert.NotNull(mbps);
        Assert.True(mbps > 0);
    }

    [Fact]
    public async Task Http_probe_returns_null_when_nothing_answers()
    {
        var port = FreePort.Next();

        var mbps = await new HttpSpeedProbe(new Uri($"http://127.0.0.1:{port}/down"), TimeSpan.FromSeconds(3))
            .MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None);

        Assert.Null(mbps);
    }

    static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    static HttpSpeedProbe ProbeFor(int port, double seconds) =>
        new(new Uri($"http://127.0.0.1:{port}/down"), TimeSpan.FromSeconds(seconds));

    static async Task ReadRequestAsync(NetworkStream stream) =>
        Assert.True(await stream.ReadAsync(new byte[4096]) > 0);

    static async Task StallAsync(CancellationToken token)
    {
        try { await Task.Delay(Timeout.Infinite, token); }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Http_probe_times_out_when_the_body_stalls()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5000000\r\n\r\n"));
            await stream.WriteAsync(new byte[1000]);
            await StallAsync(stop.Token);
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        double? mbps;
        try { mbps = await ProbeFor(port, 2).MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { stop.Cancel(); await server; }

        Assert.Null(mbps);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Http_probe_stops_reading_at_the_byte_cap()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 50000000\r\n\r\n"));
            try { await stream.WriteAsync(new byte[7_000_000]); } catch (IOException) { }
            // Declares 50 MB but never sends the rest: only a capped read can finish.
            await StallAsync(stop.Token);
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        double? mbps;
        try { mbps = await ProbeFor(port, 10).MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)); }
        finally { stop.Cancel(); await server; }

        Assert.NotNull(mbps);
        Assert.True(mbps > 0);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task Http_probe_does_not_follow_redirects()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        using var stop = new CancellationTokenSource();
        var connections = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref connections);
                    var stream = client.GetStream();
                    await ReadRequestAsync(stream);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
            }
            catch (OperationCanceledException) { }
        });

        double? mbps;
        try { mbps = await ProbeFor(port, 5).MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None); }
        finally { stop.Cancel(); await server; }

        Assert.Null(mbps);
        Assert.Equal(1, Volatile.Read(ref connections));
    }

    [Fact]
    public async Task Http_probe_returns_null_for_a_short_download_without_content_length()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"));
            await stream.WriteAsync(new byte[10 * 1024]);
        });

        var mbps = await ProbeFor(port, 5).MeasureMbpsAsync(IPAddress.Loopback, CancellationToken.None);

        await server;
        Assert.Null(mbps);
    }

    [Fact]
    public async Task Http_probe_propagates_caller_cancellation()
    {
        var (listener, port) = Listen();
        using var _ = listener;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await ReadRequestAsync(stream);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 5000000\r\n\r\n"));
            await stream.WriteAsync(new byte[1000]);
            await StallAsync(stop.Token);
        });
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ProbeFor(port, 30).MeasureMbpsAsync(IPAddress.Loopback, caller.Token).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { stop.Cancel(); await server; }
    }

    [Fact]
    public async Task Malformed_adapter_address_is_not_measured_and_does_not_stop_the_other_probe()
    {
        var probe = new FixedProbe();
        var adapters = new DetectionResult(TestAdapters.Phone(), DetectionIssue.None,
            TestAdapters.Lan() with { IPv4 = "not-an-ip" }, DetectionIssue.None);

        var result = await SpeedTest.RunAsync(probe, adapters);

        Assert.Equal(new SpeedTestResult(20.24, null), result);
        Assert.Equal(new[] { "192.168.42.11" }, probe.Sources.Select(s => s.ToString()));
    }
}
