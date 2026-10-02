using System.Net;
using System.Net.Sockets;
using NetRoute.Core.Windows;

namespace NetRoute.Core.Tests;

public class TcpLatencyProbeTests
{
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
}
