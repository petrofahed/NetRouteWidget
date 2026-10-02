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
}
